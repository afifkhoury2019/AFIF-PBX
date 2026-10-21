using System.Management;
using System.Text.RegularExpressions;

namespace AfifPbx.Gsm;

public sealed record DiscoveredPort(string Port, string Name);

/// <summary>Finds the Huawei dongle's COM ports through Windows device manager (WMI).</summary>
public static class PortDiscovery
{
    public static IReadOnlyList<DiscoveredPort> ListComPorts()
    {
        var list = new List<DiscoveredPort>();
        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in System.IO.Ports.SerialPort.GetPortNames()) list.Add(new DiscoveredPort(p, p));
            return list;
        }
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var o in searcher.Get())
            {
                var name = o["Name"]?.ToString() ?? "";
                var m = Regex.Match(name, @"\((COM\d+)\)");
                if (m.Success) list.Add(new DiscoveredPort(m.Groups[1].Value, name));
            }
        }
        catch { }
        foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
            if (list.All(x => x.Port != p)) list.Add(new DiscoveredPort(p, p));
        return list.OrderBy(p => p.Port.Length).ThenBy(p => p.Port).ToList();
    }

    /// <summary>Huawei devices Windows sees, including ones without a driver (shown as CD-ROM/mass storage).</summary>
    public static IReadOnlyList<string> HuaweiDevices()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_12D1%' OR Name LIKE '%HUAWEI%'");
            foreach (var o in searcher.Get()) list.Add($"{o["Name"]} [{o["DeviceID"]}]");
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Opens the dongle: explicit ports from settings win, otherwise the "PC UI Interface" and
    /// "Application Interface" ports are picked automatically.
    /// </summary>
    public static Func<(IModemPorts?, string, string)> Opener(Func<(string at, string audio)> configured) => () =>
    {
        var (cfgAt, cfgAudio) = configured();
        string at = cfgAt, audio = cfgAudio;
        if (string.IsNullOrWhiteSpace(at) || string.IsNullOrWhiteSpace(audio))
        {
            var ports = ListComPorts();
            var pcui = ports.FirstOrDefault(p => p.Name.Contains("PC UI", StringComparison.OrdinalIgnoreCase));
            var app = ports.FirstOrDefault(p => p.Name.Contains("Application Interface", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(at)) at = pcui?.Port ?? "";
            if (string.IsNullOrWhiteSpace(audio)) audio = app?.Port ?? "";
        }
        if (string.IsNullOrWhiteSpace(at))
        {
            var huawei = HuaweiDevices();
            if (huawei.Count == 0)
                return (null, "no-device", "No GSM dongle found. Plug in the Huawei dongle.");
            return (null, "no-driver",
                "The Huawei dongle is plugged in but its COM ports are missing. Install the HUAWEI Mobile Connect driver " +
                "(run the setup on the dongle's virtual CD drive), then unplug and re-plug the dongle.");
        }
        try
        {
            return (new SerialModemPorts(at, audio), "starting", "");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "port-busy",
                $"{at} is in use by another program. Close HUAWEI Mobile Partner (and remove it from start-up), or any other dialler.");
        }
        catch (Exception ex)
        {
            return (null, "error", $"Could not open {at}/{audio}: {ex.Message}");
        }
    };
}
