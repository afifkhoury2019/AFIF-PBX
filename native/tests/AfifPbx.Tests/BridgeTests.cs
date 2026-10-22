using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AfifPbx.Bridge;
using Xunit;

namespace AfifPbx.Tests;

/// <summary>Pretends to be the Cloudflare API and records what AFIF-PBX asked for.</summary>
internal sealed class FakeCloudflare : HttpMessageHandler
{
    public readonly List<(string Method, string Path, string Body)> Calls = new();
    public readonly List<JsonObject> Dns = new();
    public readonly List<JsonObject> Routes = new();
    public List<JsonObject> Exclude = new()
    {
        new JsonObject { ["address"] = "10.0.0.0/8", ["description"] = "default" },
        new JsonObject { ["address"] = "192.168.0.0/16", ["description"] = "default" },
    };
    public bool TunnelExists;
    public JsonNode? TunnelConfig;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
        string path = req.RequestUri!.PathAndQuery.Replace("/client/v4/", "");
        Calls.Add((req.Method.Method, path, body));
        if (req.Headers.Authorization?.Parameter != "good-token") return Json(new JsonObject { ["success"] = false, ["errors"] = new JsonArray(new JsonObject { ["code"] = 10000, ["message"] = "Authentication error" }) }, HttpStatusCode.Forbidden);

        JsonNode? result = null;
        if (path.StartsWith("zones?name=powerchain.uk"))
            result = new JsonArray(new JsonObject { ["id"] = "zone1", ["name"] = "powerchain.uk", ["account"] = new JsonObject { ["id"] = "acc1", ["name"] = "Afif" } });
        else if (path.StartsWith("zones?name=")) result = new JsonArray();
        else if (path.StartsWith("accounts/acc1/cfd_tunnel?"))
            result = TunnelExists ? new JsonArray(new JsonObject { ["id"] = "tun-1234567890", ["name"] = "afif-pbx" }) : new JsonArray();
        else if (path == "accounts/acc1/cfd_tunnel" && req.Method == HttpMethod.Post) { TunnelExists = true; result = new JsonObject { ["id"] = "tun-1234567890" }; }
        else if (path == "accounts/acc1/cfd_tunnel/tun-1234567890/configurations") { TunnelConfig = JsonNode.Parse(body); result = new JsonObject(); }
        else if (path == "accounts/acc1/cfd_tunnel/tun-1234567890/token") result = JsonValue.Create("TUNNEL-TOKEN");
        else if (path.StartsWith("zones/zone1/dns_records?name="))
        {
            var name = Uri.UnescapeDataString(path.Split("name=")[1]);
            result = new JsonArray(Dns.Where(d => d["name"]!.GetValue<string>() == name).Select(d => (JsonNode)d.DeepClone()).ToArray());
        }
        else if (path == "zones/zone1/dns_records" && req.Method == HttpMethod.Post)
        {
            var rec = JsonNode.Parse(body)!.AsObject(); rec["id"] = "dns" + Dns.Count; Dns.Add(rec); result = rec.DeepClone();
        }
        else if (path.StartsWith("accounts/acc1/teamnet/routes?")) result = new JsonArray(Routes.Select(r => (JsonNode)r.DeepClone()).ToArray());
        else if (path == "accounts/acc1/teamnet/routes" && req.Method == HttpMethod.Post) { var r = JsonNode.Parse(body)!.AsObject(); Routes.Add(r); result = r.DeepClone(); }
        else if (path == "accounts/acc1/devices/policy/exclude" && req.Method == HttpMethod.Get) result = new JsonArray(Exclude.Select(e => (JsonNode)e.DeepClone()).ToArray());
        else if (path == "accounts/acc1/devices/policy/exclude" && req.Method == HttpMethod.Put) { Exclude = JsonNode.Parse(body)!.AsArray().Select(n => n!.AsObject()).ToList(); result = new JsonArray(); }
        else if (path == "accounts/acc1/access/organizations") result = new JsonObject { ["auth_domain"] = "afif.cloudflareaccess.com" };
        else return Json(new JsonObject { ["success"] = false, ["errors"] = new JsonArray(new JsonObject { ["code"] = 7003, ["message"] = "No route " + path }) }, HttpStatusCode.NotFound);

        return Json(new JsonObject { ["success"] = true, ["errors"] = new JsonArray(), ["result"] = result });
    }

    private static HttpResponseMessage Json(JsonNode n, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json") };
}

public class BridgeTests
{
    private static (BridgeManager mgr, FakeCloudflare cf) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "afif-bridge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var cf = new FakeCloudflare();
        var mgr = new BridgeManager(dir, () => 8080, handler: () => cf) { ConnectorPath = Path.Combine(dir, "no-cloudflared.exe") };
        return (mgr, cf);
    }

    [Fact]
    public async Task Full_setup_creates_tunnel_dns_route_and_split_tunnel()
    {
        var (mgr, cf) = Make();
        var (ok, log) = await mgr.SetupAsync(new BridgeManager.SetupRequest("good-token", "powerchain.uk", "pbx", "sip", "192.168.0.103"));
        Assert.True(ok, string.Join("\n", log));

        // tunnel ingress: panel hostname to the local web panel, catch-all 404, warp routing on
        var ingress = cf.TunnelConfig!["config"]!["ingress"]!.AsArray();
        Assert.Equal("pbx.powerchain.uk", ingress[0]!["hostname"]!.GetValue<string>());
        Assert.Equal("http://localhost:8080", ingress[0]!["service"]!.GetValue<string>());
        Assert.Equal("http_status:404", ingress[^1]!["service"]!.GetValue<string>());
        Assert.True(cf.TunnelConfig["config"]!["warp-routing"]!["enabled"]!.GetValue<bool>());

        Assert.Contains(cf.Dns, d => d["name"]!.GetValue<string>() == "pbx.powerchain.uk" && d["type"]!.GetValue<string>() == "CNAME"
            && d["content"]!.GetValue<string>() == "tun-1234567890.cfargotunnel.com" && d["proxied"]!.GetValue<bool>());
        Assert.Contains(cf.Dns, d => d["name"]!.GetValue<string>() == "sip.powerchain.uk" && d["content"]!.GetValue<string>() == "192.168.0.103");
        Assert.Contains(cf.Routes, r => r["network"]!.GetValue<string>() == "192.168.0.103/32");

        // 192.168.0.0/16 replaced by blocks that leave out 192.168.0.103; 10/8 untouched
        var excl = cf.Exclude.Select(e => e["address"]!.GetValue<string>()).ToList();
        Assert.Contains("10.0.0.0/8", excl);
        Assert.DoesNotContain("192.168.0.0/16", excl);
        Assert.DoesNotContain(excl, e => Cidr.Contains(e, "192.168.0.103"));
        Assert.Contains(excl, e => Cidr.Contains(e, "192.168.0.104"));
        Assert.Contains(excl, e => Cidr.Contains(e, "192.168.200.1"));

        var c = mgr.Config;
        Assert.Equal("tun-1234567890", c.TunnelId);
        Assert.Equal("afif.cloudflareaccess.com", c.TeamDomain);
        Assert.Equal("TUNNEL-TOKEN", BridgeManager.Unprotect(c.ProtectedTunnelToken));
        mgr.Dispose();
    }

    [Fact]
    public async Task Second_setup_reuses_the_tunnel_and_never_touches_foreign_dns()
    {
        var (mgr, cf) = Make();
        cf.Dns.Add(new JsonObject { ["id"] = "x", ["name"] = "iot.powerchain.uk", ["type"] = "CNAME", ["content"] = "other.cfargotunnel.com", ["proxied"] = true, ["comment"] = "Node-RED" });
        Assert.True((await mgr.SetupAsync(new("good-token", "powerchain.uk", "pbx", "sip", "192.168.0.103"))).Ok);
        Assert.True((await mgr.SetupAsync(new("good-token", "powerchain.uk", "pbx", "sip", "192.168.0.103"))).Ok);
        Assert.Single(cf.Calls, c => c.Method == "POST" && c.Path == "accounts/acc1/cfd_tunnel");
        Assert.DoesNotContain(cf.Calls, c => c.Path.Contains("dns_records/x"));

        // choosing a name that already belongs to something else is refused
        var (ok, log) = await mgr.SetupAsync(new("good-token", "powerchain.uk", "iot", "sip", "192.168.0.103"));
        Assert.False(ok);
        Assert.Contains(log, l => l.Contains("not managed by AFIF-PBX"));
        mgr.Dispose();
    }

    [Fact]
    public async Task Bad_token_reports_a_clear_error()
    {
        var (mgr, _) = Make();
        var (ok, log) = await mgr.SetupAsync(new("bad", "powerchain.uk", "pbx", "sip", "192.168.0.103"));
        Assert.False(ok);
        Assert.Contains(log, l => l.Contains("permissions"));
        mgr.Dispose();
    }

    [Theory]
    [InlineData("192.168.0.0/16", "192.168.0.103", 16)]
    [InlineData("10.0.0.0/8", "10.1.2.3", 24)]
    public void Cidr_subtract_covers_everything_but_the_address(string range, string ip, int blocks)
    {
        var parts = Cidr.Subtract(range, ip);
        Assert.Equal(blocks, parts.Count);
        Assert.DoesNotContain(parts, p => Cidr.Contains(p, ip));
        // total size = range size - 1
        long total = parts.Sum(p => 1L << (32 - int.Parse(p.Split('/')[1])));
        Assert.Equal((1L << (32 - int.Parse(range.Split('/')[1]))) - 1, total);
    }
}
