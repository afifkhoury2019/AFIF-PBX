using System.Drawing.Drawing2D;

namespace AfifPbx;

/// <summary>AFIF-PBX control panel: engine, web UI and GSM dongle status plus common actions.</summary>
public sealed class StatusForm : Form
{
    private readonly PbxController _pbx;
    private readonly DongleWatcher _dongle;
    private readonly Label _engine = new(), _web = new(), _gsm = new(), _usb = new(), _addr = new(), _uptime = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 5000 };
    private bool _refreshing;

    public StatusForm(PbxController pbx, DongleWatcher dongle)
    {
        _pbx = pbx; _dongle = dongle;
        Text = "AFIF-PBX";
        Icon = Theme.AppIcon;
        BackColor = Theme.Navy;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(10);
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(640, 520);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        var header = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = Theme.Navy2 };
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
        header.Resize += (_, _) => header.Invalidate();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 220, Padding = new Padding(24, 16, 24, 0), ColumnCount = 2, RowCount = 6,
            BackColor = Theme.Navy,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string name, Label value)
        {
            var l = new Label { Text = name, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
            value.AutoSize = true; value.Margin = new Padding(0, 6, 0, 6); value.Text = "…";
            grid.Controls.Add(l); grid.Controls.Add(value);
        }
        Row("PBX engine", _engine);
        Row("Web interface", _web);
        Row("GSM dongle", _gsm);
        Row("USB passthrough", _usb);
        Row("Phones register to", _addr);
        Row("Uptime", _uptime);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(20, 8, 20, 0), BackColor = Theme.Navy };
        Button Btn(string text, Func<Task> action, bool primary = false)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, Height = 36, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(4),
                FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, BackColor = primary ? Theme.Blue : Theme.Panel, Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = primary ? Theme.Blue : Color.FromArgb(70, Theme.Muted);
            b.Click += async (_, _) =>
            {
                b.Enabled = false;
                try { await action(); } catch (Exception ex) { AddLog(ex.Message); }
                finally { b.Enabled = true; await RefreshStatusAsync(); }
            };
            buttons.Controls.Add(b);
            return b;
        }
        Btn("Open AFIF-PBX", () => { Shell.OpenUrl("http://localhost/"); return Task.CompletedTask; }, primary: true);
        Btn("Asterisk CLI", () => { _pbx.OpenCli(); return Task.CompletedTask; });
        Btn("Rescan dongle", async () => { AddLog("Rescanning dongle"); await _pbx.ScanDongleAsync(); });
        Btn("Reload", async () => { AddLog("Reloading configuration"); await _pbx.ReloadAsync(); });
        Btn("Restart", async () => { AddLog("Restarting AFIF-PBX"); await _pbx.RestartAsync(); });

        _log.Multiline = true; _log.ReadOnly = true; _log.Dock = DockStyle.Fill; _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Theme.Panel; _log.ForeColor = Theme.Muted; _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font(FontFamily.GenericMonospace, 9f);
        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 8, 24, 20), BackColor = Theme.Navy };
        logHost.Controls.Add(_log);

        Controls.Add(logHost);
        Controls.Add(buttons);
        Controls.Add(grid);
        Controls.Add(header);

        _dongle.Log += AddLog;
        _refresh.Tick += async (_, _) => await RefreshStatusAsync();
        Load += async (_, _) => { Theme.RoundCorners(this); _refresh.Start(); await RefreshStatusAsync(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void AddLog(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => AddLog(line)); return; }
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
    }

    private static void Set(Label l, string text, Color c) { l.Text = text; l.ForeColor = c; }

    public async Task RefreshStatusAsync()
    {
        if (_refreshing || !Visible) return;
        _refreshing = true;
        try
        {
            var s = await _pbx.GetStatusAsync();
            Set(_engine, s.AsteriskUp ? "Running" : "Stopped", s.AsteriskUp ? Theme.Good : Theme.Bad);
            Set(_web, s.WebUp ? "http://localhost/" : "Not responding", s.WebUp ? Theme.Good : Theme.Warn);
            var gsmOk = s.Dongles.Any(d => d.State.StartsWith("Free", StringComparison.OrdinalIgnoreCase) || d.State.StartsWith("Ring") || d.State.StartsWith("Dial") || d.State.StartsWith("Active"));
            Set(_gsm, s.DongleSummary, gsmOk ? Theme.Good : s.UsbDevices > 0 ? Theme.Warn : Theme.Muted);
            Set(_usb, _dongle.LastState, _dongle.LastState.StartsWith("Dongle connected") ? Theme.Good : Theme.Muted);
            var ips = PbxController.LanAddresses();
            Set(_addr, ips.Count > 0 ? string.Join("  ·  ", ips) : "No network", Theme.Text);
            var up = TimeSpan.FromSeconds(s.UptimeSeconds);
            Set(_uptime, s.AsteriskUp ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m" : "—", Theme.Text);
        }
        finally { _refreshing = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _refresh.Dispose(); _dongle.Log -= AddLog; }
        base.Dispose(disposing);
    }
}
