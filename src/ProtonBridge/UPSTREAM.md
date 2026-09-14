# discSapo Proton bridge

Separate GPL-3.0 helper derived from https://github.com/hatemosphere/protonvpn-wg-confgen
at commit 32e869e3dbb48f135dc47a6fa2bb68834b2ea2ce. Original license: LICENSE.
Modifications (2026-09-11): private NDJSON IPC entry point; TOTP callback;
in-memory configuration generation; server eligibility filtering; compatibility
headers updated to Linux client 4.18.1 from ProtonVPN/proton-vpn-gtk-app stable/versions.yml.
Upstream internal code and tests are retained. This is an unofficial integration.
Version 0.5.1: authenticated /vpn/v2 MaxTier is checked before listing or selecting
servers. Missing entitlement fails closed; the UI cannot enable paid servers for
a free account. Regression tests cover free accounts with the free-only filter off.

Build: Go 1.26.6, `go build -trimpath -o proton-bridge.exe ./cmd/discsapo-proton`.
Run tests: `go test ./...`. Exact dependencies/checksums: go.mod and go.sum.
The app build ships this complete helper source beside its executable.
Passwords are never stored. Session data travels only on inherited private pipes;
the desktop app stores tokens using Windows DPAPI. Upstream console diagnostics
are discarded. CAPTCHA and security keys fall back to the embedded official portal.
