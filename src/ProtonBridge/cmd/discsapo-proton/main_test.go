package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"protonvpn-wg-confgen/internal/api"
	"protonvpn-wg-confgen/internal/config"
)

func TestPrivateProtocolRefreshSelectGenerate(t *testing.T) {
	certificateRequests := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/vpn/v2":
			fmt.Fprint(w, `{"Code":1000,"VPN":{"MaxTier":0}}`)
		case "/auth/refresh":
			var body map[string]any
			_ = json.NewDecoder(r.Body).Decode(&body)
			if body["RefreshToken"] != "old-refresh" {
				t.Error("refresh token not supplied")
			}
			fmt.Fprint(w, `{"Code":1000,"AccessToken":"new-access","RefreshToken":"new-refresh","UID":"test-uid"}`)
		case "/vpn/v1/logicals":
			if r.Header.Get("Authorization") != "Bearer new-access" || r.Header.Get("x-pm-uid") != "test-uid" {
				t.Error("rotated session not used")
			}
			fmt.Fprint(w, `{"Code":1000,"LogicalServers":[
    {"Name":"NL-FREE","ExitCountry":"NL","Tier":0,"Status":1,"Score":5,"Servers":[{"Status":1,"EntryIP":"192.0.2.1","X25519PublicKey":"AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE="}]},
    {"Name":"US-PLUS","ExitCountry":"US","Tier":2,"Status":1,"Score":1,"Servers":[{"Status":1,"EntryIP":"192.0.2.2","X25519PublicKey":"AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE="}]}
   ]}`)
		case "/vpn/v1/certificate":
			certificateRequests++
			var body map[string]any
			_ = json.NewDecoder(r.Body).Decode(&body)
			if _, exists := body["Mode"]; exists {
				t.Error("certificate must be session-only")
			}
			if !strings.Contains(fmt.Sprint(body["ClientPublicKey"]), "BEGIN PUBLIC KEY") {
				t.Error("missing generated public key")
			}
			fmt.Fprint(w, `{"Code":1000,"ExpirationTime":2000000000}`)
		default:
			t.Errorf("unexpected path %s", r.URL.Path)
			w.WriteHeader(404)
		}
	}))
	defer server.Close()
	input := `{"op":"generate","freeOnly":true}
{"op":"restore","session":{"AccessToken":"old","RefreshToken":"old-refresh","UID":"test-uid"}}
{"op":"countries","freeOnly":false}
{"op":"generate","country":"US","freeOnly":false}
{"op":"generate","country":"","freeOnly":false}
`
	var output bytes.Buffer
	run(strings.NewReader(input), &output, server.URL)
	var responses []map[string]any
	scanner := bufio.NewScanner(&output)
	for scanner.Scan() {
		var v map[string]any
		if err := json.Unmarshal(scanner.Bytes(), &v); err != nil {
			t.Fatal("non-protocol output")
		}
		responses = append(responses, v)
	}
	if len(responses) != 5 {
		t.Fatalf("response count %d", len(responses))
	}
	if responses[0]["code"] != "session" || responses[1]["type"] != "session" {
		t.Fatal("authentication gate failed")
	}
	countries := responses[2]["countries"].([]any)
	if len(countries) != 1 || countries[0] != "NL" || responses[2]["canUsePaidServers"] != false {
		t.Fatal("free countries incorrect")
	}
	if responses[3]["code"] != "servers" {
		t.Fatal("ineligible country accepted")
	}
	generated := responses[4]
	conf, _ := generated["configuration"].(string)
	if generated["country"] != "NL" || !strings.Contains(conf, "Endpoint = 192.0.2.1:51820") || !strings.Contains(conf, "PrivateKey = ") || !strings.Contains(conf, "DNS = 10.2.0.1") {
		t.Fatal("invalid generated configuration")
	}
	if certificateRequests != 1 {
		t.Fatal("certificate created before confirming server availability")
	}
}

func TestEligibilityExcludesSpecialAndOfflineEndpoints(t *testing.T) {
	base := api.LogicalServer{Name: "valid", Status: 1, Tier: 0, ExitCountry: "JP", Servers: []api.PhysicalServer{{Status: 1, EntryIP: "192.0.2.1", X25519PublicKey: "key"}}}
	tor := base
	tor.Features = api.FeatureTor
	core := base
	core.Features = api.FeatureSecureCore
	offline := base
	offline.Servers = []api.PhysicalServer{{Status: 0, EntryIP: "192.0.2.1", X25519PublicKey: "key"}}
	noKey := base
	noKey.Servers = []api.PhysicalServer{{Status: 1, EntryIP: "192.0.2.1"}}
	if result := eligible([]api.LogicalServer{tor, core, offline, noKey, base}, true, "JP", 0); len(result) != 1 || result[0].Name != "valid" {
		t.Fatal("invalid endpoint eligible")
	}
}

func TestPaidEntitlementAndFreeFilter(t *testing.T) {
	free := api.LogicalServer{Status: 1, Tier: 0, Servers: []api.PhysicalServer{{Status: 1, EntryIP: "192.0.2.1", X25519PublicKey: "key"}}}
	paid := free
	paid.Tier = api.TierPlus
	servers := []api.LogicalServer{free, paid}
	if len(eligible(servers, false, "", api.TierPlus)) != 2 {
		t.Fatal("paid account lost access")
	}
	if len(eligible(servers, true, "", api.TierPlus)) != 1 {
		t.Fatal("free filter ignored")
	}
	if len(eligible(servers, false, "", 0)) != 1 {
		t.Fatal("free account can select paid server")
	}
}

func TestUnknownAccountEntitlementFailsClosed(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/vpn/v2" {
			t.Error("must not fetch servers or create certificate with unknown entitlement")
			w.WriteHeader(500)
			return
		}
		fmt.Fprint(w, `{"Code":1000,"VPN":{}}`)
	}))
	defer server.Close()
	b := bridge{cfg: &config.Config{APIURL: server.URL}, session: &api.Session{AccessToken: "test", UID: "test"}}
	if _, err := b.handle(request{Op: "generate"}); err == nil {
		t.Fatal("missing tier accepted")
	}
}

func TestErrorDoesNotExposeAPISecrets(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprint(w, `{"Code":9001,"Error":"secret-do-not-expose","Details":{"HumanVerificationToken":"secret-do-not-expose"}}`)
	}))
	defer server.Close()
	var out bytes.Buffer
	run(strings.NewReader("{\"op\":\"restore\",\"session\":{\"UID\":\"id\",\"RefreshToken\":\"secret-do-not-expose\"}}\n"), &out, server.URL)
	if strings.Contains(out.String(), "secret-do-not-expose") || !strings.Contains(out.String(), `"type":"error"`) {
		t.Fatal("API error data escaped")
	}
}
