using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AfifPbx.Gsm;

/// <summary>One interface of a dongle bound to WinUSB by the AFIF-PBX dongle driver.</summary>
public sealed record WinUsbInterface(string Path, string Vid, string Pid, int Mi, int Class, int SubClass, int Protocol,
    bool HasBulkIn, bool HasBulkOut, bool HasInterrupt)
{
    public string Name => $"USB {Vid}:{Pid} interface {Mi} (class {Class:X2}/{SubClass:X2}/{Protocol:X2})";
    public string Key => $"USB:MI_{Mi:D2}";
}

/// <summary>Talks to dongle interfaces through Microsoft's WinUSB driver (no COM-port driver needed).</summary>
public static class WinUsb
{
    /// <summary>Must match DeviceInterfaceGUIDs in native/driver/afif-dongle.inf.</summary>
    public static readonly Guid InterfaceGuid = new("AF1F0E30-3D2B-4B8F-9C61-0A1F1B2C3D4E");

    // ------------------------------------------------------------------ native
    private const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    internal const uint PIPE_TRANSFER_TIMEOUT = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints,
            bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINUSB_PIPE_INFORMATION { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("winusb.dll", SetLastError = true)]
    internal static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);
    [DllImport("winusb.dll", SetLastError = true)]
    internal static extern bool WinUsb_Free(IntPtr handle);
    [DllImport("winusb.dll", SetLastError = true)]
    internal static extern bool WinUsb_QueryInterfaceSettings(IntPtr handle, byte alt, out USB_INTERFACE_DESCRIPTOR desc);
    [DllImport("winusb.dll", SetLastError = true)]
    internal static extern bool WinUsb_QueryPipe(IntPtr handle, byte alt, byte index, out WINUSB_PIPE_INFORMATION pipe);
    [DllImport("winusb.dll", SetLastError = true)]
    internal static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, byte[] buffer, int length, out int transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    internal static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte[] buffer, int length, out int transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    internal static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, int length, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)]
    internal static extern bool WinUsb_AbortPipe(IntPtr handle, byte pipe);
    [DllImport("winusb.dll", SetLastError = true)]
    internal static extern bool WinUsb_FlushPipe(IntPtr handle, byte pipe);

    // ------------------------------------------------------------------ discovery

    /// <summary>Device paths of every interface bound to the AFIF-PBX dongle driver.</summary>
    public static List<string> DevicePaths()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        var guid = InterfaceGuid;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return list;
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero))
                        list.Add(Marshal.PtrToStringUni(buf + 4) ?? "");
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list.Where(p => p.Length > 0).ToList();
    }

    /// <summary>Describe every dongle interface (class, endpoints).</summary>
    public static List<WinUsbInterface> Interfaces()
    {
        var result = new List<WinUsbInterface>();
        foreach (var path in DevicePaths())
        {
            var m = Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})(?:&mi_([0-9a-f]{2}))?", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            try
            {
                using var dev = WinUsbDevice.Open(path);
                result.Add(new WinUsbInterface(path, m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant(),
                    m.Groups[3].Success ? Convert.ToInt32(m.Groups[3].Value, 16) : 0,
                    dev.Descriptor.bInterfaceClass, dev.Descriptor.bInterfaceSubClass, dev.Descriptor.bInterfaceProtocol,
                    dev.BulkIn != 0, dev.BulkOut != 0, dev.HasInterrupt));
            }
            catch { /* in use or vanished */ }
        }
        return result.OrderBy(i => i.Pid).ThenBy(i => i.Mi).ToList();
    }

    /// <summary>Does this interface answer "AT" with "OK"?</summary>
    public static bool AnswersAt(WinUsbInterface itf)
    {
        if (!itf.HasBulkIn || !itf.HasBulkOut) return false;
        try
        {
            using var dev = WinUsbDevice.Open(itf.Path);
            dev.SetReadTimeout(400);
            var buf = new byte[512];
            while (dev.Read(buf) > 0) { } // drain anything pending
            var sb = new StringBuilder();
            for (int attempt = 0; attempt < 2 && !sb.ToString().Contains("OK"); attempt++)
            {
                dev.Write(Encoding.ASCII.GetBytes("AT\r"));
                var until = DateTime.UtcNow.AddMilliseconds(900);
                while (DateTime.UtcNow < until)
                {
                    int n = dev.Read(buf);
                    if (n > 0) sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (sb.ToString().Contains("OK")) break;
                }
            }
            return sb.ToString().Contains("OK");
        }
        catch { return false; }
    }

    /// <summary>
    /// Pick the command (PC UI) and voice interfaces. The PC UI port is the highest-numbered interface
    /// that answers AT; the voice port is a bulk-only vendor interface below it that does not answer AT
    /// (network interfaces also have an interrupt endpoint, which rules them out).
    /// </summary>
    public static (WinUsbInterface? at, WinUsbInterface? audio) Choose(IReadOnlyList<WinUsbInterface> itfs, Func<WinUsbInterface, bool> answersAt,
        string preferAt = "", string preferAudio = "")
    {
        var at = itfs.FirstOrDefault(i => i.Key == preferAt);
        var audio = itfs.FirstOrDefault(i => i.Key == preferAudio);
        var responders = new HashSet<WinUsbInterface>();
        if (at is null || audio is null)
        {
            foreach (var i in itfs.Where(i => i.HasBulkIn && i.HasBulkOut))
                if (answersAt(i)) responders.Add(i);
        }
        at ??= responders.OrderByDescending(i => i.Mi).FirstOrDefault();
        if (audio is null && at is not null)
        {
            audio = itfs.Where(i => i.Pid == at.Pid && i != at && !responders.Contains(i) && i.HasBulkIn && i.HasBulkOut
                                    && !i.HasInterrupt && i.Class == 0xFF)
                        .OrderBy(i => i.Mi < at.Mi ? 0 : 1).ThenByDescending(i => i.Mi < at.Mi ? i.Mi : -i.Mi)
                        .FirstOrDefault();
        }
        return (at, audio);
    }
}

/// <summary>An opened WinUSB interface with its bulk pipes.</summary>
public sealed class WinUsbDevice : IDisposable
{
    private readonly SafeFileHandle _file;
    private IntPtr _handle;
    public WinUsb.USB_INTERFACE_DESCRIPTOR Descriptor { get; }
    public byte BulkIn { get; }
    public byte BulkOut { get; }
    public bool HasInterrupt { get; }
    public string Path { get; }

    private WinUsbDevice(string path)
    {
        Path = path;
        _file = WinUsb.CreateFile(path, 0x80000000 | 0x40000000, 1 | 2, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (_file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open dongle interface");
        if (!WinUsb.WinUsb_Initialize(_file, out _handle))
        {
            var err = Marshal.GetLastWin32Error();
            _file.Dispose();
            throw new Win32Exception(err, "WinUSB could not open the dongle interface");
        }
        WinUsb.WinUsb_QueryInterfaceSettings(_handle, 0, out var desc);
        Descriptor = desc;
        for (byte i = 0; i < desc.bNumEndpoints; i++)
        {
            if (!WinUsb.WinUsb_QueryPipe(_handle, 0, i, out var pipe)) continue;
            bool isIn = (pipe.PipeId & 0x80) != 0;
            if (pipe.PipeType == 2) { if (isIn) BulkIn = pipe.PipeId; else BulkOut = pipe.PipeId; }
            else if (pipe.PipeType == 3) HasInterrupt = true;
        }
    }

    public static WinUsbDevice Open(string path) => new(path);

    public void SetReadTimeout(int ms)
    {
        if (BulkIn == 0) return;
        uint v = (uint)ms;
        WinUsb.WinUsb_SetPipePolicy(_handle, BulkIn, WinUsb.PIPE_TRANSFER_TIMEOUT, 4, ref v);
    }

    /// <summary>Returns bytes read; 0 on timeout or an empty packet. Throws when the device is gone.</summary>
    public int Read(byte[] buffer)
    {
        if (WinUsb.WinUsb_ReadPipe(_handle, BulkIn, buffer, buffer.Length, out int n, IntPtr.Zero)) return n;
        int err = Marshal.GetLastWin32Error();
        if (err == 121 /* ERROR_SEM_TIMEOUT */ || err == 995 /* aborted */) return 0;
        throw new IOException("Dongle disconnected", new Win32Exception(err));
    }

    public void Write(byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            var chunk = offset == 0 ? data : data[offset..];
            if (!WinUsb.WinUsb_WritePipe(_handle, BulkOut, chunk, chunk.Length, out int n, IntPtr.Zero))
                throw new IOException("Dongle disconnected", new Win32Exception(Marshal.GetLastWin32Error()));
            if (n <= 0) break;
            offset += n;
        }
    }

    public void Abort()
    {
        if (_handle == IntPtr.Zero) return;
        if (BulkIn != 0) WinUsb.WinUsb_AbortPipe(_handle, BulkIn);
        if (BulkOut != 0) WinUsb.WinUsb_AbortPipe(_handle, BulkOut);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            Abort();
            WinUsb.WinUsb_Free(_handle);
            _handle = IntPtr.Zero;
        }
        _file.Dispose();
    }
}

/// <summary>Stream view of a WinUSB interface, used by the AT channel and the voice reader.</summary>
public sealed class WinUsbStream : Stream
{
    private readonly WinUsbDevice _dev;
    private readonly byte[] _rx = new byte[4096];
    private int _rxPos, _rxLen;
    private volatile bool _closed;

    public WinUsbStream(WinUsbDevice dev) { _dev = dev; _dev.SetReadTimeout(0); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_rxPos >= _rxLen)
        {
            if (_closed) return 0;
            _rxLen = _dev.Read(_rx);
            _rxPos = 0;
            if (_rxLen == 0 && _closed) return 0;
        }
        int n = Math.Min(count, _rxLen - _rxPos);
        Array.Copy(_rx, _rxPos, buffer, offset, n);
        _rxPos += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        _dev.Write(offset == 0 && count == buffer.Length ? buffer : buffer.AsSpan(offset, count).ToArray());

    public override void Flush() { }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set { } }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        _dev.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The dongle's command and voice interfaces over WinUSB.</summary>
public sealed class WinUsbModemPorts : IModemPorts
{
    private readonly WinUsbStream _at;
    private readonly WinUsbStream? _audio;

    public WinUsbModemPorts(WinUsbInterface at, WinUsbInterface? audio)
    {
        _at = new WinUsbStream(WinUsbDevice.Open(at.Path));
        try { _audio = audio is null ? null : new WinUsbStream(WinUsbDevice.Open(audio.Path)); }
        catch { _at.Dispose(); throw; }
        AtName = at.Key; AudioName = audio?.Key ?? "";
    }

    public Stream At => _at;
    public Stream? Audio => _audio;
    public string AtName { get; }
    public string AudioName { get; }

    public void Dispose()
    {
        _at.Dispose();
        _audio?.Dispose();
    }
}
