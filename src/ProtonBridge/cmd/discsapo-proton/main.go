// discSapo adaptation, GPL-3.0; see ../../LICENSE and ../../UPSTREAM.md.
package main

import (
	"bufio"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"sort"
	"strings"
	"time"

	"github.com/ProtonVPN/go-vpn-lib/ed25519"
	"protonvpn-wg-confgen/internal/api"
	"protonvpn-wg-confgen/internal/auth"
	"protonvpn-wg-confgen/internal/config"
	"protonvpn-wg-confgen/internal/constants"
	"protonvpn-wg-confgen/internal/vpn"
	"protonvpn-wg-confgen/internal/wireguard"
)

type request struct {
	Op       string       `json:"op"`
	Username string       `json:"username"`
	Password string       `json:"password"`
	Code     string       `json:"code"`
	Country  string       `json:"country"`
	FreeOnly bool         `json:"freeOnly"`
	Session  *api.Session `json:"session"`
}
type bridge struct {
	input   *bufio.Scanner
	output  *json.Encoder
	cfg     *config.Config
	session *api.Session
}

func main() {
	out := os.Stdout
	// Libraries print diagnostics, sometimes including API responses. Never expose them.
	sink, err := os.OpenFile(os.DevNull, os.O_WRONLY, 0)
	if err != nil {
		return
	}
	defer sink.Close()
	os.Stdout, os.Stderr = sink, sink
	run(os.Stdin, out, constants.DefaultAPIURL)
}
func run(input io.Reader, output io.Writer, apiURL string) {
	scanner := bufio.NewScanner(input)
	scanner.Buffer(make([]byte, 4096), 131072)
	b := &bridge{input: scanner, output: json.NewEncoder(output), cfg: &config.Config{
		APIURL: apiURL, NoSession: true, NoSave: true, Duration: "7d", DeviceName: "discSapo",
		DNSServers: []string{constants.DefaultDNSIPv4}, AllowedIPs: []string{constants.DefaultAllowedIPsIPv4},
	}}
	b.cfg.RequestTwoFactor = func() (string, error) {
		if err := b.output.Encode(map[string]any{"type": "twoFactor"}); err != nil {
			return "", err
		}
		var r request
		if !b.input.Scan() || json.Unmarshal(b.input.Bytes(), &r) != nil || r.Op != "twoFactor" || len(r.Code) != 6 || strings.Trim(r.Code, "0123456789") != "" {
			return "", errors.New("invalid two factor")
		}
		return r.Code, nil
	}
	for scanner.Scan() {
		var r request
		if json.Unmarshal(scanner.Bytes(), &r) != nil {
			_ = b.output.Encode(map[string]any{"type": "error", "code": "protocol"})
			continue
		}
		result, err := b.handle(r)
		if err != nil {
			result = map[string]any{"type": "error", "code": safeError(err)}
		}
		if b.output.Encode(result) != nil {
			return
		}
	}
}
func safeError(err error) string {
	s := strings.ToLower(err.Error())
	switch {
	case strings.Contains(s, "captcha"), strings.Contains(s, "9001"), strings.Contains(s, "2-password"), strings.Contains(s, "security key"):
		return "portalRequired"
	case strings.Contains(s, "5003"):
		return "clientVersion"
	case strings.Contains(s, "8002"), strings.Contains(s, "incorrect username or password"):
		return "credentials"
	case strings.Contains(s, "two factor"), strings.Contains(s, "2fa"):
		return "twoFactor"
	case strings.Contains(s, "session"):
		return "session"
	case strings.Contains(s, "no suitable"), strings.Contains(s, "no physical"):
		return "servers"
	default:
		return "requestFailed"
	}
}

// Exclude special multi-hop/Tor routes and physical endpoints that cannot speak WireGuard.
func eligible(servers []api.LogicalServer, free bool, country string, maxTier int) []api.LogicalServer {
	result := []api.LogicalServer{}
	for _, s := range servers {
		if s.Tier < 0 || s.Tier > maxTier || s.Features&(api.FeatureSecureCore|api.FeatureTor) != 0 || s.Status != 1 || (s.Tier != 0 && (free || s.Tier != api.TierPlus)) || (country != "" && s.ExitCountry != country) {
			continue
		}
		physical := []api.PhysicalServer{}
		for _, p := range s.Servers {
			if p.Status == 1 && p.EntryIP != "" && p.X25519PublicKey != "" {
				physical = append(physical, p)
			}
		}
		if len(physical) > 0 {
			s.Servers = physical
			result = append(result, s)
		}
	}
	return result
}
func (b *bridge) handle(r request) (map[string]any, error) {
	switch r.Op {
	case "login":
		b.session = nil
		if strings.TrimSpace(r.Username) == "" || r.Password == "" {
			return nil, errors.New("incorrect username or password")
		}
		b.cfg.Username, b.cfg.Password = r.Username, r.Password
		session, err := auth.NewClient(b.cfg).Authenticate()
		b.cfg.Password = ""
		if err != nil {
			return nil, err
		}
		b.session = session
		return map[string]any{"type": "session", "session": session}, nil
	case "restore":
		b.session = nil
		if r.Session == nil || r.Session.RefreshToken == "" || r.Session.UID == "" {
			return nil, errors.New("session invalid")
		}
		session, err := auth.RefreshSession(&http.Client{Timeout: 30 * time.Second}, b.cfg.APIURL, r.Session)
		if err != nil {
			return nil, errors.New("session expired")
		}
		if session.UID == "" {
			session.UID = r.Session.UID
		}
		if session.AccessToken == "" || session.RefreshToken == "" {
			return nil, errors.New("session invalid")
		}
		b.session = session
		return map[string]any{"type": "session", "session": session}, nil
	case "countries", "generate":
		if b.session == nil {
			return nil, errors.New("session required")
		}
		client := vpn.NewClient(b.cfg, b.session)
		maxTier, err := client.GetMaxTier()
		if err != nil {
			return nil, err
		}
		servers, err := client.GetServers()
		if err != nil {
			return nil, err
		}
		servers = eligible(servers, r.FreeOnly, r.Country, maxTier)
		if len(servers) == 0 {
			return nil, errors.New("no suitable servers")
		}
		if r.Op == "countries" {
			countries := []string{}
			seen := map[string]bool{}
			for _, s := range servers {
				if !seen[s.ExitCountry] {
					countries = append(countries, s.ExitCountry)
					seen[s.ExitCountry] = true
				}
			}
			sort.Strings(countries)
			return map[string]any{"type": "countries", "countries": countries, "canUsePaidServers": maxTier >= api.TierPlus}, nil
		}
		sort.SliceStable(servers, func(i, j int) bool {
			if servers[i].Score == servers[j].Score {
				return servers[i].Load < servers[j].Load
			}
			return servers[i].Score < servers[j].Score
		})
		selected := servers[0]
		physical := vpn.GetBestPhysicalServer(&selected)
		key, err := ed25519.NewKeyPair()
		if err != nil {
			return nil, err
		}
		cert, err := client.GetCertificate(key)
		if err != nil {
			return nil, err
		}
		conf, err := wireguard.NewConfigGenerator(b.cfg).Build(&selected, physical, key.ToX25519Base64(), cert)
		if err != nil {
			return nil, err
		}
		expires := time.Now().Add(7 * 24 * time.Hour).Unix()
		if cert.ExpirationTime > 0 && cert.ExpirationTime < expires {
			expires = cert.ExpirationTime
		}
		if expires <= time.Now().Add(time.Minute).Unix() {
			return nil, errors.New("session certificate expired")
		}
		return map[string]any{"type": "configuration", "configuration": conf, "server": selected.Name, "country": selected.ExitCountry, "expires": expires}, nil
	default:
		return nil, errors.New("unknown operation")
	}
}
