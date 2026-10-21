using System.Diagnostics;
using System.Text;

namespace AfifPbx;

/// <summary>Runs console tools (wsl.exe, usbipd.exe) without showing a window.</summary>
internal static class Shell
{
    public record Result(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
    }

    public static async Task<Result> RunAsync(string file, string args, int timeoutMs = 60_000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["WSL_UTF8"] = "1"; // make wsl.exe print UTF-8 instead of UTF-16
        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { return new Result(-1, "", ex.Message); }

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new Result(-2, await SafeAsync(outTask), "timeout");
        }
        return new Result(p.ExitCode, Clean(await outTask), Clean(await errTask));
    }

    /// <summary>Start a long-running hidden process (e.g. the WSL keep-alive).</summary>
    public static Process? StartHidden(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["WSL_UTF8"] = "1";
        try { return Process.Start(psi); } catch { return null; }
    }

    /// <summary>Open a visible console window (Asterisk CLI).</summary>
    public static void OpenConsole(string title, string file, string args)
    {
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        try
        {
            if (File.Exists(wt))
                Process.Start(new ProcessStartInfo(wt, $"--title \"{title}\" {file} {args}") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k title {title} && {file} {args}") { UseShellExecute = true });
        }
        catch { }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static async Task<string> SafeAsync(Task<string> t)
    {
        try { return await t; } catch { return ""; }
    }

    private static string Clean(string s) => s.Replace("\0", "").Trim();
}
