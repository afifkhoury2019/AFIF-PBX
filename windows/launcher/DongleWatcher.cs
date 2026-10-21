using System.Text.Json;

namespace AfifPbx;

/// <summary>
/// Hands every Huawei USB device (vendor 12D1, e.g. the E303) to the AFIF-PBX system through usbipd-win.
/// The E303 first appears as a CD-ROM, gets switched to modem mode inside Linux and then re-appears
/// with a new product id, so the watcher keeps binding and attaching whatever Huawei device it sees.
/// </summary>
public sealed class DongleWatcher : IDisposable
{
    private const string HuaweiVid = "VID_12D1";
    private readonly System.Threading.Timer _timer;
    private int _busy;
    private readonly HashSet<string> _attachFailed = new();

    public event Action<string>? Log;
    public string LastState { get; private set; } = "Waiting for dongle";

    public static string? UsbipdPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe"),
                "usbipd.exe",
            };
            return candidates.FirstOrDefault(c => !c.Contains('\\') || File.Exists(c));
        }
    }

    public DongleWatcher()
    {
        _timer = new System.Threading.Timer(async _ => await TickAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    private sealed record UsbDevice(string BusId, string InstanceId, string Description, bool Shared, bool Attached);

    private static async Task<List<UsbDevice>> ListAsync(string usbipd)
    {
        var r = await Shell.RunAsync(usbipd, "state", 15_000);
        var list = new List<UsbDevice>();
        if (!r.Ok || string.IsNullOrWhiteSpace(r.Output)) return list;
        try
        {
            using var doc = JsonDocument.Parse(r.Output);
            foreach (var d in doc.RootElement.GetProperty("Devices").EnumerateArray())
            {
                string S(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var busId = S("BusId");
                if (string.IsNullOrEmpty(busId)) continue; // persisted but not connected
                list.Add(new UsbDevice(busId, S("InstanceId"), S("Description"),
                    Shared: !string.IsNullOrEmpty(S("PersistedGuid")),
                    Attached: !string.IsNullOrEmpty(S("ClientIPAddress"))));
            }
        }
        catch (JsonException) { }
        return list;
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var usbipd = UsbipdPath;
            if (usbipd is null) { LastState = "usbipd-win is not installed"; return; }

            var huawei = (await ListAsync(usbipd))
                .Where(d => d.InstanceId.Contains(HuaweiVid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (huawei.Count == 0) { LastState = "No dongle plugged in"; _attachFailed.Clear(); return; }

            foreach (var d in huawei)
            {
                var id = $"{d.BusId} {Pid(d.InstanceId)}";
                if (!d.Shared)
                {
                    Log?.Invoke($"Sharing {d.Description} ({id}) with AFIF-PBX");
                    // --force: Windows has usually grabbed the stick as a CD-ROM / Huawei modem
                    var b = await Shell.RunAsync(usbipd, $"bind --busid {d.BusId} --force", 30_000);
                    if (!b.Ok) { LastState = $"Could not share dongle: {b.Error}"; Log?.Invoke(LastState); continue; }
                }
                if (!d.Attached)
                {
                    if (_attachFailed.Contains(id)) continue;
                    Log?.Invoke($"Attaching {id} to AFIF-PBX");
                    var a = await Shell.RunAsync(usbipd, $"attach --wsl --busid {d.BusId}", 45_000);
                    if (!a.Ok)
                    {
                        // A forced bind takes effect after the device is re-plugged; retry next time it changes.
                        LastState = a.Error.Contains("not shared", StringComparison.OrdinalIgnoreCase)
                            ? "Unplug and re-plug the dongle once to finish sharing it"
                            : $"Attach failed: {a.Error}";
                        Log?.Invoke(LastState);
                        _attachFailed.Add(id);
                        continue;
                    }
                }
                LastState = $"Dongle connected to AFIF-PBX ({Pid(d.InstanceId)})";
            }
        }
        catch (Exception ex) { LastState = ex.Message; }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static string Pid(string instanceId)
    {
        var i = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && instanceId.Length >= i + 8 ? "12D1:" + instanceId.Substring(i + 4, 4) : "12D1";
    }

    public void Dispose() => _timer.Dispose();
}
