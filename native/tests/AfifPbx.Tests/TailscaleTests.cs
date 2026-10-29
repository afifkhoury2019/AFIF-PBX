using AfifPbx.Remote;
using Xunit;

namespace AfifPbx.Tests;

public class TailscaleTests
{
    private const string RunningJson = """
    {
      "Version": "1.80.2-t1234",
      "BackendState": "Running",
      "AuthURL": "",
      "TailscaleIPs": ["100.101.102.103", "fd7a:115c:a1e0::1234"],
      "Self": { "HostName": "afif-pbx", "DNSName": "afif-pbx.tail1a2b3.ts.net.", "OS": "windows", "Online": true,
                "KeyExpiry": "2027-04-20T10:00:00Z" },
      "Health": [],
      "MagicDNSSuffix": "tail1a2b3.ts.net",
      "CurrentTailnet": { "Name": "afifkhoury2012@gmail.com", "MagicDNSSuffix": "tail1a2b3.ts.net", "MagicDNSEnabled": true },
      "Peer": {
        "nodekey:aaa": { "HostName": "Pixel-8", "DNSName": "pixel-8.tail1a2b3.ts.net.", "OS": "android", "Online": true,
                         "TailscaleIPs": ["100.90.1.2", "fd7a:115c:a1e0::2"], "CurAddr": "94.249.1.2:41641", "Relay": "fra" },
        "nodekey:bbb": { "HostName": "laptop", "DNSName": "laptop.tail1a2b3.ts.net.", "OS": "windows", "Online": false,
                         "TailscaleIPs": ["100.90.1.3"], "CurAddr": "", "Relay": "fra", "LastSeen": "2026-10-27T08:00:00Z" }
      }
    }
    """;

    [Fact]
    public void Parses_tailscale_status()
    {
        var st = new TailscaleStatus();
        TailscaleManager.Parse(RunningJson, st);
        Assert.True(st.Connected);
        Assert.Equal("100.101.102.103", st.Ipv4);
        Assert.Equal("fd7a:115c:a1e0::1234", st.Ipv6);
        Assert.Equal("afif-pbx.tail1a2b3.ts.net", st.DnsName);
        Assert.Equal("afifkhoury2012@gmail.com", st.Tailnet);
        Assert.NotNull(st.KeyExpiry);
        Assert.Equal(2, st.Peers.Count);
        var phone = st.Peers[0];
        Assert.Equal("Pixel-8", phone.HostName);
        Assert.True(phone.Online);
        Assert.Equal("100.90.1.2", phone.Address);
        Assert.Equal("direct", phone.Connection);
        Assert.Equal("relay fra", st.Peers[1].Connection);
    }

    [Fact]
    public void Status_serializes_like_the_web_panel_does()
    {
        var st = new TailscaleStatus();
        TailscaleManager.Parse(RunningJson, st);
        var json = System.Text.Json.JsonSerializer.Serialize(st, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"ipv4\":\"100.101.102.103\"", json);
        Assert.Contains("\"connected\":true", json);
    }

    [Fact]
    public void Parses_signed_out_state_with_login_link()
    {
        var st = new TailscaleStatus();
        TailscaleManager.Parse("""{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/abc123","TailscaleIPs":null,"Self":{"HostName":"afif-pbx"},"Peer":null}""", st);
        Assert.False(st.Connected);
        Assert.Equal("https://login.tailscale.com/a/abc123", st.AuthUrl);
        Assert.Equal("", st.Ipv4);
    }

    /// <summary>The real engine in test mode (no tailnet): AFIF_NET_PATH is set by the CI after building afif-net.</summary>
    private static (TailscaleManager? mgr, FakeCloudflare cf, string dir) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afif-ts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var cf = new FakeCloudflare();
        var exe = Environment.GetEnvironmentVariable("AFIF_NET_PATH");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return (null, cf, dir);
        var mgr = new TailscaleManager(dir, handler: () => cf) { EnginePath = exe, ExtraArgs = "-plain-ip 127.0.0.2" };
        return (mgr, cf, dir);
    }

    [Fact]
    public async Task Switching_on_starts_the_built_in_engine_and_switching_off_stops_it()
    {
        var (mgr, _, dir) = Make();
        if (mgr is null) return;
        using (mgr)
        {
            var off = await mgr.StatusAsync(false);
            Assert.True(off.Installed);
            Assert.False(off.Enabled);
            var (ok, msg, url) = await mgr.ConnectAsync(null, "AFIF PBX");
            Assert.True(ok, msg);
            Assert.Equal("", url);
            var st = await mgr.StatusAsync(false);
            Assert.True(st.Connected, st.Message);
            Assert.Equal("127.0.0.2", st.Ipv4);
            Assert.Equal("afif-pbx", st.WantedName);
            Assert.True(new TailscaleManager(dir).Config.Enabled);
            var (lok, _) = await mgr.LogoutAsync();
            Assert.True(lok);
            Assert.False((await mgr.StatusAsync(false)).Connected);
            Assert.False(new TailscaleManager(dir).Config.Enabled);
        }
    }

    [Fact]
    public async Task Points_the_domain_at_the_secure_network_address()
    {
        var (mgr, cf, dir) = Make();
        if (mgr is null) return;
        using (mgr)
        {
            Assert.True((await mgr.ConnectAsync(null, null)).Ok);
            var (ok, log) = await mgr.SetDomainAsync("voip.powerchain.uk", "good-token");
            Assert.True(ok, string.Join("\n", log));
            var rec = Assert.Single(cf.Dns);
            Assert.Equal("A", rec["type"]!.GetValue<string>());
            Assert.Equal("voip.powerchain.uk", rec["name"]!.GetValue<string>());
            Assert.Equal("127.0.0.2", rec["content"]!.GetValue<string>());
            Assert.False(rec["proxied"]!.GetValue<bool>());
            Assert.Contains("AFIF-PBX", rec["comment"]!.GetValue<string>());
            // the saved token is used next time
            var (ok2, log2) = await mgr.SetDomainAsync("voip.powerchain.uk", null);
            Assert.True(ok2);
            Assert.Contains(log2, l => l.Contains("unchanged"));
            Assert.Equal("voip.powerchain.uk", new TailscaleManager(dir).Config.Domain);
        }
    }

    [Fact]
    public async Task Domain_without_token_says_what_record_to_create()
    {
        var (mgr, cf, _) = Make();
        if (mgr is null) return;
        using (mgr)
        {
            Assert.True((await mgr.ConnectAsync(null, null)).Ok);
            var (ok, log) = await mgr.SetDomainAsync("voip.powerchain.uk", null);
            Assert.False(ok);
            Assert.Contains(log, l => l.Contains("127.0.0.2") && l.Contains("DNS only"));
            Assert.Empty(cf.Calls);
        }
    }

    [Fact]
    public async Task Rejects_names_that_are_not_full_hostnames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afif-ts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        using var mgr = new TailscaleManager(dir);
        var (ok, _) = await mgr.SetDomainAsync("voip", null);
        Assert.False(ok);
    }

    [Fact]
    public void Old_bridge_token_is_kept_for_the_domain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afif-ts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bridge.json"), "{\"ProtectedApiToken\":\"" + Secrets.Protect("tok") + "\"}");
        using var mgr = new TailscaleManager(dir);
        Assert.Equal("tok", Secrets.Unprotect(mgr.Config.ProtectedApiToken));
    }
}
