using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AfifPbx.Bridge;
using AfifPbx.Sip;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AfifPbx.Remote;

/// <summary>Saved Tailscale settings (tailscale.json in the data folder; the Cloudflare token is encrypted for this PC).</summary>
public sealed class TailscaleConfig
{
    /// <summary>The built-in secure network is switched on (AFIF-PBX keeps its engine running).</summary>
    public bool Enabled { get; set; }
    /// <summary>Name of this PC on the tailnet (becomes afif-pbx.&lt;tailnet&gt;.ts.net).</summary>
    public string Hostname { get; set; } = "afif-pbx";
    /// <summary>Full name on your own domain that points at this PC's Tailscale address, e.g. voip.powerchain.uk.</summary>
    public string Domain { get; set; } = "";
    public string ProtectedApiToken { get; set; } = "";
    public string LastDnsAddress { get; set; } = "";
    public DateTime? LastDnsUpdateUtc { get; set; }
    public List<string> LastDnsLog { get; set; } = new();
}

public sealed record TailscalePeer(string HostName, string DnsName, string Os, bool Online, string Address, string Connection, DateTime? LastSeen);

public sealed class TailscaleStatus
{
    public bool Installed { get; set; }
    public bool DaemonRunning { get; set; }
    /// <summary>NoState, NeedsLogin, NeedsMachineAuth, Stopped, Starting, Running</summary>
    public string BackendState { get; set; } = "";
    public bool Connected => BackendState == "Running";
    public string AuthUrl { get; set; } = "";
    public string HostName { get; set; } = "";
    public string DnsName { get; set; } = "";
    public string Ipv4 { get; set; } = "";
    public string Ipv6 { get; set; } = "";
    public string Tailnet { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime? KeyExpiry { get; set; }
    public List<string> Health { get; set; } = new();
    public List<TailscalePeer> Peers { get; set; } = new();
    public string Message { get; set; } = "";
    // our own settings
    /// <summary>The name AFIF-PBX asks Tailscale to use for this PC.</summary>
    public string WantedName { get; set; } = "";
    public string Domain { get; set; } = "";
    public bool HasApiToken { get; set; }
    public bool Enabled { get; set; }
    /// <summary>UDP ports the PBX answers on over the secure network (SIP + voice).</summary>
    public int RelayPorts { get; set; }
    public List<string> RelayErrors { get; set; } = new();
    public bool LoginInProgress { get; set; }
    public string DomainResolvesTo { get; set; } = "";
    public bool DomainOk { get; set; }
    public DateTime? LastDnsUpdateUtc { get; set; }
    public List<string> LastDnsLog { get; set; } = new();
}

/// <summary>
/// AFIF-PBX's built-in secure network. Runs afif-net (a Tailscale node that lives only inside that process — no
/// network adapter, VPN or route is added to Windows) and lets phones on your tailnet reach the PBX's SIP and voice
/// ports, which afif-net relays to AFIF-PBX on 127.0.0.1. Also keeps a DNS record on your own domain
/// (e.g. voip.powerchain.uk) pointing at the PBX's tailnet address.
/// </summary>
public sealed class TailscaleManager : IDisposable
{
    private readonly string _path, _stateDir;
    private readonly ILogger _log;
    private readonly Func<HttpMessageHandler?> _handler;
    private readonly SipServer? _sip;
    private readonly Func<(int Start, int End)> _rtpRange;
    private readonly object _lock = new();
    private TailscaleConfig _cfg;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    private Process? _proc;
    private string _control = "";
    private string _pendingAuthKey = "";
    private DateTime _nextStart = DateTime.MinValue;
    private int _failures;

    /// <summary>The engine (afif-net.exe next to AFIF-PBX). Settable for tests.</summary>
    public string EnginePath { get; set; }
    /// <summary>Extra engine arguments (tests use "-plain-ip 127.0.0.2" instead of a real tailnet).</summary>
    public string ExtraArgs { get; set; } = "";

    public TailscaleManager(string dataDir, ILogger? log = null, Func<HttpMessageHandler?>? handler = null,
        SipServer? sip = null, Func<(int Start, int End)>? rtpRange = null)
    {
        _path = Path.Combine(dataDir, "tailscale.json");
        _stateDir = Path.Combine(dataDir, "secure-network");
        _log = log ?? NullLogger.Instance;
        _handler = handler ?? (() => null);
        _sip = sip;
        _rtpRange = rtpRange ?? (() => (21000, 21199));
        EnginePath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "afif-net.exe" : "afif-net");
        _cfg = File.Exists(_path) ? JsonSerializer.Deserialize<TailscaleConfig>(File.ReadAllText(_path)) ?? new() : new();
        // the Cloudflare token saved by the old AFIF Bridge (removed) keeps working for the DNS record
        try
        {
            var bridge = Path.Combine(dataDir, "bridge.json");
            if (_cfg.ProtectedApiToken.Length == 0 && File.Exists(bridge) &&
                JsonNode.Parse(File.ReadAllText(bridge))?["ProtectedApiToken"]?.GetValue<string>() is { Length: > 0 } old)
            {
                _cfg.ProtectedApiToken = old;
                Save();
            }
        }
        catch { }
    }

    public TailscaleConfig Config { get { lock (_lock) return JsonSerializer.Deserialize<TailscaleConfig>(JsonSerializer.Serialize(_cfg))!; } }

    private void Save()
    {
        lock (_lock) File.WriteAllText(_path, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------------------------ engine process

    private bool EngineRunning { get { lock (_lock) return _proc is { HasExited: false } && _control.Length > 0; } }

    private void StartEngine()
    {
        lock (_lock)
        {
            if (_proc is { HasExited: false }) return;
            if (!File.Exists(EnginePath)) return;
            Directory.CreateDirectory(_stateDir);
            var (rs, re) = _rtpRange();
            string sipMap = _sip?.RelaySipMap ?? "";
            var psi = new ProcessStartInfo(EnginePath,
                $"-dir \"{_stateDir}\" -hostname {CleanHostname(_cfg.Hostname)} {(sipMap.Length > 0 ? "-sip " + sipMap : "")} -rtp {rs}-{re} -watch-stdin {ExtraArgs}".Trim())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.Environment["AFIF_NET_TOKEN"] = _token;
            if (_pendingAuthKey.Length > 0) psi.Environment["TS_AUTHKEY"] = _pendingAuthKey;
            _pendingAuthKey = "";
            _control = "";
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) =>
            {
                var line = e.Data ?? "";
                if (line.StartsWith("CONTROL ")) lock (_lock) _control = line[8..].Trim();
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogInformation("Secure network: {Line}", e.Data);
            };
            p.Exited += (_, _) =>
            {
                lock (_lock) { _control = ""; }
                if (_sip is not null) _sip.TailnetAddress = null;
            };
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _proc = p;
                _log.LogInformation("Secure network engine started (pid {Pid})", p.Id);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Secure network engine could not start: {Message}", ex.Message);
            }
        }
    }

    private void StopEngine()
    {
        Process? p;
        lock (_lock) { p = _proc; _proc = null; _control = ""; }
        if (_sip is not null) _sip.TailnetAddress = null;
        if (p is null) return;
        try { p.StandardInput.Close(); } catch { }
        try { if (!p.WaitForExit(3000)) p.Kill(true); } catch { }
        try { p.Dispose(); } catch { }
    }

    private async Task<bool> WaitForControlAsync(int ms)
    {
        for (int i = 0; i < ms / 100; i++)
        {
            if (EngineRunning) return true;
            await Task.Delay(100).ConfigureAwait(false);
        }
        return EngineRunning;
    }

    private async Task<(bool Ok, string Body)> CallAsync(HttpMethod method, string path)
    {
        string control;
        lock (_lock) control = _control;
        if (control.Length == 0) return (false, "not running");
        try
        {
            using var req = new HttpRequestMessage(method, $"http://{control}{path}");
            req.Headers.Add("X-Afif-Token", _token);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            return (resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ------------------------------------------------------------------ status

    public async Task<TailscaleStatus> StatusAsync(bool checkDomain = true)
    {
        var c = Config;
        var st = new TailscaleStatus { Installed = File.Exists(EnginePath), Enabled = c.Enabled, Version = "built in" };
        if (!st.Installed) st.Message = "The secure network engine (afif-net) is missing — reinstall AFIF-PBX.";
        else if (!c.Enabled) st.Message = "Switched off";
        else if (!EngineRunning) st.Message = "Starting the secure network…";
        else
        {
            var (ok, json) = await CallAsync(HttpMethod.Get, "/status").ConfigureAwait(false);
            if (ok)
            {
                try { Parse(json, st); st.DaemonRunning = true; }
                catch (Exception ex) { st.Message = "Could not read the secure network status: " + ex.Message; }
            }
            else st.Message = "The secure network is starting…";
            var (rok, rjson) = await CallAsync(HttpMethod.Get, "/relay").ConfigureAwait(false);
            if (rok)
            {
                try
                {
                    var r = JsonNode.Parse(rjson)!;
                    st.RelayPorts = (r["ports"] as JsonArray)?.Count ?? 0;
                    st.RelayErrors = (r["errors"] as JsonArray)?.Select(e => e?.ToString() ?? "").Where(e => e.Length > 0).ToList() ?? new();
                }
                catch { }
            }
            if (string.IsNullOrEmpty(st.Message))
                st.Message = st.BackendState switch
                {
                    "Running" => "Connected",
                    "NeedsLogin" => "Sign in to connect AFIF-PBX to your tailnet",
                    "NeedsMachineAuth" => "Waiting for approval in the Tailscale admin console",
                    "Stopped" => "Stopped",
                    "Starting" or "NoState" => "Connecting…",
                    _ => st.BackendState,
                };
        }
        st.LoginInProgress = c.Enabled && st.AuthUrl.Length > 0;
        st.WantedName = c.Hostname;
        st.Domain = c.Domain;
        st.HasApiToken = !string.IsNullOrEmpty(c.ProtectedApiToken);
        st.LastDnsUpdateUtc = c.LastDnsUpdateUtc;
        st.LastDnsLog = c.LastDnsLog;
        if (checkDomain && !string.IsNullOrWhiteSpace(c.Domain))
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(c.Domain).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                st.DomainResolvesTo = string.Join(", ", addrs.Select(a => a.ToString()));
                st.DomainOk = !string.IsNullOrEmpty(st.Ipv4) && addrs.Any(a => a.ToString() == st.Ipv4);
            }
            catch { st.DomainResolvesTo = "not found yet"; }
        }
        return st;
    }

    /// <summary>Fill <paramref name="st"/> from the output of <c>tailscale status --json</c>.</summary>
    public static void Parse(string json, TailscaleStatus st)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        st.BackendState = S(root["BackendState"]);
        st.AuthUrl = S(root["AuthURL"]);
        var ips = (root["TailscaleIPs"] as JsonArray)?.Select(S).ToList() ?? new List<string>();
        st.Ipv4 = ips.FirstOrDefault(i => i.Contains('.')) ?? "";
        st.Ipv6 = ips.FirstOrDefault(i => i.Contains(':')) ?? "";
        st.Tailnet = S(root["CurrentTailnet"]?["Name"]);
        if (root["Health"] is JsonArray health) st.Health = health.Select(S).Where(h => h.Length > 0).ToList();
        if (root["Self"] is JsonObject self)
        {
            st.HostName = S(self["HostName"]);
            st.DnsName = S(self["DNSName"]).TrimEnd('.');
            if (DateTime.TryParse(S(self["KeyExpiry"]), out var exp)) st.KeyExpiry = exp.ToUniversalTime();
        }
        if (root["Peer"] is JsonObject peers)
        {
            foreach (var (_, node) in peers)
            {
                if (node is not JsonObject p) continue;
                var pIps = (p["TailscaleIPs"] as JsonArray)?.Select(S).ToList() ?? new List<string>();
                string cur = S(p["CurAddr"]), relay = S(p["Relay"]);
                string conn = cur.Length > 0 ? "direct" : relay.Length > 0 ? "relay " + relay : "";
                DateTime? seen = DateTime.TryParse(S(p["LastSeen"]), out var ls) && ls.Year > 2000 ? ls.ToUniversalTime() : null;
                st.Peers.Add(new TailscalePeer(S(p["HostName"]), S(p["DNSName"]).TrimEnd('.'), S(p["OS"]),
                    p["Online"] is JsonValue on && on.TryGetValue<bool>(out var o) && o,
                    pIps.FirstOrDefault(i => i.Contains('.')) ?? "", conn, seen));
            }
            st.Peers = st.Peers.OrderByDescending(p => p.Online).ThenBy(p => p.HostName).ToList();
        }
    }

    // ------------------------------------------------------------------ connect

    private static string CleanHostname(string h)
    {
        h = Regex.Replace((h ?? "").Trim().ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-');
        return h.Length == 0 ? "afif-pbx" : h.Length > 63 ? h[..63] : h;
    }

    /// <summary>
    /// Switch the secure network on. With an auth key AFIF-PBX joins straight away; without one it returns a
    /// sign-in link to open in a browser (any device).
    /// </summary>
    public async Task<(bool Ok, string Message, string AuthUrl)> ConnectAsync(string? authKey, string? hostname)
    {
        if (!File.Exists(EnginePath)) return (false, "The secure network engine is missing — reinstall AFIF-PBX.", "");
        string host = CleanHostname(string.IsNullOrWhiteSpace(hostname) ? Config.Hostname : hostname!);
        bool restart;
        lock (_lock)
        {
            restart = _cfg.Hostname != host || !string.IsNullOrWhiteSpace(authKey);
            _cfg.Hostname = host;
            _cfg.Enabled = true;
            _pendingAuthKey = (authKey ?? "").Trim();
            _failures = 0;
            _nextStart = DateTime.MinValue;
        }
        Save();
        if (restart) StopEngine();
        StartEngine();
        if (!await WaitForControlAsync(15000).ConfigureAwait(false))
            return (false, "The secure network engine did not start. See the AFIF-PBX log.", "");

        for (int i = 0; i < 60; i++)
        {
            var st = await StatusAsync(false).ConfigureAwait(false);
            if (st.Connected) { await RefreshAddressAsync(st).ConfigureAwait(false); return (true, "Connected.", ""); }
            if (st.AuthUrl.Length > 0) return (true, "Open the link to sign in.", st.AuthUrl);
            if (i == 6 && st.BackendState == "NeedsLogin") await CallAsync(HttpMethod.Post, "/login").ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);
        }
        return (false, "The secure network did not answer in time. Check the internet connection of this PC.", "");
    }

    /// <summary>Sign out and switch the secure network off.</summary>
    public async Task<(bool Ok, string Message)> LogoutAsync()
    {
        if (EngineRunning) await CallAsync(HttpMethod.Post, "/logout").ConfigureAwait(false);
        lock (_lock) _cfg.Enabled = false;
        Save();
        StopEngine();
        return (true, "Signed out and switched off.");
    }

    private async Task RefreshAddressAsync(TailscaleStatus st)
    {
        if (_sip is null) return;
        IPAddress? ip = st.Connected && IPAddress.TryParse(st.Ipv4, out var a) ? a : null;
        if (!Equals(_sip.TailnetAddress, ip))
        {
            _sip.TailnetAddress = ip;
            _log.LogInformation("Secure network address: {Ip}", ip?.ToString() ?? "(none)");
        }
        var c = Config;
        if (ip is not null && c.Domain.Length > 0 && c.LastDnsAddress != ip.ToString() &&
            (c.LastDnsUpdateUtc is null || DateTime.UtcNow - c.LastDnsUpdateUtc > TimeSpan.FromMinutes(1)))
            await SetDomainAsync(c.Domain, null).ConfigureAwait(false);
    }

    /// <summary>Keep the engine running while switched on, and the PBX and DNS told about its address.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var c = Config;
                    if (c.Enabled && File.Exists(EnginePath))
                    {
                        bool alive;
                        lock (_lock) alive = _proc is { HasExited: false };
                        if (!alive && DateTime.UtcNow >= _nextStart)
                        {
                            lock (_lock)
                            {
                                if (_proc is not null) { _failures++; _proc = null; }
                                _nextStart = DateTime.UtcNow.AddSeconds(Math.Min(60, 2 << Math.Min(5, _failures)));
                            }
                            StartEngine();
                        }
                        if (EngineRunning) await RefreshAddressAsync(await StatusAsync(false).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { _log.LogWarning("Secure network check failed: {Message}", ex.Message); }
                try { await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token).ConfigureAwait(false); } catch { break; }
            }
        });
    }

    // ------------------------------------------------------------------ your domain

    /// <summary>
    /// Point <paramref name="fqdn"/> (e.g. voip.powerchain.uk) at the PBX's tailnet address with a DNS-only A record
    /// (Cloudflare DNS API, token with Zone · DNS · Edit).
    /// </summary>
    public async Task<(bool Ok, List<string> Log)> SetDomainAsync(string fqdn, string? apiToken)
    {
        var log = new List<string>();
        fqdn = (fqdn ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (fqdn.StartsWith("http")) fqdn = new Uri(fqdn).Host;
        if (!Regex.IsMatch(fqdn, @"^([a-z0-9-]+\.)+[a-z]{2,}$")) { log.Add("✗ Enter a full name such as voip.powerchain.uk"); return (false, log); }

        string token = (apiToken ?? "").Trim();
        if (token.Length == 0) token = Secrets.Unprotect(Config.ProtectedApiToken);
        lock (_lock)
        {
            _cfg.Domain = fqdn;
            if ((apiToken ?? "").Trim().Length > 0) _cfg.ProtectedApiToken = Secrets.Protect(apiToken!.Trim());
        }
        Save();

        var st = await StatusAsync(false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(st.Ipv4))
        {
            log.Add("✗ AFIF-PBX has no secure network address yet — connect it to your tailnet first.");
            return Finish(false, log);
        }
        if (token.Length == 0)
        {
            log.Add($"• No DNS token: create this record yourself at your DNS provider: type A, name {fqdn}, IPv4 {st.Ipv4} (Cloudflare: proxy status DNS only).");
            return Finish(false, log);
        }
        var api = new CloudflareApi(token, _handler());
        try
        {
            CloudflareApi.Zone? zone = null;
            CloudflareException? last = null;
            var labels = fqdn.Split('.');
            for (int i = 0; i <= labels.Length - 2 && zone is null; i++)
            {
                try { zone = await api.FindZoneAsync(string.Join('.', labels[i..])).ConfigureAwait(false); }
                catch (CloudflareException ex) { last = ex; if (ex.Message.Contains("permission")) break; }
            }
            if (zone is null) throw last ?? new CloudflareException($"No domain for {fqdn} was found with this token.");
            log.Add($"✓ Domain {zone.Name} found");
            var r = await api.UpsertDnsAsync(zone.Id, "A", fqdn, st.Ipv4, false, "AFIF-PBX SIP via secure network").ConfigureAwait(false);
            log.Add($"✓ DNS {fqdn} → {st.Ipv4} {r} (reachable from AFIF Phone with its secure network on)");
            lock (_lock) { _cfg.LastDnsAddress = st.Ipv4; _cfg.LastDnsUpdateUtc = DateTime.UtcNow; }
            return Finish(true, log);
        }
        catch (Exception ex)
        {
            log.Add("✗ " + ex.Message);
            lock (_lock) _cfg.LastDnsUpdateUtc = DateTime.UtcNow;
            return Finish(false, log);
        }
    }

    private (bool, List<string>) Finish(bool ok, List<string> log)
    {
        lock (_lock) _cfg.LastDnsLog = log.ToList();
        Save();
        return (ok, log);
    }

    public void ClearDomain()
    {
        lock (_lock) { _cfg.Domain = ""; _cfg.LastDnsAddress = ""; _cfg.LastDnsLog = new(); }
        Save();
    }

    public void Dispose()
    {
        _cts.Cancel();
        StopEngine();
        _http.Dispose();
    }
}
