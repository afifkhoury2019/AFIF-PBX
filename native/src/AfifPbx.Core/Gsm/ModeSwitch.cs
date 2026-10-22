using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AfifPbx.Gsm;

/// <summary>
/// Huawei sticks start as a virtual CD-ROM. This sends them the same "switch to modem" command
/// usb_modeswitch uses (SCSI 0x11 0x06), and ejects the virtual CD as a fallback, so the modem
/// interfaces appear without installing Huawei software.
/// </summary>
public static class ModeSwitch
{
    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x4D014;
    private const uint IOCTL_STORAGE_EJECT_MEDIA = 0x2D4808;

    [StructLayout(LayoutKind.Sequential)]
    private struct SCSI_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public byte ScsiStatus, PathId, TargetId, Lun, CdbLength, SenseInfoLength, DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Cdb;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref SCSI_PASS_THROUGH_DIRECT inBuf, int inSize,
        ref SCSI_PASS_THROUGH_DIRECT outBuf, int outSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>Drive letters (e.g. "E:") of Huawei virtual CD drives.</summary>
    public static List<string> HuaweiCdDrives()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Drive, PNPDeviceID, Caption FROM Win32_CDROMDrive");
            foreach (var o in s.Get())
            {
                string id = o["PNPDeviceID"]?.ToString() ?? "", cap = o["Caption"]?.ToString() ?? "";
                if ((id.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase) || cap.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase))
                    && o["Drive"] is string d && d.Length > 0) list.Add(d);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Try to switch every Huawei virtual CD to modem mode. Returns how many were sent the command.</summary>
    public static int SwitchAll()
    {
        int n = 0;
        foreach (var drive in HuaweiCdDrives())
        {
            if (TrySwitch(drive)) n++;
        }
        return n;
    }

    private static bool TrySwitch(string drive)
    {
        using var h = WinUsb.CreateFile(@"\\.\" + drive.TrimEnd('\\'), 0x80000000 | 0x40000000, 1 | 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return false;
        bool sent = false;
        try
        {
            var cdb = new byte[16];
            cdb[0] = 0x11; cdb[1] = 0x06; cdb[2] = 0x20; cdb[5] = 0x01; // Huawei "new mode" switch
            var sptd = new SCSI_PASS_THROUGH_DIRECT
            {
                Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>(),
                CdbLength = 16,
                DataIn = 2, // unspecified
                TimeOutValue = 5,
                Cdb = cdb,
            };
            sent = DeviceIoControl(h, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref sptd, sptd.Length, ref sptd, sptd.Length, out _, IntPtr.Zero);
        }
        catch { }
        try
        {
            // Many Huawei sticks also switch when their virtual CD is ejected
            if (DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) sent = true;
        }
        catch { }
        return sent;
    }
}
