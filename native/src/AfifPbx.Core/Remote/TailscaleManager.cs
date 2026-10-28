using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AfifPbx.Bridge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AfifPbx.Remote;

/// <summary>Saved Tailscale settings (tailscale.json in the data folder; the Cloudflare token is encrypted for this PC).</summary>
public sealed class TailscaleConfig
{
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
    public bool BridgeTokenAvailable { get; set; }
    public bool InstallerAvailable { get; set; }
    public bool LoginInProgress { get; set; }
    public string DomainResolvesTo { get; set; } = "";
    public bool DomainOk { get; set; }
    public DateTime? LastDnsUpdateUtc { get; set; }
    public List<string> LastDnsLog { get; set; } = new();
}

/// <summary>
/// Runs AFIF-PBX on your Tailscale network: controls the Tailscale client on this PC (install, sign-in,
/// unattended mode), reports the tailnet address and the phones that can reach it, and keeps a DNS record on
/// your own domain (e.g. voip.powerchain.uk) pointing at the PBX's Tailscale address so Linphone can use a
/// friendly name. Voice (SIP + RTP over UDP) then flows phone ⇄ PBX directly over WireGuard.
/// </summary>
public sealed class TailscaleManager : IDisposable
{
    private readonly string _path;
    private readonly ILogger _log;
    private readonly Func<HttpMessageHandler?> _handler;
    private readonly Func<string> _bridgeToken;
    private readonly object _lock = new();
    private TailscaleConfig _cfg;
    private Process? _login;
    private string _loginUrl = "";
    private readonly CancellationTokenSource _cts = new();

    /// <summary>tailscale.exe (CLI). Settable for tests.</summary>
    public string CliPath { get; set; }
    /// <summary>The Tailscale MSI shipped with AFIF-PBX (installed on request).</summary>
    public string InstallerPath { get; set; }

    public TailscaleManager(string dataDir, ILogger? log = null, Func<HttpMessageHandler?>? handler = null, Func<string>? bridgeToken = null)
    {
        _path = Path.Combine(dataDir, "tailscale.json");
        _log = log ?? NullLogger.Instance;
        _handler = handler ?? (() => null);
        _bridgeToken = bridgeToken ?? (() => "");
        CliPath = DefaultCliPath();
        InstallerPath = Path.Combine(AppContext.BaseDirectory, "deps", "tailscale.msi");
        _cfg = File.Exists(_path) ? JsonSerializer.Deserialize<TailscaleConfig>(File.ReadAllText(_path)) ?? new() : new();
    }

    public static string DefaultCliPath()
    {
        if (!OperatingSystem.IsWindows()) return "/usr/bin/tailscale";
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\Program Files" })
        {
            var p = Path.Combine(root, "Tailscale", "tailscale.exe");
            if (File.Exists(p)) return p;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
    }

    public TailscaleConfig Config { get { lock (_lock) return JsonSerializer.Deserialize<TailscaleConfig>(JsonSerializer.Serialize(_cfg))!; } }

    private void Save()
    {
        lock (_lock) File.WriteAllText(_path, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------------------------ CLI

    private async Task<(int Code, string Out)> RunAsync(string args, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo(CliPath, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { return (-1, ex.Message); }
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-2, "timed out"); }
        return (p.ExitCode, ((await o) + "\n" + (await e)).Trim());
    }

    // ------------------------------------------------------------------ status

    public async Task<TailscaleStatus> StatusAsync(bool checkDomain = true)
    {
        var c = Config;
        var st = new TailscaleStatus();
        if (File.Exists(CliPath))
        {
            st.Installed = true;
            var (code, json) = await RunAsync("status --json", 10000).ConfigureAwait(false);
            int brace = json.IndexOf('{');
            if (brace >= 0)
            {
                try { Parse(json[brace..], st); st.DaemonRunning = true; }
                catch (Exception ex) { st.Message = "Could not read Tailscale status: " + ex.Message; }
            }
            else st.Message = code == 0 ? "" : "The Tailscale service is not running on this PC.";
            var (_, ver) = await RunAsync("version", 5000).ConfigureAwait(false);
            st.Version = ver.Split('\n').FirstOrDefault()?.Trim() ?? "";
        }
        else st.Message = "Tailscale is not installed on this PC.";

        lock (_lock)
        {
            if (_login is { HasExited: false })
            {
                st.LoginInProgress = true;
                if (string.IsNullOrEmpty(st.AuthUrl)) st.AuthUrl = _loginUrl;
            }
        }
        if (st.Installed && st.DaemonRunning && string.IsNullOrEmpty(st.Message))
            st.Message = st.BackendState switch
            {
                "Running" => "Connected",
                "NeedsLogin" => "Signed out — connect this PC to your tailnet",
                "NeedsMachineAuth" => "Waiting for approval in the Tailscale admin console",
                "Stopped" => "Tailscale is switched off on this PC",
                "Starting" => "Connecting…",
                _ => st.BackendState,
            };

        st.WantedName = c.Hostname;
        st.Domain = c.Domain;
        st.HasApiToken = !string.IsNullOrEmpty(c.ProtectedApiToken);
        st.BridgeTokenAvailable = !string.IsNullOrEmpty(SafeBridgeToken());
        st.InstallerAvailable = File.Exists(InstallerPath);
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

    // ------------------------------------------------------------------ install / connect

    /// <summary>Install the bundled Tailscale MSI silently, set to stay connected without anyone signed in to Windows.</summary>
    public async Task<(bool Ok, string Message)> InstallAsync()
    {
        if (File.Exists(CliPath)) return (true, "Tailscale is already installed.");
        if (!File.Exists(InstallerPath)) return (false, "The Tailscale installer is not included in this AFIF-PBX build.");
        var psi = new ProcessStartInfo("msiexec.exe",
            $"/i \"{InstallerPath}\" /qn /norestart TS_UNATTENDEDMODE=always TS_NOLAUNCH=yes")
        { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return (false, "The Tailscale installer did not finish in 5 minutes."); }
        CliPath = DefaultCliPath();
        bool ok = p.ExitCode is 0 or 3010 && File.Exists(CliPath);
        _log.LogInformation("Tailscale install finished with code {Code}", p.ExitCode);
        return ok ? (true, "Tailscale installed.") : (false, $"The Tailscale installer stopped with code {p.ExitCode}.");
    }

    private static string CleanHostname(string h)
    {
        h = Regex.Replace((h ?? "").Trim().ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-');
        return h.Length == 0 ? "afif-pbx" : h.Length > 63 ? h[..63] : h;
    }

    /// <summary>
    /// Join this PC to the tailnet. With an auth key it connects straight away; without one Tailscale returns a
    /// sign-in link to open in a browser (any device).
    /// </summary>
    public async Task<(bool Ok, string Message, string AuthUrl)> ConnectAsync(string? authKey, string? hostname)
    {
        if (!File.Exists(CliPath)) return (false, "Install Tailscale first.", "");
        string host = CleanHostname(string.IsNullOrWhiteSpace(hostname) ? Config.Hostname : hostname!);
        lock (_lock) { _cfg.Hostname = host; }
        Save();

        var st = await StatusAsync(false).ConfigureAwait(false);
        if (st.Connected)
        {
            var (code, outp) = await RunAsync($"set --hostname={host}").ConfigureAwait(false);
            await RunAsync("set --unattended=true").ConfigureAwait(false); // keep running when nobody is signed in to Windows
            return (code == 0, code == 0 ? "Connected." : Shorten(outp), "");
        }

        if (!string.IsNullOrWhiteSpace(authKey))
        {
            var (code, outp) = await RunAsync($"login --unattended --hostname={host} --auth-key={authKey.Trim()} --timeout=60s", 75000).ConfigureAwait(false);
            if (code != 0) return (false, "Tailscale did not accept the key: " + Shorten(outp), "");
            await RunAsync("set --unattended=true").ConfigureAwait(false);
            _log.LogInformation("Joined the tailnet as {Host} with an auth key", host);
            return (true, "Connected.", "");
        }

        // interactive sign-in: start "tailscale login" and hand its URL to the browser
        lock (_lock)
        {
            if (_login is { HasExited: false } && _loginUrl.Length > 0) return (true, "Open the link to sign in.", _loginUrl);
            try { _login?.Kill(true); } catch { }
            _loginUrl = "";
            var psi = new ProcessStartInfo(CliPath, $"login --unattended --hostname={host}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            _login = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void OnLine(object _, DataReceivedEventArgs e)
            {
                var m = Regex.Match(e.Data ?? "", @"https://\S+");
                if (m.Success) lock (_lock) _loginUrl = m.Value;
            }
            _login.OutputDataReceived += OnLine;
            _login.ErrorDataReceived += OnLine;
            _login.Exited += async (_, _) =>
            {
                try { await RunAsync("set --unattended=true").ConfigureAwait(false); } catch { }
            };
            _login.Start();
            _login.BeginOutputReadLine();
            _login.BeginErrorReadLine();
        }
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            string url;
            lock (_lock) url = _loginUrl;
            if (url.Length == 0)
            {
                var s2 = await StatusAsync(false).ConfigureAwait(false);
                if (s2.Connected) return (true, "Connected.", "");
                url = s2.AuthUrl;
            }
            if (url.Length > 0) return (true, "Open the link to sign in.", url);
        }
        return (false, "Tailscale did not return a sign-in link. Check that the Tailscale service is running.", "");
    }

    public async Task<(bool Ok, string Message)> LogoutAsync()
    {
        if (!File.Exists(CliPath)) return (false, "Tailscale is not installed.");
        var (code, outp) = await RunAsync("logout", 30000).ConfigureAwait(false);
        return (code == 0, code == 0 ? "Signed out." : Shorten(outp));
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;

    // ------------------------------------------------------------------ your domain

    private string SafeBridgeToken()
    {
        try { return _bridgeToken() ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// Point <paramref name="fqdn"/> (e.g. voip.powerchain.uk) at this PC's Tailscale address with a DNS-only A
    /// record in Cloudflare. The token can be the AFIF Bridge one (it already has Zone · DNS · Edit).
    /// </summary>
    public async Task<(bool Ok, List<string> Log)> SetDomainAsync(string fqdn, string? apiToken)
    {
        var log = new List<string>();
        fqdn = (fqdn ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (fqdn.StartsWith("http")) fqdn = new Uri(fqdn).Host;
        if (!Regex.IsMatch(fqdn, @"^([a-z0-9-]+\.)+[a-z]{2,}$")) { log.Add("✗ Enter a full name such as voip.powerchain.uk"); return (false, log); }

        string token = (apiToken ?? "").Trim();
        if (token.Length == 0)
        {
            token = BridgeManager.Unprotect(Config.ProtectedApiToken);
            if (token.Length == 0) token = SafeBridgeToken();
        }
        lock (_lock)
        {
            _cfg.Domain = fqdn;
            if ((apiToken ?? "").Trim().Length > 0) _cfg.ProtectedApiToken = BridgeManager.Protect(apiToken!.Trim());
        }
        Save();

        var st = await StatusAsync(false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(st.Ipv4))
        {
            log.Add("✗ This PC has no Tailscale address yet — connect it to your tailnet first.");
            return Finish(false, log);
        }
        if (token.Length == 0)
        {
            log.Add($"• No Cloudflare token: create this record yourself in Cloudflare → DNS: type A, name {fqdn}, IPv4 {st.Ipv4}, proxy status DNS only.");
            return Finish(false, log);
        }
        var api = new CloudflareApi(token, _handler());
        try
        {
            // the zone is the longest parent of the name that exists in the account (voip.powerchain.uk → powerchain.uk)
            CloudflareApi.Zone? zone = null;
            CloudflareException? last = null;
            var labels = fqdn.Split('.');
            for (int i = 0; i <= labels.Length - 2 && zone is null; i++)
            {
                try { zone = await api.FindZoneAsync(string.Join('.', labels[i..])).ConfigureAwait(false); }
                catch (CloudflareException ex) { last = ex; if (ex.Message.Contains("permission")) break; }
            }
            if (zone is null) throw last ?? new CloudflareException($"No domain for {fqdn} was found in this Cloudflare account.");
            log.Add($"✓ Domain {zone.Name} found");
            var r = await api.UpsertDnsAsync(zone.Id, "A", fqdn, st.Ipv4, false, "AFIF-PBX SIP via Tailscale").ConfigureAwait(false);
            log.Add($"✓ DNS {fqdn} → {st.Ipv4} {r} (DNS only — reachable when the phone's Tailscale app is on)");
            lock (_lock) { _cfg.LastDnsAddress = st.Ipv4; _cfg.LastDnsUpdateUtc = DateTime.UtcNow; }
            return Finish(true, log);
        }
        catch (Exception ex)
        {
            log.Add("✗ " + ex.Message);
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

    /// <summary>Keep the DNS record right if the Tailscale address ever changes (e.g. after re-joining).</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(5), _cts.Token).ConfigureAwait(false); } catch { break; }
                try
                {
                    var c = Config;
                    if (string.IsNullOrEmpty(c.Domain) || string.IsNullOrEmpty(c.LastDnsAddress)) continue;
                    var st = await StatusAsync(false).ConfigureAwait(false);
                    if (st.Connected && st.Ipv4.Length > 0 && st.Ipv4 != c.LastDnsAddress)
                    {
                        _log.LogInformation("Tailscale address changed to {Ip}; updating {Domain}", st.Ipv4, c.Domain);
                        await SetDomainAsync(c.Domain, null).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { _log.LogWarning("Tailscale check failed: {Message}", ex.Message); }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock) { try { if (_login is { HasExited: false }) _login.Kill(true); } catch { } }
    }
}
