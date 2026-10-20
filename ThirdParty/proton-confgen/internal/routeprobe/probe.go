package routeprobe

import (
	"context"
	"crypto/rand"
	"crypto/tls"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strings"
	"sync"
	"time"
)

const maxResponseBytes = 32 * 1024

type Observation struct {
	Source    string `json:"source"`
	IP        string `json:"ip"`
	Country   string `json:"country,omitempty"`
	LatencyMS int64  `json:"latencyMs"`
}

type Result struct {
	Success      bool          `json:"success"`
	Observations []Observation `json:"observations,omitempty"`
	DiscordOK    bool          `json:"discordOk"`
	DiscordMS    int64         `json:"discordMs,omitempty"`
	Error        string        `json:"error,omitempty"`
}

type Endpoint struct {
	Name  string
	URL   string
	Parse func([]byte) (string, string, error)
}

var defaultEndpoints = []Endpoint{
	{Name: "cloudflare", URL: "https://www.cloudflare.com/cdn-cgi/trace", Parse: parseCloudflare},
	{Name: "country-is", URL: "https://api.country.is/", Parse: parseCountryIS},
	{Name: "ipify", URL: "https://api64.ipify.org/?format=json", Parse: parseIPJSON},
}

const defaultDiscordURL = "https://discord.com/api/v9/gateway"

func NewHTTPClient() *http.Client {
	return newHTTPClientForNetwork("tcp")
}

func newHTTPClientForNetwork(network string) *http.Client {
	dialer := &net.Dialer{Timeout: 3500 * time.Millisecond, KeepAlive: 15 * time.Second}
	transport := &http.Transport{
		Proxy: nil,
		DialContext: func(ctx context.Context, _ string, address string) (net.Conn, error) {
			return dialer.DialContext(ctx, network, address)
		},
		ForceAttemptHTTP2:     true,
		TLSHandshakeTimeout:   3500 * time.Millisecond,
		ResponseHeaderTimeout: 3500 * time.Millisecond,
		ExpectContinueTimeout: time.Second,
		DisableCompression:    true,
		TLSClientConfig:       &tls.Config{MinVersion: tls.VersionTLS12},
	}
	return &http.Client{
		Transport: transport,
		Timeout:   7 * time.Second,
		CheckRedirect: func(_ *http.Request, _ []*http.Request) error {
			return errors.New("redirect recusado")
		},
	}
}

func Run(ctx context.Context) Result {
	type familyResult struct {
		family string
		result Result
	}
	results := make(chan familyResult, 2)
	for _, family := range []string{"tcp4", "tcp6"} {
		family := family
		go func() {
			results <- familyResult{family: "ipv" + family[3:], result: Probe(ctx, newHTTPClientForNetwork(family), defaultEndpoints, defaultDiscordURL)}
		}()
	}

	combined := Result{}
	activeFamilies := 0
	for range 2 {
		family := <-results
		if len(family.result.Observations) == 0 {
			continue
		}
		activeFamilies++
		for _, observation := range family.result.Observations {
			observation.Source = family.family + "/" + observation.Source
			combined.Observations = append(combined.Observations, observation)
		}
		combined.DiscordOK = combined.DiscordOK || family.result.DiscordOK
		if family.result.DiscordMS > combined.DiscordMS {
			combined.DiscordMS = family.result.DiscordMS
		}
	}
	if activeFamilies == 0 {
		combined.Error = "nenhuma familia de rede devolveu um IP publico valido"
		return combined
	}
	combined.Success = true
	if !combined.DiscordOK {
		combined.Error = "Discord HTTPS inacessivel pela rota"
	}
	return combined
}

func Probe(ctx context.Context, client *http.Client, endpoints []Endpoint, discordURL string) Result {
	if client == nil {
		client = NewHTTPClient()
	}
	nonce := makeNonce()
	type observationResult struct {
		observation Observation
		err         error
	}
	results := make(chan observationResult, len(endpoints))
	var wg sync.WaitGroup
	for _, endpoint := range endpoints {
		endpoint := endpoint
		wg.Add(1)
		go func() {
			defer wg.Done()
			started := time.Now()
			body, err := getLimited(ctx, client, withNonce(endpoint.URL, nonce))
			if err != nil {
				results <- observationResult{err: err}
				return
			}
			ip, country, err := endpoint.Parse(body)
			if err != nil {
				results <- observationResult{err: err}
				return
			}
			if !IsPublicIP(ip) {
				results <- observationResult{err: fmt.Errorf("resposta sem IP publico")}
				return
			}
			results <- observationResult{observation: Observation{
				Source: endpoint.Name, IP: ip, Country: normalizeCountry(country), LatencyMS: time.Since(started).Milliseconds(),
			}}
		}()
	}
	wg.Wait()
	close(results)

	observations := make([]Observation, 0, len(endpoints))
	for result := range results {
		if result.err == nil {
			observations = append(observations, result.observation)
		}
	}

	discordStarted := time.Now()
	_, discordErr := getLimited(ctx, client, withNonce(discordURL, nonce))
	discordMS := time.Since(discordStarted).Milliseconds()
	if len(observations) == 0 {
		return Result{DiscordOK: discordErr == nil, DiscordMS: discordMS, Error: "nenhuma fonte devolveu um IP publico valido"}
	}
	if discordErr != nil {
		return Result{Success: true, Observations: observations, DiscordMS: discordMS, Error: "Discord HTTPS inacessivel pela rota"}
	}
	return Result{Success: true, Observations: observations, DiscordOK: true, DiscordMS: discordMS}
}

func getLimited(ctx context.Context, client *http.Client, rawURL string) ([]byte, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, http.NoBody)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "application/json,text/plain;q=0.9")
	req.Header.Set("Cache-Control", "no-store, no-cache, max-age=0")
	req.Header.Set("Pragma", "no-cache")
	req.Header.Set("User-Agent", "GoLiveBypass-RouteProbe/1")
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, maxResponseBytes+1))
	if err != nil {
		return nil, err
	}
	if len(body) > maxResponseBytes {
		return nil, errors.New("resposta excedeu o limite")
	}
	return body, nil
}

func withNonce(rawURL, nonce string) string {
	u, err := url.Parse(rawURL)
	if err != nil {
		return rawURL
	}
	q := u.Query()
	q.Set("golive_nonce", nonce)
	u.RawQuery = q.Encode()
	return u.String()
}

func makeNonce() string {
	buf := make([]byte, 12)
	if _, err := rand.Read(buf); err != nil {
		return fmt.Sprintf("%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(buf)
}

func parseCloudflare(body []byte) (string, string, error) {
	var ip, country string
	for _, line := range strings.Split(string(body), "\n") {
		key, value, ok := strings.Cut(strings.TrimSpace(line), "=")
		if !ok {
			continue
		}
		switch key {
		case "ip":
			ip = strings.TrimSpace(value)
		case "loc":
			country = strings.TrimSpace(value)
		}
	}
	if ip == "" {
		return "", "", errors.New("campo ip ausente")
	}
	return ip, country, nil
}

func parseCountryIS(body []byte) (string, string, error) {
	var value struct {
		IP      string `json:"ip"`
		Country string `json:"country"`
	}
	if err := json.Unmarshal(body, &value); err != nil {
		return "", "", err
	}
	if value.IP == "" {
		return "", "", errors.New("campo ip ausente")
	}
	return value.IP, value.Country, nil
}

func parseIPJSON(body []byte) (string, string, error) {
	var value struct {
		IP string `json:"ip"`
	}
	if err := json.Unmarshal(body, &value); err != nil {
		return "", "", err
	}
	if value.IP == "" {
		return "", "", errors.New("campo ip ausente")
	}
	return value.IP, "", nil
}

func normalizeCountry(country string) string {
	country = strings.ToUpper(strings.TrimSpace(country))
	if len(country) != 2 {
		return ""
	}
	for _, r := range country {
		if r < 'A' || r > 'Z' {
			return ""
		}
	}
	return country
}

var nonPublicPrefixes = []netip.Prefix{
	netip.MustParsePrefix("100.64.0.0/10"),
	netip.MustParsePrefix("192.0.2.0/24"),
	netip.MustParsePrefix("198.18.0.0/15"),
	netip.MustParsePrefix("198.51.100.0/24"),
	netip.MustParsePrefix("203.0.113.0/24"),
	netip.MustParsePrefix("2001:db8::/32"),
}

func IsPublicIP(value string) bool {
	addr, err := netip.ParseAddr(strings.TrimSpace(value))
	if err != nil || !addr.IsGlobalUnicast() || addr.IsPrivate() || addr.IsLoopback() || addr.IsLinkLocalUnicast() || addr.IsUnspecified() {
		return false
	}
	for _, prefix := range nonPublicPrefixes {
		if prefix.Contains(addr) {
			return false
		}
	}
	return true
}
