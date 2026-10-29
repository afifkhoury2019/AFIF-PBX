// AFIF-PBX secure network engine — UDP relay.
//
// Each Relay owns one UDP socket on the tailnet (public side) and forwards every datagram to a fixed local
// port of AFIF-PBX on 127.0.0.1. Every remote peer (phone address:port) gets its own local socket, so the PBX
// sees one stable 127.0.0.1:port per phone and answers it; replies go back out of the tailnet socket to that
// phone. Nothing is routed through Windows' network stack.
package main

import (
	"errors"
	"fmt"
	"net"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

type flow struct {
	conn *net.UDPConn
	peer net.Addr
	last atomic.Int64
}

func (f *flow) touch() { f.last.Store(time.Now().UnixNano()) }

// Relay forwards datagrams between one public socket and one local target.
type Relay struct {
	Name   string
	Pub    net.PacketConn
	Target *net.UDPAddr
	Idle   time.Duration
	Logf   func(format string, args ...any)

	mu     sync.Mutex
	flows  map[string]*flow
	closed bool
	done   chan struct{}
}

func (r *Relay) logf(format string, args ...any) {
	if r.Logf != nil {
		r.Logf(format, args...)
	}
}

// Serve runs until the public socket is closed.
func (r *Relay) Serve() error {
	r.mu.Lock()
	if r.flows == nil {
		r.flows = map[string]*flow{}
	}
	if r.done == nil {
		r.done = make(chan struct{})
	}
	r.mu.Unlock()
	go r.reap()
	buf := make([]byte, 65535)
	for {
		n, from, err := r.Pub.ReadFrom(buf)
		if err != nil {
			r.Close()
			return err
		}
		f, err := r.get(from)
		if err != nil {
			r.logf("relay %s: %v", r.Name, err)
			continue
		}
		f.touch()
		if _, err := f.conn.Write(buf[:n]); err != nil && !isReset(err) {
			r.logf("relay %s: to PBX: %v", r.Name, err)
		}
	}
}

func (r *Relay) get(peer net.Addr) (*flow, error) {
	key := peer.String()
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.closed {
		return nil, net.ErrClosed
	}
	if f, ok := r.flows[key]; ok {
		return f, nil
	}
	c, err := net.DialUDP("udp", nil, r.Target)
	if err != nil {
		return nil, fmt.Errorf("local socket: %w", err)
	}
	f := &flow{conn: c, peer: peer}
	f.touch()
	r.flows[key] = f
	go r.back(f)
	return f, nil
}

// back copies the PBX's answers for one peer back out of the tailnet socket.
func (r *Relay) back(f *flow) {
	buf := make([]byte, 65535)
	for {
		n, err := f.conn.Read(buf)
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				return
			}
			if isReset(err) { // Windows reports "port unreachable" as a read error; the PBX may just be restarting
				time.Sleep(50 * time.Millisecond)
				continue
			}
			return
		}
		f.touch()
		if _, err := r.Pub.WriteTo(buf[:n], f.peer); err != nil {
			if errors.Is(err, net.ErrClosed) {
				return
			}
		}
	}
}

func (r *Relay) reap() {
	every := r.Idle / 4
	if every < time.Second {
		every = time.Second
	}
	t := time.NewTicker(every)
	defer t.Stop()
	for {
		select {
		case <-r.done:
			return
		case <-t.C:
		}
		cut := time.Now().Add(-r.Idle).UnixNano()
		r.mu.Lock()
		for k, f := range r.flows {
			if f.last.Load() < cut {
				f.conn.Close()
				delete(r.flows, k)
			}
		}
		r.mu.Unlock()
	}
}

// Flows is the number of peers currently relayed.
func (r *Relay) Flows() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.flows)
}

func (r *Relay) Close() {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.closed {
		return
	}
	r.closed = true
	r.Pub.Close()
	for k, f := range r.flows {
		f.conn.Close()
		delete(r.flows, k)
	}
	if r.done != nil {
		close(r.done)
	}
}

func isReset(err error) bool {
	s := err.Error()
	return strings.Contains(s, "forcibly closed") || strings.Contains(s, "connection refused") ||
		strings.Contains(s, "wsarecv") && strings.Contains(s, "10054")
}

// PortMap says which public (tailnet) port is relayed to which local PBX port.
type PortMap struct {
	Public, Local int
	Idle          time.Duration
}

// ParseMaps reads "5060:15060,5160:15160" (SIP) and "21000-21199" (RTP, same port locally).
func ParseMaps(sip, rtp string) ([]PortMap, error) {
	var out []PortMap
	for _, part := range strings.Split(sip, ",") {
		part = strings.TrimSpace(part)
		if part == "" {
			continue
		}
		a, b, ok := strings.Cut(part, ":")
		pub, e1 := strconv.Atoi(a)
		loc, e2 := strconv.Atoi(b)
		if !ok || e1 != nil || e2 != nil || pub <= 0 || pub > 65535 || loc <= 0 || loc > 65535 {
			return nil, fmt.Errorf("bad SIP port pair %q", part)
		}
		out = append(out, PortMap{Public: pub, Local: loc, Idle: 30 * time.Minute})
	}
	if rtp = strings.TrimSpace(rtp); rtp != "" {
		a, b, ok := strings.Cut(rtp, "-")
		lo, e1 := strconv.Atoi(a)
		hi, e2 := strconv.Atoi(b)
		if !ok || e1 != nil || e2 != nil || lo <= 0 || hi > 65535 || hi < lo || hi-lo > 2000 {
			return nil, fmt.Errorf("bad RTP range %q", rtp)
		}
		for p := lo; p <= hi; p++ {
			out = append(out, PortMap{Public: p, Local: p, Idle: 2 * time.Minute})
		}
	}
	return out, nil
}

// RelaySet is every relay for one tailnet address.
type RelaySet struct {
	mu     sync.Mutex
	ip     string
	relays []*Relay
	errs   []string
}

// Open (re)creates the relays on ip using listen to open the public sockets.
func (s *RelaySet) Open(ip string, listen func(addr string) (net.PacketConn, error), maps []PortMap, logf func(string, ...any)) {
	s.Close()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.ip, s.errs = ip, nil
	for _, m := range maps {
		pc, err := listen(net.JoinHostPort(ip, strconv.Itoa(m.Public)))
		if err != nil {
			s.errs = append(s.errs, fmt.Sprintf("udp %d: %v", m.Public, err))
			continue
		}
		r := &Relay{
			Name:   fmt.Sprintf("%d→%d", m.Public, m.Local),
			Pub:    pc,
			Target: &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: m.Local},
			Idle:   m.Idle,
			Logf:   logf,
			flows:  map[string]*flow{},
			done:   make(chan struct{}),
		}
		s.relays = append(s.relays, r)
		go r.Serve()
	}
	if logf != nil {
		logf("relaying %d UDP ports on %s (%d failed)", len(s.relays), ip, len(s.errs))
	}
}

func (s *RelaySet) Close() {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, r := range s.relays {
		r.Close()
	}
	s.relays, s.ip = nil, ""
}

type RelayInfo struct {
	Address string   `json:"address"`
	Ports   []int    `json:"ports"`
	Flows   int      `json:"flows"`
	Errors  []string `json:"errors"`
}

func (s *RelaySet) Info() RelayInfo {
	s.mu.Lock()
	defer s.mu.Unlock()
	info := RelayInfo{Address: s.ip, Errors: append([]string{}, s.errs...), Ports: []int{}}
	for _, r := range s.relays {
		_, p, _ := net.SplitHostPort(r.Pub.LocalAddr().String())
		if n, err := strconv.Atoi(p); err == nil {
			info.Ports = append(info.Ports, n)
		}
		info.Flows += r.Flows()
	}
	sort.Ints(info.Ports)
	return info
}

func (s *RelaySet) IP() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.ip
}
