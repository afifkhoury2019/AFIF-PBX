using System.Runtime.InteropServices;
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
    public void Parses_signed_out_state_with_login_link()
    {
        var st = new TailscaleStatus();
        TailscaleManager.Parse("""{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/abc123","TailscaleIPs":null,"Self":{"HostName":"afif-pbx"},"Peer":null}""", st);
        Assert.False(st.Connected);
        Assert.Equal("https://login.tailscale.com/a/abc123", st.AuthUrl);
        Assert.Equal("", st.Ipv4);
    }

    /// <summary>A stand-in for tailscale.exe that answers "status --json" and "version".</summary>
    private static (TailscaleManager mgr, FakeCloudflare cf, string dir) Make(Func<string>? bridgeToken = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "afif-ts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var cf = new FakeCloudflare();
        var mgr = new TailscaleManager(dir, handler: () => cf, bridgeToken: bridgeToken);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cli = Path.Combine(dir, "tailscale");
            File.WriteAllText(cli, "#!/bin/sh\ncase \"$1\" in\n  status) cat \"$(dirname \"$0\")/status.json\" ;;\n  version) echo 1.80.2 ;;\n  *) echo ok ;;\nesac\n");
            File.WriteAllText(Path.Combine(dir, "status.json"), RunningJson);
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            mgr.CliPath = cli;
        }
        return (mgr, cf, dir);
    }

    [Fact]
    public async Task Status_runs_the_cli()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (mgr, _, _) = Make();
        var st = await mgr.StatusAsync(checkDomain: false);
        Assert.True(st.Installed);
        Assert.True(st.DaemonRunning);
        Assert.Equal("Connected", st.Message);
        Assert.Equal("1.80.2", st.Version);
    }

    [Fact]
    public async Task Points_the_domain_at_the_tailscale_address_using_the_bridge_token()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (mgr, cf, dir) = Make(() => "good-token");
        var (ok, log) = await mgr.SetDomainAsync("voip.powerchain.uk", null);
        Assert.True(ok, string.Join("\n", log));
        var rec = Assert.Single(cf.Dns);
        Assert.Equal("A", rec["type"]!.GetValue<string>());
        Assert.Equal("voip.powerchain.uk", rec["name"]!.GetValue<string>());
        Assert.Equal("100.101.102.103", rec["content"]!.GetValue<string>());
        Assert.False(rec["proxied"]!.GetValue<bool>());
        Assert.Contains("AFIF-PBX", rec["comment"]!.GetValue<string>());

        // a second run changes nothing
        var (ok2, log2) = await mgr.SetDomainAsync("voip.powerchain.uk", null);
        Assert.True(ok2);
        Assert.Contains(log2, l => l.Contains("unchanged"));
        Assert.Equal("voip.powerchain.uk", new TailscaleManager(dir).Config.Domain);
    }

    [Fact]
    public async Task Domain_needs_a_token_and_says_what_to_create_otherwise()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (mgr, cf, _) = Make();
        var (ok, log) = await mgr.SetDomainAsync("voip.powerchain.uk", null);
        Assert.False(ok);
        Assert.Contains(log, l => l.Contains("100.101.102.103") && l.Contains("DNS only"));
        Assert.Empty(cf.Calls);
    }

    [Fact]
    public async Task Rejects_names_that_are_not_full_hostnames()
    {
        var (mgr, _, _) = Make(() => "good-token");
        var (ok, _) = await mgr.SetDomainAsync("voip", null);
        Assert.False(ok);
    }
}
