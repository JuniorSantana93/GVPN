package main

import (
	"encoding/json"
	"strings"
	"testing"

	"protonvpn-wg-confgen/internal/api"
	"protonvpn-wg-confgen/internal/config"
	"protonvpn-wg-confgen/internal/constants"
	"protonvpn-wg-confgen/internal/vpn"
)

func catalogTestServer(name, country, city string, tier int, load int, score float64) api.LogicalServer {
	return api.LogicalServer{
		Name:        name,
		ExitCountry: country,
		City:        city,
		Tier:        tier,
		Load:        load,
		Score:       score,
		Status:      constants.StatusOnline,
		Servers:     []api.PhysicalServer{{Status: constants.StatusOnline}},
	}
}

func TestEligibleRouteCatalogFiltersExcludesAndSorts(t *testing.T) {
	offline := catalogTestServer("offline", "NL", "Amsterdam", api.TierFree, 1, 0)
	offline.Status = 0
	servers := []api.LogicalServer{
		catalogTestServer("US#2", "US", "New York", api.TierFree, 20, 2),
		catalogTestServer("NL#2", "NL", "Amsterdam", api.TierFree, 40, 2),
		catalogTestServer("US#1", "US", "New York", api.TierFree, 30, 1),
		catalogTestServer("NL#1", "NL", "Amsterdam", api.TierFree, 50, 1),
		catalogTestServer("US#premium", "US", "New York", api.TierPlus, 1, 0.5),
		catalogTestServer("BR#1", "BR", "São Paulo", api.TierFree, 10, 1),
		offline,
		{Name: "empty", ExitCountry: "NL", Tier: api.TierFree, Status: constants.StatusOnline},
	}

	free := &config.Config{
		FreeOnly:          true,
		ExcludedCountries: []string{"BR"},
		ExcludedServers:   []string{"US#2"},
	}
	got := eligibleRouteCatalog(free, servers)
	if len(got) != 3 {
		t.Fatalf("got %d routes, want 3: %+v", len(got), got)
	}
	wantNames := []string{"NL#1", "NL#2", "US#1"}
	for index, want := range wantNames {
		if got[index].Server != want {
			t.Fatalf("route %d = %q, want %q; got %+v", index, got[index].Server, want, got)
		}
		if got[index].Tier != "Free" {
			t.Fatalf("route %q tier = %q, want Free", got[index].Server, got[index].Tier)
		}
	}

	premium := eligibleRouteCatalog(&config.Config{}, servers)
	if len(premium) != 1 || premium[0].Server != "US#premium" || premium[0].Tier != "Plus" {
		t.Fatalf("premium catalog = %+v, want only US#premium", premium)
	}
}

func TestAttachRouteCatalogPingsKeepsOnlyValidMeasurements(t *testing.T) {
	entries := []routeCatalogEntry{
		{Server: "NL#1"},
		{Server: "US#1"},
		{Server: "DE#1"},
		{Server: "CH#1"},
	}
	attachRouteCatalogPings(entries, map[string]int{
		"NL#1": 42,
		"US#1": 0,
		"DE#1": 999,
		"CH#1": -4,
		"NO#1": 18,
	})

	if entries[0].PingMs != 42 {
		t.Fatalf("valid ping = %d, want 42", entries[0].PingMs)
	}
	for _, entry := range entries[1:] {
		if entry.PingMs != 0 {
			t.Fatalf("invalid or absent ping for %s = %d, want 0", entry.Server, entry.PingMs)
		}
	}
}


func TestCatalogProgressAnnouncesRowsBeforeMeasurements(t *testing.T) {
	entries := []routeCatalogEntry{
		{Server: "NL#1", Country: "NL", City: "Amsterdam", Tier: "Free", Load: 14, Score: 1.25},
		{Server: "US#1", Country: "US", City: "New York", Tier: "Free", Load: 0, Score: 0},
	}
	var events []map[string]any
	sink := func(event any) {
		data, err := json.Marshal(event)
		if err != nil {
			t.Fatalf("marshal progress event: %v", err)
		}
		parsed := map[string]any{}
		if err := json.Unmarshal(data, &parsed); err != nil {
			t.Fatalf("unmarshal progress event %s: %v", data, err)
		}
		events = append(events, parsed)
	}
	progress := newCatalogProgress(entries, sink)

	progress.announce(entries)
	if len(events) != 3 {
		t.Fatalf("got %d announcement events, want the header plus one per route: %+v", len(events), events)
	}
	header := events[0]
	if header["phase"] != "catalog" || header["total"] != float64(2) ||
		header["tested"] != float64(0) || header["succeeded"] != float64(0) {
		t.Fatalf("unexpected header event: %+v", header)
	}
	if _, announced := header["server"]; announced {
		t.Fatalf("header event announced a server: %+v", header)
	}

	announced := events[2]
	if announced["phase"] != "catalog" || announced["server"] != "US#1" || announced["country"] != "US" ||
		announced["city"] != "New York" || announced["tier"] != "Free" || announced["status"] != "success" {
		t.Fatalf("unexpected announced route: %+v", announced)
	}
	// A zero load and a zero score must still reach the consumer, and an
	// announcement must not claim a measurement that has not run yet.
	if announced["load"] != float64(0) || announced["score"] != float64(0) ||
		announced["tested"] != float64(0) || announced["succeeded"] != float64(0) {
		t.Fatalf("announcement invented or dropped a metric: %+v", announced)
	}
	if ping, present := announced["pingMs"]; present {
		t.Fatalf("announcement invented a ping: %v", ping)
	}

	progress.record(vpn.PingProgressEvent{Server: "NL#1", PingMs: 42, Status: "success"})
	progress.record(vpn.PingProgressEvent{Server: "EXCLUDED#9", PingMs: 12, Status: "success"})
	progress.record(vpn.PingProgressEvent{Server: "US#1", PingMs: 999, Status: "failed"})

	if len(events) != 5 {
		t.Fatalf("got %d events, want two measurement updates plus the three announcements: %+v", len(events), events)
	}
	measured := events[3]
	if measured["server"] != "NL#1" || measured["pingMs"] != float64(42) || measured["status"] != "success" ||
		measured["country"] != "NL" || measured["city"] != "Amsterdam" || measured["tier"] != "Free" ||
		measured["load"] != float64(14) || measured["score"] != 1.25 ||
		measured["total"] != float64(2) || measured["tested"] != float64(1) || measured["succeeded"] != float64(1) {
		t.Fatalf("unexpected measurement update: %+v", measured)
	}
	failed := events[4]
	if failed["server"] != "US#1" || failed["status"] != "failed" ||
		failed["total"] != float64(2) || failed["tested"] != float64(2) || failed["succeeded"] != float64(1) ||
		failed["country"] != "US" || failed["city"] != "New York" ||
		failed["load"] != float64(0) || failed["score"] != float64(0) {
		t.Fatalf("unexpected failed measurement: %+v", failed)
	}
	if ping, invented := failed["pingMs"]; invented {
		t.Fatalf("failed measurement invented a latency: %v", ping)
	}
}

func TestCatalogProgressStaysInertWithoutSink(t *testing.T) {
	entries := []routeCatalogEntry{{Server: "NL#1", Country: "NL", City: "Amsterdam", Tier: "Free", Load: 14, Score: 1.25}}
	progress := newCatalogProgress(entries, nil)
	if progress != nil {
		t.Fatalf("catalog progress = %+v, want no emitter without a sink", progress)
	}
	progress.announce(entries)
	progress.record(vpn.PingProgressEvent{Server: "NL#1", PingMs: 42, Status: "success"})
}

func TestRouteCatalogJSONContainsOnlyPublicMetadata(t *testing.T) {
	entries := []routeCatalogEntry{
		{Server: "NL#1", Country: "NL", City: "Amsterdam", Tier: "Free", Load: 14, Score: 1.25, PingMs: 42},
		{Server: "US#1", Country: "US", City: "New York", Tier: "Free", Load: 21, Score: 2.5},
	}
	data, err := json.Marshal(routeCatalogResult{Success: true, Routes: entries})
	if err != nil {
		t.Fatalf("marshal catalog: %v", err)
	}
	var payload struct {
		Success bool                `json:"success"`
		Routes  []routeCatalogEntry `json:"routes"`
	}
	if err := json.Unmarshal(data, &payload); err != nil {
		t.Fatalf("unmarshal catalog: %v", err)
	}
	if !payload.Success || len(payload.Routes) != 2 || payload.Routes[0].Server != "NL#1" || payload.Routes[0].PingMs != 42 {
		t.Fatalf("catalog payload = %+v", payload)
	}
	if payload.Routes[1].PingMs != 0 {
		t.Fatalf("unmeasured route ping = %d, want 0", payload.Routes[1].PingMs)
	}
	if containsCatalogSecret(string(data)) {
		t.Fatalf("catalog payload exposes unexpected secret-shaped data: %s", data)
	}
}

func containsCatalogSecret(value string) bool {
	for _, field := range []string{"endpoint", "confFile", "privateKey", "certificate"} {
		if strings.Contains(value, field) {
			return true
		}
	}
	return false
}
