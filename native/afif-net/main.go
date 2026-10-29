//go:build !relayonly

// afif-net — the secure network engine built into AFIF-PBX.
//
// It is a complete Tailscale node that lives inside this process only (userspace networking): no network
// adapter, no VPN and no route is added to Windows, so nothing else on the PC uses the tailnet. It answers on
// the PBX's tailnet address for SIP and voice (UDP) and relays that traffic to AFIF-PBX on 127.0.0.1.
//
// AFIF-PBX starts it, reads "CONTROL <addr>" from stdout and drives it through a small local HTTP API
// (protected by the AFIF_NET_TOKEN secret): GET /status (Tailscale status JSON), GET /relay, POST /login,
// POST /logout.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"tailscale.com/tsnet"
)

var (
	stateDir   = flag.String("dir", "", "state folder (keys and settings of this node)")
	hostname   = flag.String("hostname", "afif-pbx", "name of the PBX on the tailnet")
	sipMap     = flag.String("sip", "", "tailnet:local UDP port pairs, e.g. 5060:15060,5160:15160")
	rtpRange   = flag.String("rtp", "", "UDP port range relayed to the same ports on 127.0.0.1, e.g. 21000-21199")
	plainIP    = flag.String("plain-ip", "", "test mode: listen on this ordinary address instead of joining a tailnet")
	watchStdin = flag.Bool("watch-stdin", false, "exit when stdin closes (the PBX stopped)")
)

var (
	logMu   sync.Mutex
	logFile *os.File
)

// engineLog goes to afif-net.log in the state folder (Tailscale's own, very detailed log).
func engineLog(format string, args ...any) {
	logMu.Lock()
	defer logMu.Unlock()
	if logFile != nil {
		fmt.Fprintf(logFile, time.Now().Format("2006-01-02 15:04:05.000 ")+format+"\n", args...)
	}
}

// userLog is shown in AFIF-PBX's own log.
func userLog(format string, args ...any) {
	fmt.Fprintf(os.Stderr, format+"\n", args...)
	engineLog(format, args...)
}

func main() {
	flag.Parse()
	maps, err := ParseMaps(*sipMap, *rtpRange)
	if err != nil {
		log.Fatal(err)
	}
	if *watchStdin {
		go func() { io.Copy(io.Discard, os.Stdin); os.Exit(0) }()
	}
	token := os.Getenv("AFIF_NET_TOKEN")
	if token == "" {
		log.Fatal("AFIF_NET_TOKEN is not set")
	}

	var relays RelaySet
	var statusJSON func(ctx context.Context) ([]byte, error)
	var login, logout func(ctx context.Context) error

	if *plainIP != "" {
		ip := *plainIP
		relays.Open(ip, func(addr string) (net.PacketConn, error) { return net.ListenPacket("udp", addr) }, maps, userLog)
		statusJSON = func(context.Context) ([]byte, error) {
			return json.Marshal(map[string]any{
				"Version": "test", "BackendState": "Running", "TailscaleIPs": []string{ip},
				"Self":           map[string]any{"HostName": *hostname, "DNSName": *hostname + ".test.ts.net."},
				"CurrentTailnet": map[string]any{"Name": "test"},
			})
		}
		login = func(context.Context) error { return nil }
		logout = func(context.Context) error { return nil }
	} else {
		if *stateDir == "" {
			log.Fatal("-dir is required")
		}
		os.MkdirAll(*stateDir, 0o700)
		lp := filepath.Join(*stateDir, "afif-net.log")
		if fi, err := os.Stat(lp); err == nil && fi.Size() > 10<<20 {
			os.Rename(lp, lp+".old")
		}
		logFile, _ = os.OpenFile(lp, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)

		srv := &tsnet.Server{
			Dir:      *stateDir,
			Hostname: *hostname,
			Logf:     engineLog,
			UserLogf: userLog,
		}
		if err := srv.Start(); err != nil {
			log.Fatalf("secure network engine: %v", err)
		}
		defer srv.Close()
		lc, err := srv.LocalClient()
		if err != nil {
			log.Fatal(err)
		}
		statusJSON = func(ctx context.Context) ([]byte, error) {
			st, err := lc.Status(ctx)
			if err != nil {
				return nil, err
			}
			return json.Marshal(st)
		}
		login = lc.StartLoginInteractive
		logout = lc.Logout

		// open the relays on our tailnet IPv4 address, and again whenever it changes
		go func() {
			for {
				ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
				st, err := lc.Status(ctx)
				cancel()
				ip := ""
				if err == nil && st.BackendState == "Running" {
					for _, a := range st.TailscaleIPs {
						if a.Is4() {
							ip = a.String()
						}
					}
				}
				if ip != relays.IP() {
					if ip == "" {
						relays.Close()
						userLog("secure network down; relays closed")
					} else {
						relays.Open(ip, func(addr string) (net.PacketConn, error) { return srv.ListenPacket("udp", addr) }, maps, userLog)
					}
				}
				time.Sleep(2 * time.Second)
			}
		}()
	}

	mux := http.NewServeMux()
	auth := func(h http.HandlerFunc) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			if r.Header.Get("X-Afif-Token") != token {
				http.Error(w, "forbidden", http.StatusForbidden)
				return
			}
			h(w, r)
		}
	}
	mux.HandleFunc("GET /status", auth(func(w http.ResponseWriter, r *http.Request) {
		b, err := statusJSON(r.Context())
		if err != nil {
			http.Error(w, err.Error(), http.StatusServiceUnavailable)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.Write(b)
	}))
	mux.HandleFunc("GET /relay", auth(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(relays.Info())
	}))
	mux.HandleFunc("POST /login", auth(func(w http.ResponseWriter, r *http.Request) {
		if err := login(r.Context()); err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		w.Write([]byte("{}"))
	}))
	mux.HandleFunc("POST /logout", auth(func(w http.ResponseWriter, r *http.Request) {
		relays.Close()
		if err := logout(r.Context()); err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		w.Write([]byte("{}"))
	}))

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		log.Fatal(err)
	}
	out := bufio.NewWriter(os.Stdout)
	fmt.Fprintf(out, "CONTROL %s\n", ln.Addr())
	out.Flush()
	userLog("afif-net ready (control %s, %s)", ln.Addr(), strings.TrimSpace(*hostname))
	log.Fatal(http.Serve(ln, mux))
}
