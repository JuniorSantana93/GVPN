package routeprobe

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestParsersAndPublicIP(t *testing.T) {
	ip, country, err := parseCloudflare([]byte("fl=x\nip=8.8.8.8\nloc=us\n"))
	if err != nil || ip != "8.8.8.8" || country != "us" {
		t.Fatalf("cloudflare parse = %q %q %v", ip, country, err)
	}
	ip, country, err = parseCountryIS([]byte(`{"ip":"1.1.1.1","country":"AU"}`))
	if err != nil || ip != "1.1.1.1" || country != "AU" {
		t.Fatalf("country.is parse = %q %q %v", ip, country, err)
	}

	for _, value := range []string{"8.8.8.8", "2606:4700:4700::1111"} {
		if !IsPublicIP(value) {
			t.Fatalf("expected public: %s", value)
		}
	}
	for _, value := range []string{"127.0.0.1", "10.0.0.1", "100.64.0.1", "169.254.1.1", "192.0.2.1", "203.0.113.4", "::1", "2001:db8::1", "invalid"} {
		if IsPublicIP(value) {
			t.Fatalf("expected non-public: %s", value)
		}
	}
}

func TestProbeHonorsContextTimeout(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		time.Sleep(100 * time.Millisecond)
		fmt.Fprint(w, `{"ip":"8.8.8.8","country":"US"}`)
	}))
	defer server.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Millisecond)
	defer cancel()
	result := Probe(ctx, server.Client(), []Endpoint{{Name: "slow", URL: server.URL, Parse: parseCountryIS}}, server.URL)
	if result.Success || !strings.Contains(result.Error, "nenhuma fonte") {
		t.Fatalf("expected timeout failure, got %+v", result)
	}
}

func TestNewHTTPClientRejectsUntrustedTLS(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprint(w, `{"ip":"8.8.8.8","country":"US"}`)
	}))
	defer server.Close()
	result := Probe(context.Background(), NewHTTPClient(), []Endpoint{{Name: "tls", URL: server.URL, Parse: parseCountryIS}}, server.URL)
	if result.Success {
		t.Fatalf("untrusted TLS certificate must be rejected: %+v", result)
	}
}

func TestProbeFallsBackAndChecksDiscord(t *testing.T) {
	var sawNonce bool
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("golive_nonce") != "" && r.Header.Get("Cache-Control") != "" {
			sawNonce = true
		}
		switch r.URL.Path {
		case "/bad":
			http.Error(w, "no", http.StatusServiceUnavailable)
		case "/ip":
			fmt.Fprint(w, `{"ip":"8.8.8.8","country":"US"}`)
		case "/discord":
			fmt.Fprint(w, `{"url":"wss://gateway.discord.gg"}`)
		}
	}))
	defer server.Close()

	result := Probe(context.Background(), server.Client(), []Endpoint{
		{Name: "bad", URL: server.URL + "/bad", Parse: parseIPJSON},
		{Name: "good", URL: server.URL + "/ip", Parse: parseCountryIS},
	}, server.URL+"/discord")
	if !result.Success || !result.DiscordOK || len(result.Observations) != 1 || result.Observations[0].IP != "8.8.8.8" || !sawNonce {
		t.Fatalf("unexpected result: %+v nonce=%v", result, sawNonce)
	}
}

func TestProbeRejectsRedirectAndOversizedBody(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/redirect":
			http.Redirect(w, r, "/ip", http.StatusFound)
		case "/large":
			fmt.Fprint(w, strings.Repeat("x", maxResponseBytes+1))
		case "/discord":
			fmt.Fprint(w, "ok")
		}
	}))
	defer server.Close()
	client := server.Client()
	client.CheckRedirect = NewHTTPClient().CheckRedirect

	result := Probe(context.Background(), client, []Endpoint{
		{Name: "redirect", URL: server.URL + "/redirect", Parse: parseIPJSON},
		{Name: "large", URL: server.URL + "/large", Parse: parseIPJSON},
	}, server.URL+"/discord")
	if result.Success || !strings.Contains(result.Error, "nenhuma fonte") {
		t.Fatalf("expected failure, got %+v", result)
	}
}

func TestProbeDistinguishesDiscordFailureFromMissingIP(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/ip" {
			fmt.Fprint(w, `{"ip":"8.8.8.8","country":"US"}`)
			return
		}
		http.Error(w, "down", http.StatusServiceUnavailable)
	}))
	defer server.Close()
	result := Probe(context.Background(), server.Client(), []Endpoint{{Name: "ip", URL: server.URL + "/ip", Parse: parseCountryIS}}, server.URL+"/discord")
	if !result.Success || result.DiscordOK || !strings.Contains(result.Error, "Discord HTTPS") {
		t.Fatalf("expected usable IP with Discord-specific failure, got %+v", result)
	}
}

func TestNewHTTPClientIgnoresProxyEnvironment(t *testing.T) {
	t.Setenv("HTTPS_PROXY", "http://127.0.0.1:9")
	client := NewHTTPClient()
	transport, ok := client.Transport.(*http.Transport)
	if !ok || transport.Proxy != nil {
		t.Fatalf("route probe must not use environment proxies")
	}
}
