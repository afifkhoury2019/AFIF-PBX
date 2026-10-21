using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http.Json;
using System.Text.Json;

namespace AfifPbx;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\AFIF-PBX-Tray", out bool first);
        if (!first) { Local.OpenPanel(); return; }
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        bool quiet = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApp(showSplash: !quiet));
    }
}

/// <summary>Talks to the AFIF-PBX Windows service on this PC.</summary>
internal static class Local
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AFIF-PBX");

    public static int Port
    {
        get
        {
            try { return int.Parse(File.ReadAllText(Path.Combine(DataDir, "http-port.txt")).Trim()); }
            catch { return 80; }
        }
    }

    public static string BaseUrl => Port == 80 ? "http://localhost" : $"http://localhost:{Port}";

    public static async Task<LocalStatus?> StatusAsync()
    {
        try { return await Http.GetFromJsonAsync<LocalStatus>($"http://127.0.0.1:{Port}/api/local/status", Json); }
        catch { return null; }
    }

    public static async Task ReconnectDongleAsync()
    {
        try { await Http.PostAsync($"http://127.0.0.1:{Port}/api/local/gsm/reconnect", null); } catch { }
    }

    public static void OpenPanel()
    {
        try { Process.Start(new ProcessStartInfo(BaseUrl + "/") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Restart the Windows service (asks for administrator approval).</summary>
    public static void RestartService()
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -WindowStyle Hidden -Command \"Restart-Service -Name 'AFIF-PBX' -Force\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { /* user declined UAC */ }
    }
}

internal sealed class LocalStatus
{
    public bool Running { get; set; }
    public string Version { get; set; } = "";
    public int HttpPort { get; set; }
    public int SipPort { get; set; }
    public List<string> Addresses { get; set; } = new();
    public int Registered { get; set; }
    public int Extensions { get; set; }
    public int ActiveCalls { get; set; }
    public int NewVoicemails { get; set; }
    public ModemInfo? Modem { get; set; }
}

internal sealed class ModemInfo
{
    public string State { get; set; } = "";
    public string Message { get; set; } = "";
    public string Operator { get; set; } = "";
    public string Model { get; set; } = "";
    public int SignalPercent { get; set; }
    public string CallState { get; set; } = "";
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 5000 };
    private StatusWindow? _window;
    private LocalStatus? _last;
    private string _lastModemState = "";
    private int _lastVoicemails = -1;
    private readonly Control _ui = new();

    public TrayApp(bool showSplash)
    {
        _ui.CreateControl();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open AFIF-PBX", null, (_, _) => Local.OpenPanel());
        menu.Items.Add("Status", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reconnect GSM dongle", null, async (_, _) => { await Local.ReconnectDongleAsync(); Notify("Reconnecting the dongle…"); });
        menu.Items.Add("Restart AFIF-PBX", null, (_, _) => Local.RestartService());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray icon (AFIF-PBX keeps running)", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { Icon = Theme.AppIcon, Text = "AFIF-PBX", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => Local.OpenPanel();
        _poll.Tick += async (_, _) => await PollAsync();
        _ui.BeginInvoke(() => { _ = StartAsync(showSplash); });
    }

    private async Task StartAsync(bool showSplash)
    {
        SplashForm? splash = null;
        if (showSplash) { splash = new SplashForm(); splash.Show(); }
        var phases = new[] { "Connecting to the AFIF-PBX service", "Starting the phone system", "Checking the GSM line" };
        LocalStatus? s = null;
        for (int i = 0; i < 40 && s is null; i++)
        {
            splash?.SetStatus(phases[Math.Min(i / 4, phases.Length - 1)]);
            s = await Local.StatusAsync();
            if (s is null) await Task.Delay(750);
        }
        if (s?.Modem is not null) splash?.SetStatus($"GSM: {Describe(s.Modem)}");
        await Task.Delay(400);
        if (splash is not null)
        {
            splash.SetStatus(s is null ? "The AFIF-PBX service is not running" : "AFIF-PBX is ready");
            splash.ReadyToClose = true;
            var done = new TaskCompletionSource();
            splash.FormClosed += (_, _) => done.TrySetResult();
            await done.Task;
        }
        Update(s);
        if (showSplash) ShowWindow();
        _poll.Start();
    }

    private async Task PollAsync() => Update(await Local.StatusAsync());

    private void Update(LocalStatus? s)
    {
        _last = s;
        string tip = s is null ? "AFIF-PBX — service stopped"
            : $"AFIF-PBX — {s.Registered}/{s.Extensions} phones · GSM {(s.Modem is null ? "off" : Describe(s.Modem))}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;

        if (s?.Modem is not null && s.Modem.State != _lastModemState)
        {
            if (_lastModemState != "") Notify("GSM line: " + Describe(s.Modem));
            _lastModemState = s.Modem.State;
        }
        if (s is not null)
        {
            if (_lastVoicemails >= 0 && s.NewVoicemails > _lastVoicemails) Notify("New voicemail");
            _lastVoicemails = s.NewVoicemails;
        }
        _window?.Display(s);
    }

    public static string Describe(ModemInfo m) => m.State switch
    {
        "ready" => $"{(string.IsNullOrEmpty(m.Operator) ? "ready" : m.Operator)} ({m.SignalPercent}%)",
        "no-device" => "dongle not plugged in",
        "no-driver" => "Huawei driver missing",
        "port-busy" => "port used by another program",
        "no-sim" => "SIM problem",
        "no-voice" => "voice disabled (SMS only)",
        "starting" => "starting",
        _ => m.State,
    };

    private void ShowWindow()
    {
        if (_window is null || _window.IsDisposed) _window = new StatusWindow();
        _window.Display(_last);
        _window.Show();
        _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void Notify(string text)
    {
        _tray.BalloonTipTitle = "AFIF-PBX";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(3000);
    }

    protected override void ExitThreadCore()
    {
        _poll.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _window?.Dispose();
        _ui.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Small branded status window.</summary>
internal sealed class StatusWindow : Form
{
    private readonly Label _service = new(), _web = new(), _phones = new(), _gsm = new(), _calls = new(), _vm = new(), _addr = new();

    public StatusWindow()
    {
        Text = "AFIF-PBX";
        Icon = Theme.AppIcon;
        BackColor = Theme.Navy;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(10);
        ClientSize = new Size(640, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var header = new Panel { Dock = DockStyle.Top, Height = 120 };
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var lg = new LinearGradientBrush(header.ClientRectangle, Theme.Navy, Theme.Navy2, 0f)) g.FillRectangle(lg, header.ClientRectangle);
            var logo = Theme.Logo;
            float h = 84, w = logo.Width * h / Math.Max(1, logo.Height);
            g.DrawImage(logo, 24, 18, w, h);
            using var pen = new Pen(Color.FromArgb(90, Theme.Cyan), 1);
            g.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 250, Padding = new Padding(24, 16, 24, 0), ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string name, Label v)
        {
            grid.Controls.Add(new Label { Text = name, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 6, 0, 6) });
            v.AutoSize = true; v.Margin = new Padding(0, 6, 0, 6); v.Text = "…"; v.MaximumSize = new Size(400, 0);
            grid.Controls.Add(v);
        }
        Row("Phone system", _service);
        Row("Web panel", _web);
        Row("Phones online", _phones);
        Row("GSM line", _gsm);
        Row("Calls now", _calls);
        Row("New voicemail", _vm);
        Row("Phones register to", _addr);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(20, 12, 20, 0) };
        Button Btn(string text, Action a, bool primary = false)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, Height = 36, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(4),
                FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, BackColor = primary ? Theme.Blue : Theme.Panel, Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = primary ? Theme.Blue : Color.FromArgb(70, Theme.Muted);
            b.Click += (_, _) => a();
            buttons.Controls.Add(b);
            return b;
        }
        Btn("Open AFIF-PBX", Local.OpenPanel, primary: true);
        Btn("Reconnect dongle", () => _ = Local.ReconnectDongleAsync());
        Btn("Restart", Local.RestartService);

        Controls.Add(grid);
        Controls.Add(buttons);
        Controls.Add(header);
        Load += (_, _) => Theme.RoundCorners(this);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    private static void Set(Label l, string text, Color c) { l.Text = text; l.ForeColor = c; }

    public void Display(LocalStatus? s)
    {
        if (IsDisposed) return;
        if (s is null)
        {
            Set(_service, "Stopped — click Restart", Theme.Bad);
            foreach (var l in new[] { _web, _phones, _gsm, _calls, _vm, _addr }) Set(l, "—", Theme.Muted);
            return;
        }
        Set(_service, $"Running (version {s.Version})", Theme.Good);
        Set(_web, Local.BaseUrl + "/", Theme.Text);
        Set(_phones, $"{s.Registered} of {s.Extensions}", s.Registered > 0 ? Theme.Good : Theme.Warn);
        var m = s.Modem;
        Set(_gsm, m is null ? "Switched off" : TrayApp.Describe(m) + (m.State == "ready" || string.IsNullOrEmpty(m.Message) ? "" : "\n" + m.Message),
            m?.State == "ready" ? Theme.Good : m is null ? Theme.Muted : Theme.Warn);
        Set(_calls, s.ActiveCalls.ToString(), Theme.Text);
        Set(_vm, s.NewVoicemails.ToString(), s.NewVoicemails > 0 ? Theme.Warn : Theme.Text);
        Set(_addr, string.Join("  ·  ", s.Addresses.Select(a => s.SipPort == 5060 ? a : $"{a}:{s.SipPort}")), Theme.Text);
    }
}
