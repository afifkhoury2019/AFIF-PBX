using System.Security.Cryptography;
using System.Text;

namespace AfifPbx.Remote;

/// <summary>Stores small secrets (the DNS API token) encrypted for this PC (Windows DPAPI).</summary>
public static class Secrets
{
    // same entropy as earlier AFIF-PBX versions so tokens saved by them still open
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AFIF-PBX bridge");

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine));
        return "plain:" + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string stored)
    {
        try
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (stored.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.LocalMachine));
            if (stored.StartsWith("plain:")) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
        }
        catch { }
        return "";
    }
}
