using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AfifPbx.Bridge;

public sealed class CloudflareException : Exception
{
    public CloudflareException(string message) : base(message) { }
}

/// <summary>Minimal Cloudflare API v4 client for what the AFIF Bridge needs.</summary>
public sealed class CloudflareApi
{
    private readonly HttpClient _http;
    public const string DefaultBase = "https://api.cloudflare.com/client/v4/";

    public CloudflareApi(string apiToken, HttpMessageHandler? handler = null, string baseUrl = DefaultBase)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AFIF-PBX/2");
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch { }
        bool ok = json?["success"]?.GetValue<bool>() ?? resp.IsSuccessStatusCode;
        if (!ok)
        {
            var errors = json?["errors"] as JsonArray;
            var msg = errors is { Count: > 0 }
                ? string.Join("; ", errors.Select(e => $"{e?["message"]} (code {e?["code"]})"))
                : $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                msg += " — check the API token's permissions";
            throw new CloudflareException(msg);
        }
        return json?["result"];
    }

    public sealed record Zone(string Id, string Name, string AccountId, string AccountName);

    public async Task<Zone> FindZoneAsync(string name)
    {
        var r = await SendAsync(HttpMethod.Get, $"zones?name={Uri.EscapeDataString(name)}").ConfigureAwait(false) as JsonArray;
        var z = r?.FirstOrDefault() ?? throw new CloudflareException($"The domain {name} was not found in this Cloudflare account (or the token cannot see it).");
        return new Zone(z["id"]!.GetValue<string>(), z["name"]!.GetValue<string>(),
            z["account"]!["id"]!.GetValue<string>(), z["account"]?["name"]?.GetValue<string>() ?? "");
    }

    /// <summary>Finds the named tunnel, or creates it (remotely managed). Returns its id.</summary>
    public async Task<(string Id, bool Created)> EnsureTunnelAsync(string accountId, string name)
    {
        var list = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false").ConfigureAwait(false) as JsonArray;
        var existing = list?.FirstOrDefault(t => t?["name"]?.GetValue<string>() == name);
        if (existing is not null) return (existing["id"]!.GetValue<string>(), false);
        var created = await SendAsync(HttpMethod.Post, $"accounts/{accountId}/cfd_tunnel", new { name, config_src = "cloudflare" }).ConfigureAwait(false);
        return (created!["id"]!.GetValue<string>(), true);
    }

    public async Task<string> TunnelTokenAsync(string accountId, string tunnelId)
    {
        var r = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/token").ConfigureAwait(false);
        return r?.GetValue<string>() ?? throw new CloudflareException("Cloudflare did not return a tunnel token");
    }

    /// <summary>Public hostnames served by the tunnel, plus private-network (WARP) routing.</summary>
    public static object TunnelConfig(IEnumerable<(string Hostname, string Service)> ingress) => new
    {
        config = new
        {
            ingress = ingress.Select(i => (object)new { hostname = i.Hostname, service = i.Service })
                             .Append(new { service = "http_status:404" }).ToArray(),
            warp_routing = new { enabled = true },
        },
    };

    public Task PutTunnelConfigAsync(string accountId, string tunnelId, IEnumerable<(string Hostname, string Service)> ingress) =>
        SendAsync(HttpMethod.Put, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations", RenameWarpRouting(TunnelConfig(ingress)));

    // Cloudflare expects "warp-routing" (with a hyphen)
    private static JsonNode RenameWarpRouting(object cfg)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(cfg))!;
        var c = node["config"]!.AsObject();
        if (c.Remove("warp_routing", out var wr)) c["warp-routing"] = wr;
        return node;
    }

    /// <summary>Create or update a DNS record. Returns "created", "updated" or "unchanged".</summary>
    public async Task<string> UpsertDnsAsync(string zoneId, string type, string name, string content, bool proxied, string comment)
    {
        var list = await SendAsync(HttpMethod.Get, $"zones/{zoneId}/dns_records?name={Uri.EscapeDataString(name)}").ConfigureAwait(false) as JsonArray;
        var body = new { type, name, content, proxied, ttl = 1, comment };
        var existing = list?.FirstOrDefault();
        if (existing is null)
        {
            await SendAsync(HttpMethod.Post, $"zones/{zoneId}/dns_records", body).ConfigureAwait(false);
            return "created";
        }
        if (existing["type"]?.GetValue<string>() == type && existing["content"]?.GetValue<string>() == content
            && existing["proxied"]?.GetValue<bool>() == proxied) return "unchanged";
        string comment0 = existing["comment"]?.GetValue<string>() ?? "";
        if (!comment0.Contains("AFIF-PBX"))
            throw new CloudflareException($"{name} already exists in DNS and is not managed by AFIF-PBX — choose another name or delete it in Cloudflare first.");
        await SendAsync(HttpMethod.Put, $"zones/{zoneId}/dns_records/{existing["id"]}", body).ConfigureAwait(false);
        return "updated";
    }

    /// <summary>Route a private network (e.g. the PBX's LAN IP /32) through the tunnel for Cloudflare One clients.</summary>
    public async Task<string> EnsureRouteAsync(string accountId, string tunnelId, string cidr)
    {
        var list = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/teamnet/routes?is_deleted=false&network_subset={Uri.EscapeDataString(cidr)}").ConfigureAwait(false) as JsonArray;
        var hit = list?.FirstOrDefault(r => r?["network"]?.GetValue<string>() == cidr);
        if (hit is not null)
        {
            if (hit["tunnel_id"]?.GetValue<string>() == tunnelId) return "unchanged";
            await SendAsync(HttpMethod.Patch, $"accounts/{accountId}/teamnet/routes/{hit["id"]}", new { tunnel_id = tunnelId }).ConfigureAwait(false);
            return "moved to this tunnel";
        }
        await SendAsync(HttpMethod.Post, $"accounts/{accountId}/teamnet/routes", new { network = cidr, tunnel_id = tunnelId, comment = "AFIF-PBX" }).ConfigureAwait(false);
        return "created";
    }

    public sealed record SplitEntry(string Address, string Description);

    public async Task<List<SplitEntry>> GetSplitTunnelExcludeAsync(string accountId)
    {
        var r = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/devices/policy/exclude").ConfigureAwait(false) as JsonArray;
        return r?.Select(e => new SplitEntry(e?["address"]?.GetValue<string>() ?? e?["host"]?.GetValue<string>() ?? "",
            e?["description"]?.GetValue<string>() ?? "")).ToList() ?? new();
    }

    public Task PutSplitTunnelExcludeAsync(string accountId, IEnumerable<SplitEntry> entries) =>
        SendAsync(HttpMethod.Put, $"accounts/{accountId}/devices/policy/exclude",
            entries.Select(e => e.Address.Contains('/') || IPAddress.TryParse(e.Address, out _)
                ? (object)new { address = e.Address, description = e.Description }
                : new { host = e.Address, description = e.Description }).ToArray());

    /// <summary>The Zero Trust team domain (e.g. "afif.cloudflareaccess.com"), used to enroll the Cloudflare One app.</summary>
    public async Task<string?> TeamDomainAsync(string accountId)
    {
        try
        {
            var r = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/organizations").ConfigureAwait(false);
            return r?["auth_domain"]?.GetValue<string>();
        }
        catch (CloudflareException) { return null; }
    }

    public async Task DeleteDnsIfOursAsync(string zoneId, string name)
    {
        var list = await SendAsync(HttpMethod.Get, $"zones/{zoneId}/dns_records?name={Uri.EscapeDataString(name)}").ConfigureAwait(false) as JsonArray;
        foreach (var r in list ?? new JsonArray())
            if ((r?["comment"]?.GetValue<string>() ?? "").Contains("AFIF-PBX"))
                await SendAsync(HttpMethod.Delete, $"zones/{zoneId}/dns_records/{r!["id"]}").ConfigureAwait(false);
    }
}

/// <summary>IPv4 CIDR helpers (to carve the PBX address out of the WARP split-tunnel exclude list).</summary>
public static class Cidr
{
    public static (uint Network, int Prefix) Parse(string cidr)
    {
        var parts = cidr.Split('/');
        var ip = IPAddress.Parse(parts[0]).GetAddressBytes();
        int prefix = parts.Length > 1 ? int.Parse(parts[1]) : 32;
        uint v = (uint)(ip[0] << 24 | ip[1] << 16 | ip[2] << 8 | ip[3]);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return (v & mask, prefix);
    }

    public static string Format(uint network, int prefix) =>
        $"{network >> 24}.{(network >> 16) & 255}.{(network >> 8) & 255}.{network & 255}/{prefix}";

    public static bool Contains(string cidr, string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        try
        {
            var (net, prefix) = Parse(cidr);
            var b = a.GetAddressBytes();
            uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
            return (v & mask) == net;
        }
        catch { return false; }
    }

    /// <summary>The CIDR blocks covering <paramref name="cidr"/> minus the single address <paramref name="ip"/>.</summary>
    public static List<string> Subtract(string cidr, string ip)
    {
        var (net, prefix) = Parse(cidr);
        var (target, _) = Parse(ip + "/32");
        var result = new List<string>();
        if (!Contains(cidr, ip)) { result.Add(Format(net, prefix)); return result; }
        for (int p = prefix + 1; p <= 32; p++)
        {
            // at each level keep the half that does NOT contain the target
            uint bit = 1u << (32 - p);
            uint half = (target & (p == 0 ? 0 : uint.MaxValue << (32 - p))) ^ bit;
            result.Add(Format(half, p));
        }
        return result;
    }
}
