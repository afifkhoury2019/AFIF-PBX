namespace AfifPbx;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Global\AFIF-PBX-Launcher", out bool first);
        if (!first)
        {
            // Already running in the tray: just open the web interface.
            Shell.OpenUrl("http://localhost/");
            return;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        bool quiet = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApp(showSplash: !quiet, openPanel: !quiet));
    }
}

/// <summary>Tray icon that owns the PBX controller, the dongle watcher and the windows.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly PbxController _pbx = new();
    private readonly DongleWatcher _dongle = new();
    private readonly NotifyIcon _tray;
    private StatusForm? _panel;
    private readonly Control _ui = new();

    public TrayApp(bool showSplash, bool openPanel)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open AFIF-PBX", null, (_, _) => Shell.OpenUrl("http://localhost/"));
        menu.Items.Add("Control panel", null, (_, _) => ShowPanel());
        menu.Items.Add("Asterisk CLI", null, (_, _) => _pbx.OpenCli());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart PBX", null, async (_, _) => await _pbx.RestartAsync());
        menu.Items.Add("Stop PBX", null, async (_, _) => { await _pbx.StopAsync(); Notify("AFIF-PBX stopped"); });
        menu.Items.Add("Start PBX", null, async (_, _) => await BootAsync(null));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit launcher (PBX keeps running)", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { Icon = Theme.AppIcon, Text = "AFIF-PBX", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowPanel();
        _ui.CreateControl();
        _dongle.Log += line =>
        {
            if (line.StartsWith("Attaching") && _ui.IsHandleCreated)
                _ui.BeginInvoke(() => Notify("GSM dongle connected"));
        };

        // Start once the message loop is running
        _ui.BeginInvoke(() => { _ = StartAsync(showSplash, openPanel); });
    }

    private async Task StartAsync(bool showSplash, bool openPanel)
    {
        SplashForm? splash = null;
        if (showSplash)
        {
            splash = new SplashForm();
            splash.Show();
        }
        var progress = new Progress<string>(s => { splash?.SetStatus(s); _tray.Text = Trim("AFIF-PBX — " + s); });
        bool ok = await BootAsync(progress);
        if (splash is not null)
        {
            splash.SetStatus(ok ? "AFIF-PBX is ready" : "AFIF-PBX could not start — see control panel");
            splash.ReadyToClose = true;
            var done = new TaskCompletionSource();
            splash.FormClosed += (_, _) => done.TrySetResult();
            await done.Task;
        }
        if (openPanel || !ok) ShowPanel();
        Notify(ok ? "AFIF-PBX is running" : "AFIF-PBX did not start");
    }

    private async Task<bool> BootAsync(IProgress<string>? progress)
    {
        try { return await _pbx.StartAsync(progress); }
        catch (Exception ex) { progress?.Report(ex.Message); return false; }
    }

    private void ShowPanel()
    {
        if (_panel is null || _panel.IsDisposed) _panel = new StatusForm(_pbx, _dongle);
        _panel.Show();
        if (_panel.WindowState == FormWindowState.Minimized) _panel.WindowState = FormWindowState.Normal;
        _panel.Activate();
        _ = _panel.RefreshStatusAsync();
    }

    private void Notify(string text)
    {
        _tray.BalloonTipTitle = "AFIF-PBX";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(3000);
    }

    private static string Trim(string s) => s.Length > 63 ? s[..63] : s;

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _dongle.Dispose();
        _pbx.Dispose();
        _panel?.Dispose();
        _ui.Dispose();
        base.ExitThreadCore();
    }
}
