using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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
