using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AfifPbx.Data;
using AfifPbx.Sip;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AfifPbx.Bridge;

/// <summary>Saved bridge configuration (bridge.json in the data folder; secrets encrypted for this PC).</summary>
public sealed class BridgeConfig
{
    public bool Enabled { get; set; }
    public string Zone { get; set; } = "";
    public string PanelHostname { get; set; } = "";
    public string SipHostname { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ZoneId { get; set; } = "";
    public string TunnelId { get; set; } = "";
    public string TunnelName { get; set; } = "afif-pbx";
    public string LanIp { get; set; } = "";
    public string TeamDomain { get; set; } = "";
    public string ProtectedApiToken { get; set; } = "";
    public string ProtectedTunnelToken { get; set; } = "";
    public DateTime? LastSetupUtc { get; set; }
    public List<string> LastSetupLog { get; set; } = new();
    public bool SplitTunnelAdjusted { get; set; }
}

public sealed class BridgeStatus
{
    public bool Enabled { get; set; }
    public bool Configured { get; set; }
    public bool ConnectorInstalled { get; set; }
    public bool Running { get; set; }
    public bool Connected { get; set; }
    public int Connections { get; set; }
    public string Message { get; set; } = "";
    public string Zone { get; set; } = "";
    public string PanelUrl { get; set; } = "";
    public string SipHostname { get; set; } = "";
    public string LanIp { get; set; } = "";
    public string TeamDomain { get; set; } = "";
    public string TunnelId { get; set; } = "";
    public bool SplitTunnelAdjusted { get; set; }
    public DateTime? LastSetupUtc { get; set; }
    public List<string> LastSetupLog { get; set; } = new();
    public List<string> RecentConnectorLog { get; set; } = new();
}

/// <summary>
/// The AFIF Bridge: sets up a Cloudflare Tunnel for AFIF-PBX (its own tunnel, separate from any
/// others in the account) and runs the cloudflared connector so the panel is reachable at
/// https://pbx.&lt;domain&gt; and phones with the Cloudflare One app can reach SIP/RTP — no open ports.
/// </summary>
public sealed class BridgeManager : IDisposable
{
    private readonly string _dataDir;
    private readonly string _path;
    private readonly ILogger _log;
    private readonly Func<int> _httpPort;
    private readonly Func<HttpMessageHandler?> _handler;
    private readonly object _lock = new();
    private readonly Queue<string> _connectorLog = new();
    private BridgeConfig _cfg;
    private Process? _proc;
    private CancellationTokenSource? _supervisor;
    private const int MetricsPort = 20241;

    public string ConnectorPath { get; set; }

    public BridgeManager(string dataDir, Func<int> httpPort, ILogger? log = null, Func<HttpMessageHandler?>? handler = null)
    {
        _dataDir = dataDir;
        _path = Path.Combine(dataDir, "bridge.json");
        _httpPort = httpPort;
        _log = log ?? NullLogger.Instance;
        _handler = handler ?? (() => null);
        ConnectorPath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");
        _cfg = File.Exists(_path) ? JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(_path)) ?? new() : new();
    }

    public BridgeConfig Config { get { lock (_lock) return JsonSerializer.Deserialize<BridgeConfig>(JsonSerializer.Serialize(_cfg))!; } }

    private void Save()
    {
        lock (_lock) File.WriteAllText(_path, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------------------------ secrets
    internal static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine));
        return "plain:" + Convert.ToBase64String(bytes);
    }

    internal static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (stored.StartsWith("dpapi:") && OperatingSystem.IsWindows())
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.LocalMachine));
        if (stored.StartsWith("plain:")) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
        return "";
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AFIF-PBX bridge");

    // ------------------------------------------------------------------ setup

    public sealed record SetupRequest(string ApiToken, string Zone, string PanelHostname, string SipHostname, string? LanIp, bool AdjustSplitTunnel = true);

    /// <summary>Full automatic setup through the Cloudflare API. Returns the step log.</summary>
    public async Task<(bool Ok, List<string> Log)> SetupAsync(SetupRequest req)
    {
        var log = new List<string>();
        void Step(string s) { log.Add(s); _log.LogInformation("Bridge: {Step}", s); }
        try
        {
            string zone = req.Zone.Trim().TrimEnd('.').ToLowerInvariant();
            string panel = Fqdn(req.PanelHostname, zone);
            string sip = Fqdn(req.SipHostname, zone);
            string lanIp = string.IsNullOrWhiteSpace(req.LanIp) ? SipServer.LocalAddresses().FirstOrDefault() ?? "" : req.LanIp.Trim();
            if (lanIp.Length == 0) throw new CloudflareException("Could not find this PC's LAN address");

            var api = new CloudflareApi(req.ApiToken, _handler());
            var z = await api.FindZoneAsync(zone);
            Step($"✓ Found {z.Name} in Cloudflare account {z.AccountName}".TrimEnd());

            var (tunnelId, created) = await api.EnsureTunnelAsync(z.AccountId, "afif-pbx");
            Step(created ? $"✓ Created tunnel \"afif-pbx\" ({tunnelId[..8]}…)" : $"✓ Using existing tunnel \"afif-pbx\" ({tunnelId[..8]}…)");

            int port = _httpPort();
            await api.PutTunnelConfigAsync(z.AccountId, tunnelId, new[] { (panel, $"http://localhost:{port}") });
            Step($"✓ Tunnel serves https://{panel} → this PC's web panel, with private-network routing on");

            var r1 = await api.UpsertDnsAsync(z.Id, "CNAME", panel, $"{tunnelId}.cfargotunnel.com", true, "AFIF-PBX web panel (tunnel)");
            Step($"✓ DNS {panel} {r1}");
            var r2 = await api.UpsertDnsAsync(z.Id, "A", sip, lanIp, false, "AFIF-PBX SIP via Cloudflare One");
            Step($"✓ DNS {sip} → {lanIp} {r2} (reachable when the Cloudflare One app is on)");

            var r3 = await api.EnsureRouteAsync(z.AccountId, tunnelId, lanIp + "/32");
            Step($"✓ Private route {lanIp}/32 through the tunnel {r3}");

            bool splitOk = false;
            if (req.AdjustSplitTunnel)
            {
                try
                {
                    var excl = await api.GetSplitTunnelExcludeAsync(z.AccountId);
                    var covering = excl.Where(e => e.Address.Contains('/') && Cidr.Contains(e.Address, lanIp)).ToList();
                    if (covering.Count == 0) { Step("✓ Split tunnel already sends the PBX address through Cloudflare"); splitOk = true; }
                    else
                    {
                        var updated = excl.Except(covering).ToList();
                        foreach (var c in covering)
                            updated.AddRange(Cidr.Subtract(c.Address, lanIp).Select(a => new CloudflareApi.SplitEntry(a, $"{c.Description} (AFIF-PBX split)".Trim())));
                        await api.PutSplitTunnelExcludeAsync(z.AccountId, updated);
                        Step($"✓ Split tunnel: {lanIp} now goes through Cloudflare (was excluded by {string.Join(", ", covering.Select(c => c.Address))})");
                        splitOk = true;
                    }
                }
                catch (CloudflareException ex)
                {
                    Step($"! Could not adjust the split tunnel automatically ({ex.Message}). In Zero Trust → Settings → WARP Client → Device settings → Split Tunnels, remove the range containing {lanIp} (usually 192.168.0.0/16).");
                }
            }

            var team = await api.TeamDomainAsync(z.AccountId);
            if (team is null) Step("! Zero Trust is not set up yet: open one.dash.cloudflare.com once and pick a team name (free plan), then run setup again for the phone app.");
            else Step($"✓ Cloudflare One team: {team.Split('.')[0]}");

            var token = await api.TunnelTokenAsync(z.AccountId, tunnelId);
            Step("✓ Got the tunnel connector token");

            lock (_lock)
            {
                _cfg.Enabled = true;
                _cfg.Zone = zone; _cfg.PanelHostname = panel; _cfg.SipHostname = sip;
                _cfg.AccountId = z.AccountId; _cfg.ZoneId = z.Id; _cfg.TunnelId = tunnelId; _cfg.LanIp = lanIp;
                _cfg.TeamDomain = team ?? "";
                _cfg.ProtectedApiToken = Protect(req.ApiToken.Trim());
                _cfg.ProtectedTunnelToken = Protect(token);
                _cfg.SplitTunnelAdjusted = splitOk;
                _cfg.LastSetupUtc = DateTime.UtcNow;
                _cfg.LastSetupLog = log;
            }
            Save();
            Restart();
            Step("✓ Bridge connector started");
            return (true, log);
        }
        catch (Exception ex) when (ex is CloudflareException or HttpRequestException or TaskCanceledException)
        {
            Step("✗ " + ex.Message);
            lock (_lock) { _cfg.LastSetupLog = log; _cfg.LastSetupUtc = DateTime.UtcNow; }
            Save();
            return (false, log);
        }
    }

    /// <summary>Manual mode: use a tunnel token created in the Cloudflare dashboard.</summary>
    public void UseTunnelToken(string token, string panelHostname)
    {
        lock (_lock)
        {
            _cfg.ProtectedTunnelToken = Protect(token.Trim());
            _cfg.PanelHostname = panelHostname.Trim();
            _cfg.Enabled = true;
            _cfg.LanIp = SipServer.LocalAddresses().FirstOrDefault() ?? "";
            _cfg.LastSetupUtc = DateTime.UtcNow;
            _cfg.LastSetupLog = new() { "✓ Tunnel token saved (manual mode — public hostnames and private routes are managed in the Cloudflare dashboard)" };
        }
        Save();
        Restart();
    }

    public void SetEnabled(bool on)
    {
        lock (_lock) _cfg.Enabled = on;
        Save();
        if (on) Restart(); else StopConnector();
    }

    /// <summary>Forget the bridge; with the saved API token also remove AFIF-PBX's DNS records.</summary>
    public async Task<List<string>> RemoveAsync()
    {
        var log = new List<string>();
        StopConnector();
        var c = Config;
        var token = Unprotect(c.ProtectedApiToken);
        if (token.Length > 0 && c.ZoneId.Length > 0)
        {
            try
            {
                var api = new CloudflareApi(token, _handler());
                await api.DeleteDnsIfOursAsync(c.ZoneId, c.PanelHostname);
                await api.DeleteDnsIfOursAsync(c.ZoneId, c.SipHostname);
                log.Add("✓ Removed AFIF-PBX DNS records (the tunnel itself is kept in Cloudflare; delete it there if you no longer need it)");
            }
            catch (Exception ex) { log.Add("! " + ex.Message); }
        }
        lock (_lock) _cfg = new BridgeConfig();
        Save();
        log.Add("✓ Bridge settings removed from this PC");
        return log;
    }

    // ------------------------------------------------------------------ connector

    public void Start()
    {
        if (Config.Enabled && Config.ProtectedTunnelToken.Length > 0) Restart();
    }

    private void Restart()
    {
        StopConnector();
        var cts = new CancellationTokenSource();
        _supervisor = cts;
        _ = Task.Run(() => SuperviseAsync(cts.Token));
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        int delay = 2;
        while (!ct.IsCancellationRequested)
        {
            if (!File.Exists(ConnectorPath))
            {
                AddLog($"cloudflared not found at {ConnectorPath}");
                await Task.Delay(30000, ct).ContinueWith(_ => { });
                continue;
            }
            var token = Unprotect(Config.ProtectedTunnelToken);
            if (token.Length == 0) return;
            var psi = new ProcessStartInfo(ConnectorPath,
                $"tunnel --no-autoupdate --metrics 127.0.0.1:{MetricsPort} --loglevel info run")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.Environment["TUNNEL_TOKEN"] = token; // keeps the token off the command line
            try
            {
                var p = Process.Start(psi)!;
                _proc = p;
                p.OutputDataReceived += (_, e) => { if (e.Data is not null) AddLog(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data is not null) AddLog(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _log.LogInformation("Bridge connector started (pid {Pid})", p.Id);
                await p.WaitForExitAsync(ct).ContinueWith(_ => { });
                if (ct.IsCancellationRequested) return;
                _log.LogWarning("Bridge connector exited ({Code}); restarting in {Delay}s", p.ExitCode, delay);
            }
            catch (Exception ex) { AddLog("Could not start cloudflared: " + ex.Message); }
            await Task.Delay(delay * 1000, ct).ContinueWith(_ => { });
            delay = Math.Min(60, delay * 2);
        }
    }

    private void StopConnector()
    {
        _supervisor?.Cancel();
        _supervisor = null;
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        _proc = null;
    }

    private void AddLog(string line)
    {
        // strip ANSI colours and timestamps cloudflared prints
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\x1B\[[0-9;]*m", "");
        lock (_connectorLog)
        {
            _connectorLog.Enqueue(line);
            while (_connectorLog.Count > 200) _connectorLog.Dequeue();
        }
    }

    public async Task<BridgeStatus> StatusAsync()
    {
        var c = Config;
        var s = new BridgeStatus
        {
            Enabled = c.Enabled,
            Configured = c.ProtectedTunnelToken.Length > 0,
            ConnectorInstalled = File.Exists(ConnectorPath),
            Running = _proc is { HasExited: false },
            Zone = c.Zone,
            PanelUrl = c.PanelHostname.Length > 0 ? "https://" + c.PanelHostname : "",
            SipHostname = c.SipHostname,
            LanIp = c.LanIp,
            TeamDomain = c.TeamDomain,
            TunnelId = c.TunnelId,
            SplitTunnelAdjusted = c.SplitTunnelAdjusted,
            LastSetupUtc = c.LastSetupUtc,
            LastSetupLog = c.LastSetupLog,
        };
        lock (_connectorLog) s.RecentConnectorLog = _connectorLog.Reverse().Take(40).ToList();
        if (s.Running)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var txt = await http.GetStringAsync($"http://127.0.0.1:{MetricsPort}/ready");
                using var doc = JsonDocument.Parse(txt);
                s.Connections = doc.RootElement.TryGetProperty("readyConnections", out var rc) ? rc.GetInt32() : 0;
                s.Connected = s.Connections > 0;
            }
            catch { s.Connected = false; }
        }
        s.Message = !s.Configured ? "Not set up"
            : !s.Enabled ? "Switched off"
            : !s.ConnectorInstalled ? "cloudflared is missing — reinstall AFIF-PBX"
            : !s.Running ? "Connector starting…"
            : s.Connected ? $"Connected to Cloudflare ({s.Connections} links)"
            : "Connecting to Cloudflare…";
        return s;
    }

    public void Dispose() => StopConnector();

    private static string Fqdn(string host, string zone)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) throw new CloudflareException("Enter a host name");
        return host.EndsWith("." + zone) || host == zone ? host : $"{host}.{zone}";
    }
}
