using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AfifPbx;

/// <summary>Status reported by /usr/local/bin/afif-status inside the AFIF-PBX distro.</summary>
public sealed class PbxStatus
{
    public bool DistroInstalled { get; init; }
    public bool Running { get; init; }
    public bool AsteriskUp { get; init; }
    public bool WebUp { get; init; }
    public long UptimeSeconds { get; init; }
    public int UsbDevices { get; init; }
    public int SerialPorts { get; init; }
    public IReadOnlyList<(string Name, string State)> Dongles { get; init; } = Array.Empty<(string, string)>();
    public string Message { get; init; } = "";

    public string DongleSummary =>
        Dongles.Count > 0 ? string.Join(", ", Dongles.Select(d => $"{d.Name}: {d.State}"))
        : SerialPorts > 0 ? "Modem detected, waiting for Asterisk"
        : UsbDevices > 0 ? "E303 attached, switching to modem mode"
        : "No dongle attached";
}

/// <summary>Starts, stops and queries the AFIF-PBX WSL distribution.</summary>
public sealed class PbxController : IDisposable
{
    public const string Distro = "AFIF-PBX";
    private Process? _keepAlive;

    public static string Wsl => Path.Combine(Environment.SystemDirectory, "wsl.exe");

    public async Task<bool> IsInstalledAsync()
    {
        var r = await Shell.RunAsync(Wsl, "--list --quiet", 20_000);
        return r.Output.Split('\n').Any(l => l.Trim().Equals(Distro, StringComparison.OrdinalIgnoreCase));
    }

    public Task<Shell.Result> ExecAsync(string command, int timeoutMs = 60_000) =>
        Shell.RunAsync(Wsl, $"-d {Distro} -u root -- bash -lc \"{command.Replace("\"", "\\\"")}\"", timeoutMs);

    /// <summary>Boot the distro and keep it alive; returns when Asterisk answers (or timeout).</summary>
    public async Task<bool> StartAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Checking AFIF-PBX system");
        if (!await IsInstalledAsync())
        {
            progress?.Report("AFIF-PBX system is not installed — run setup again");
            return false;
        }

        if (_keepAlive is null || _keepAlive.HasExited)
        {
            progress?.Report("Starting AFIF-PBX engine");
            _keepAlive = Shell.StartHidden(Wsl, $"-d {Distro} -u root -- /usr/local/bin/afif-keepalive");
        }

        var started = DateTime.UtcNow;
        string[] phases = { "Starting database", "Starting Asterisk", "Loading dialplan", "Preparing GSM gateway" };
        int i = 0;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(4) && !ct.IsCancellationRequested)
        {
            var s = await GetStatusAsync();
            if (s.AsteriskUp)
            {
                progress?.Report(s.WebUp ? "AFIF-PBX is running" : "Starting web interface");
                if (s.WebUp) return true;
            }
            else
            {
                progress?.Report(phases[Math.Min(i++ / 3, phases.Length - 1)]);
                if (i == 12) await ExecAsync("systemctl start freepbx || fwconsole start", 120_000);
            }
            await Task.Delay(2500, ct).ContinueWith(_ => { });
        }
        return (await GetStatusAsync()).AsteriskUp;
    }

    public async Task<PbxStatus> GetStatusAsync()
    {
        var r = await ExecAsync("/usr/local/bin/afif-status", 20_000);
        if (!r.Ok)
            return new PbxStatus { DistroInstalled = true, Message = r.Error };
        var kv = r.Output.Split('\n').Last().Split(';')
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        string Get(string k) => kv.TryGetValue(k, out var v) ? v : "";
        var dongles = Get("dongles").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split('|')).Where(d => d.Length >= 2)
            .Select(d => (d[0], d[1])).ToList();
        _ = long.TryParse(Get("uptime"), out var up);
        _ = int.TryParse(Get("usb"), out var usb);
        _ = int.TryParse(Get("tty"), out var tty);
        return new PbxStatus
        {
            DistroInstalled = true,
            Running = true,
            AsteriskUp = Get("asterisk") == "up",
            WebUp = Get("web") == "up",
            UptimeSeconds = up,
            UsbDevices = usb,
            SerialPorts = tty,
            Dongles = dongles,
        };
    }

    public Task RestartAsync() => ExecAsync("fwconsole restart", 180_000);
    public Task ReloadAsync() => ExecAsync("fwconsole reload", 120_000);
    public Task ScanDongleAsync() => ExecAsync("/usr/local/bin/afif-dongle scan", 60_000);

    public async Task StopAsync()
    {
        await ExecAsync("fwconsole stop", 120_000);
        try { _keepAlive?.Kill(true); } catch { }
        _keepAlive = null;
        await Shell.RunAsync(Wsl, $"--terminate {Distro}", 30_000);
    }

    public void OpenCli() => Shell.OpenConsole("AFIF-PBX CLI", "wsl.exe", $"-d {Distro} -u root -- asterisk -rvvv");
    public void OpenShell() => Shell.OpenConsole("AFIF-PBX shell", "wsl.exe", $"-d {Distro} -u root");

    /// <summary>LAN addresses phones should register to (mirrored networking shares the PC's IP).</summary>
    public static IReadOnlyList<string> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254"))
            .Select(a => a.Address.ToString()).Distinct().ToList();

    public void Dispose()
    {
        // Leave the PBX running when the launcher closes; only drop our handle.
        _keepAlive?.Dispose();
    }
}
