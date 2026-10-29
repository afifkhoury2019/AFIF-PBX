package main

import (
	"net"
	"testing"
	"time"
)

// A phone talks to the public socket; the "PBX" echoes with a prefix and also sends first to a known peer.
func TestRelayForwardsBothWaysPerPeer(t *testing.T) {
	pbx, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer pbx.Close()
	go func() {
		buf := make([]byte, 2000)
		for {
			n, from, err := pbx.ReadFromUDP(buf)
			if err != nil {
				return
			}
			pbx.WriteToUDP(append([]byte("pbx:"), buf[:n]...), from)
		}
	}()

	var set RelaySet
	maps := []PortMap{{Public: 0, Local: pbx.LocalAddr().(*net.UDPAddr).Port, Idle: time.Minute}}
	set.Open("127.0.0.1", func(addr string) (net.PacketConn, error) { return net.ListenPacket("udp", addr) }, maps, t.Logf)
	defer set.Close()
	info := set.Info()
	if len(info.Ports) != 1 {
		t.Fatalf("relay not listening: %+v", info)
	}
	pub := &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: info.Ports[0]}

	phones := make([]*net.UDPConn, 3)
	for i := range phones {
		c, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
		if err != nil {
			t.Fatal(err)
		}
		defer c.Close()
		phones[i] = c
	}
	for round := 0; round < 3; round++ {
		for i, c := range phones {
			msg := []byte{byte('a' + i), byte('0' + round)}
			c.WriteToUDP(msg, pub)
			c.SetReadDeadline(time.Now().Add(2 * time.Second))
			buf := make([]byte, 100)
			n, from, err := c.ReadFromUDP(buf)
			if err != nil {
				t.Fatalf("phone %d round %d: %v", i, round, err)
			}
			if got := string(buf[:n]); got != "pbx:"+string(msg) {
				t.Fatalf("phone %d got %q", i, got)
			}
			if from.Port != pub.Port {
				t.Fatalf("answer must come from the public port %d, came from %d", pub.Port, from.Port)
			}
		}
	}
	if f := set.Info().Flows; f != 3 {
		t.Fatalf("want one flow per phone, got %d", f)
	}
}

func TestIdleFlowsAreDropped(t *testing.T) {
	pbx, _ := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	defer pbx.Close()
	var set RelaySet
	set.Open("127.0.0.1", func(addr string) (net.PacketConn, error) { return net.ListenPacket("udp", addr) },
		[]PortMap{{Public: 0, Local: pbx.LocalAddr().(*net.UDPAddr).Port, Idle: time.Second}}, nil)
	defer set.Close()
	pub := &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: set.Info().Ports[0]}
	c, _ := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	defer c.Close()
	c.WriteToUDP([]byte("x"), pub)
	time.Sleep(200 * time.Millisecond)
	if set.Info().Flows != 1 {
		t.Fatal("flow not created")
	}
	time.Sleep(2500 * time.Millisecond)
	if n := set.Info().Flows; n != 0 {
		t.Fatalf("idle flow kept: %d", n)
	}
}

func TestParseMaps(t *testing.T) {
	m, err := ParseMaps("5060:15060, 5160:15160", "21000-21003")
	if err != nil {
		t.Fatal(err)
	}
	if len(m) != 6 || m[0].Public != 5060 || m[0].Local != 15060 || m[5].Public != 21003 || m[5].Local != 21003 {
		t.Fatalf("%+v", m)
	}
	if _, err := ParseMaps("5060", ""); err == nil {
		t.Fatal("want error")
	}
}
