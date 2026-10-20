package speedtest

import (
	"context"
	"crypto/ecdh"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"net"
	"net/http"
	"net/netip"
	"strconv"
	"strings"
	"testing"
	"time"

	"golang.zx2c4.com/wireguard/conn"
	"golang.zx2c4.com/wireguard/device"
	"golang.zx2c4.com/wireguard/tun/netstack"
	"protonvpn-wg-confgen/internal/api"
)

func TestRealUserspaceTunnelTransfers(t *testing.T) {
	serverKey, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	clientKey, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	tun, tnet, err := netstack.CreateNetTUN([]netip.Addr{netip.MustParseAddr("10.2.0.1")}, nil, 1280)
	if err != nil {
		t.Fatal(err)
	}
	dev := device.NewDevice(tun, conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	defer dev.Close()
	err = dev.IpcSet(fmt.Sprintf("private_key=%s\nlisten_port=0\npublic_key=%s\nallowed_ip=10.2.0.2/32\n", hex.EncodeToString(serverKey.Bytes()), hex.EncodeToString(clientKey.PublicKey().Bytes())))
	if err != nil {
		t.Fatal(err)
	}
	if err = dev.Up(); err != nil {
		t.Fatal(err)
	}
	state, err := dev.IpcGet()
	if err != nil {
		t.Fatal(err)
	}
	port := 0
	for _, line := range strings.Split(state, "\n") {
		if strings.HasPrefix(line, "listen_port=") {
			port, _ = strconv.Atoi(strings.TrimPrefix(line, "listen_port="))
		}
	}
	if port == 0 {
		t.Fatal("no UDP listener")
	}
	listener, err := tnet.ListenTCP(&net.TCPAddr{IP: net.ParseIP("10.2.0.1"), Port: 8080})
	if err != nil {
		t.Fatal(err)
	}
	srv := &http.Server{Handler: speedHandler(t)}
	defer srv.Close()
	go srv.Serve(listener)
	client, cleanup, err := tunnelClientPort(base64.StdEncoding.EncodeToString(clientKey.Bytes()), api.PhysicalServer{EntryIP: "127.0.0.1", X25519PublicKey: base64.StdEncoding.EncodeToString(serverKey.PublicKey().Bytes())}, port)
	if err != nil {
		t.Fatal(err)
	}
	defer cleanup()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	m, err := measureHTTP(ctx, client, "http://10.2.0.1:8080", 65536, 32768)
	if err != nil || capacity(m) <= 0 {
		t.Fatalf("encrypted tunnel transfer failed: %+v %v", m, err)
	}
}

func TestTunnelNeverFallsBackToHostHTTP(t *testing.T) {
	private, _ := ecdh.X25519().GenerateKey(rand.Reader)
	peer, _ := ecdh.X25519().GenerateKey(rand.Reader)
	client, cleanup, err := tunnelClientPort(base64.StdEncoding.EncodeToString(private.Bytes()), api.PhysicalServer{EntryIP: "127.0.0.1", X25519PublicKey: base64.StdEncoding.EncodeToString(peer.PublicKey().Bytes())}, 1)
	if err != nil {
		t.Fatal(err)
	}
	defer cleanup()
	host, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer host.Close()
	server := &http.Server{Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("benchmark escaped to host HTTP")
		w.WriteHeader(200)
	})}
	go server.Serve(host)
	defer server.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	if _, err := request(ctx, client, http.MethodGet, "http://"+host.Addr().String(), nil, 0); err == nil {
		t.Fatal("unconnected tunnel succeeded")
	}
}
