using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AfifPbx;

/// <summary>
/// Animated AFIF-PBX splash: the logo "renders" in (zoom, de-blur, brightness flash), light rings orbit it,
/// a light sweep runs across the logo's own shape, and the start-up status is shown underneath.
/// </summary>
public sealed class SplashForm : Form
{
    private readonly System.Windows.Forms.Timer _frame = new() { Interval = 15 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Bitmap _logo;
    private readonly Bitmap _glow;
    private readonly Bitmap _sweep;
    private readonly byte[] _logoAlpha;
    private readonly int _sweepStride;
    private readonly (PointF P, PointF V)[] _dots;
    private readonly Font _statusFont = Theme.UiFont(10.5f);
    private readonly Font _tagFont = Theme.UiFont(8.5f, FontStyle.Bold);
    private string _status = "Starting";
    private float _closeStart = -1;

    public float MinimumShowSeconds { get; set; } = 3.2f;
    public bool ReadyToClose { get; set; }

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 440);
        ShowInTaskbar = true;
        Text = "AFIF-PBX";
        Icon = Theme.AppIcon;
        BackColor = Theme.Navy;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // Logo scaled to the splash, plus its glow and alpha mask
        int lw = 470;
        int lh = Math.Max(1, (int)(Theme.Logo.Height * (lw / (float)Math.Max(1, Theme.Logo.Width))));
        _logo = new Bitmap(lw, lh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(_logo))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Theme.Logo, 0, 0, lw, lh);
        }
        _glow = Theme.MakeGlow(_logo, Theme.Cyan, 14);
        var d = _logo.LockBits(new Rectangle(0, 0, lw, lh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[d.Stride * lh];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        _logo.UnlockBits(d);
        _logoAlpha = new byte[lw * lh];
        for (int y = 0; y < lh; y++) for (int x = 0; x < lw; x++) _logoAlpha[y * lw + x] = px[y * d.Stride + x * 4 + 3];
        _sweep = new Bitmap(lw, lh, PixelFormat.Format32bppArgb);
        _sweepStride = lw * 4;

        var rnd = new Random(7);
        _dots = Enumerable.Range(0, 46).Select(_ => (
            new PointF(rnd.Next(ClientSize.Width), rnd.Next(ClientSize.Height)),
            new PointF((float)(rnd.NextDouble() - .5) * .5f, (float)(rnd.NextDouble() - .5) * .5f))).ToArray();

        _frame.Tick += (_, _) =>
        {
            float t = (float)_clock.Elapsed.TotalSeconds;
            if (ReadyToClose && t >= MinimumShowSeconds && _closeStart < 0) _closeStart = t;
            if (_closeStart >= 0)
            {
                Opacity = Math.Max(0, 1 - (t - _closeStart) / .45f);
                if (Opacity <= 0) { _frame.Stop(); Close(); return; }
            }
            Invalidate();
        };
        Load += (_, _) => { Theme.RoundCorners(this); _frame.Start(); };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };
    }

    public void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        _status = text;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = CompositingQuality.HighQuality;
        float t = (float)_clock.Elapsed.TotalSeconds;
        int W = ClientSize.Width, H = ClientSize.Height;
        var center = new PointF(W / 2f, H * .42f);

        // Background
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(-W * .3f, -H * .5f, W * 1.6f, H * 1.7f);
            using var bg = new PathGradientBrush(path) { CenterColor = Theme.Navy2, SurroundColors = new[] { Theme.Navy } };
            bg.CenterPoint = center;
            g.FillRectangle(bg, ClientRectangle);
        }

        // Signal field
        for (int i = 0; i < _dots.Length; i++)
        {
            var (p, v) = _dots[i];
            p = new PointF((p.X + v.X + W) % W, (p.Y + v.Y + H) % H);
            _dots[i] = (p, v);
        }
        using (var dot = new SolidBrush(Color.FromArgb(120, Theme.Muted)))
            foreach (var (p, _) in _dots) g.FillRectangle(dot, p.X, p.Y, 2, 2);
        for (int i = 0; i < _dots.Length; i++)
            for (int j = i + 1; j < _dots.Length; j++)
            {
                float dx = _dots[i].P.X - _dots[j].P.X, dy = _dots[i].P.Y - _dots[j].P.Y, dd = dx * dx + dy * dy;
                if (dd > 9000) continue;
                using var pen = new Pen(Color.FromArgb((int)(40 * (1 - dd / 9000)), Theme.Cyan), 1);
                g.DrawLine(pen, _dots[i].P, _dots[j].P);
            }

        // Wave lines
        for (int k = 0; k < 3; k++)
        {
            var pts = new PointF[W / 8 + 2];
            for (int i = 0; i < pts.Length; i++)
            {
                float x = i * 8;
                pts[i] = new PointF(x, H * .80f + MathF.Sin(x / 140f + t * (1.6f + k * .5f) + k) * (10 + k * 7));
            }
            using var pen = new Pen(Color.FromArgb(38 - k * 10, Theme.Cyan), 1.4f);
            g.DrawCurve(pen, pts, .3f);
        }

        // Orbit rings
        float ringIn = Theme.EaseOutCubic(t / .9f);
        DrawRing(g, center, 300 * ringIn, 95 * ringIn, t * 140, 120, Theme.Cyan, 2.2f, (int)(220 * ringIn));
        DrawRing(g, center, 340 * ringIn, 110 * ringIn, -t * 90 + 60, 90, Theme.Blue, 1.8f, (int)(170 * ringIn));
        DrawRing(g, center, 320 * ringIn, 102 * ringIn, t * 60 + 200, 50, Theme.Cyan, 1.2f, (int)(110 * ringIn));

        // Logo render-in
        float lt = Math.Clamp((t - .15f) / 1.3f, 0, 1);
        float scale = .6f + .4f * Theme.EaseOutBack(lt);
        float alpha = Theme.EaseOutCubic(lt);
        float flash = lt < .6f ? lt / .6f : Math.Max(0, 1 - (lt - .6f) / .4f);
        float lw = _logo.Width * scale, lh = _logo.Height * scale;
        var rect = new RectangleF(center.X - lw / 2, center.Y - lh / 2, lw, lh);

        float glowPulse = .55f + .25f * MathF.Sin(t * 2.2f);
        float gs = scale * (_glow.Width / (float)_logo.Width);
        var grect = new RectangleF(center.X - _glow.Width * scale / 2, center.Y - _glow.Height * scale / 2, _glow.Width * scale, _glow.Height * scale);
        DrawWithAlpha(g, _glow, grect, alpha * glowPulse, 1);
        // de-blur: draw a few offset ghost copies while the logo is still "out of focus"
        float blur = (1 - lt) * 14;
        if (blur > .5f)
            for (int k = 0; k < 4; k++)
            {
                float a = k * MathF.PI / 2;
                var r2 = rect; r2.Offset(MathF.Cos(a) * blur, MathF.Sin(a) * blur);
                DrawWithAlpha(g, _logo, r2, alpha * .25f, 1 + flash);
            }
        DrawWithAlpha(g, _logo, rect, alpha, 1 + flash * .9f);

        // Light sweep across the logo shape (every 5 s after the render)
        float st = (t - 1.25f) % 5f;
        if (t > 1.25f && st < 1.1f)
        {
            UpdateSweep(st / 1.1f);
            g.DrawImage(_sweep, rect);
        }

        // Tagline + status
        float tagA = Theme.EaseOutCubic((t - 1.2f) / .7f);
        using (var b = new SolidBrush(Color.FromArgb((int)(255 * tagA), Theme.Muted)))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            g.DrawString("C O M M U N I C A T I O N S   P L A T F O R M   ·   A F I F - T E C H", _tagFont, b,
                new RectangleF(0, center.Y + 112 + (1 - tagA) * 10, W, 24), sf);

        var bar = new RectangleF(W / 2f - 150, H - 74, 300, 4);
        using (var track = new SolidBrush(Color.FromArgb(50, Theme.Muted))) g.FillRectangle(track, bar);
        float seg = 90, pos = (t * 160) % (bar.Width + seg) - seg;
        var segRect = RectangleF.Intersect(new RectangleF(bar.X + pos, bar.Y, seg, bar.Height), bar);
        if (segRect.Width > 0)
            using (var lg = new LinearGradientBrush(new RectangleF(bar.X + pos - 1, bar.Y, seg + 2, bar.Height), Color.FromArgb(0, Theme.Cyan), Theme.Cyan, 0f))
            {
                var blend = new ColorBlend { Colors = new[] { Color.FromArgb(0, Theme.Cyan), Theme.Cyan, Color.FromArgb(0, Theme.Cyan) }, Positions = new[] { 0f, .5f, 1f } };
                lg.InterpolationColors = blend;
                g.FillRectangle(lg, segRect);
            }
        using (var b = new SolidBrush(Theme.Text))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            g.DrawString(_status, _statusFont, b, new RectangleF(0, H - 60, W, 26), sf);

        // Border
        using (var pen = new Pen(Color.FromArgb(70, Theme.Cyan), 1)) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
    }

    private static void DrawRing(Graphics g, PointF c, float rx, float ry, float start, float sweep, Color col, float width, int alpha)
    {
        if (rx < 2) return;
        var r = new RectangleF(c.X - rx, c.Y - ry, rx * 2, ry * 2);
        alpha = Math.Clamp(alpha, 0, 255);
        using (var faint = new Pen(Color.FromArgb(alpha / 6, col), 1)) g.DrawEllipse(faint, r);
        using (var glow = new Pen(Color.FromArgb(alpha / 4, col), width * 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(glow, r, start, sweep);
        using var pen = new Pen(Color.FromArgb(alpha, col), width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, r, start, sweep);
    }

    private static void DrawWithAlpha(Graphics g, Image img, RectangleF dest, float alpha, float brightness)
    {
        if (alpha <= 0.01f) return;
        float b = brightness - 1;
        var cm = new ColorMatrix(new[]
        {
            new float[] { 1, 0, 0, 0, 0 },
            new float[] { 0, 1, 0, 0, 0 },
            new float[] { 0, 0, 1, 0, 0 },
            new float[] { 0, 0, 0, Math.Clamp(alpha, 0, 1), 0 },
            new float[] { b, b, b, 0, 1 },
        });
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(cm);
        var pts = new[] { dest.Location, new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) };
        g.DrawImage(img, pts, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
    }

    /// <summary>White diagonal band, masked by the logo's alpha so it only lights up the logo itself.</summary>
    private void UpdateSweep(float p)
    {
        int w = _sweep.Width, h = _sweep.Height;
        var buf = new byte[_sweepStride * h];
        float center = -0.3f * w + p * 1.6f * w, half = w * .09f;
        for (int y = 0; y < h; y++)
        {
            float shift = (h - y) * .45f;
            int x0 = Math.Max(0, (int)(center + shift - half)), x1 = Math.Min(w - 1, (int)(center + shift + half));
            for (int x = x0; x <= x1; x++)
            {
                float d = Math.Abs(x - (center + shift)) / half;
                float inten = MathF.Exp(-d * d * 3.2f);
                int a = (int)(_logoAlpha[y * w + x] * inten * .85f);
                if (a <= 0) continue;
                int i = y * _sweepStride + x * 4;
                buf[i] = 255; buf[i + 1] = 255; buf[i + 2] = 255; buf[i + 3] = (byte)a;
            }
        }
        var d2 = _sweep.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(buf, 0, d2.Scan0, buf.Length);
        _sweep.UnlockBits(d2);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame.Dispose(); _logo.Dispose(); _glow.Dispose(); _sweep.Dispose();
            _statusFont.Dispose(); _tagFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
