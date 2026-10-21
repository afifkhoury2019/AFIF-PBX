using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AfifPbx;

internal static class Theme
{
    public static readonly Color Navy = Color.FromArgb(7, 13, 29);
    public static readonly Color Navy2 = Color.FromArgb(19, 35, 74);
    public static readonly Color Panel = Color.FromArgb(14, 26, 54);
    public static readonly Color Cyan = Color.FromArgb(56, 225, 255);
    public static readonly Color Blue = Color.FromArgb(37, 99, 235);
    public static readonly Color Text = Color.FromArgb(230, 239, 255);
    public static readonly Color Muted = Color.FromArgb(127, 166, 217);
    public static readonly Color Good = Color.FromArgb(52, 211, 153);
    public static readonly Color Warn = Color.FromArgb(251, 191, 36);
    public static readonly Color Bad = Color.FromArgb(248, 113, 113);

    private static Bitmap? _logo;
    private static Icon? _icon;

    public static Bitmap Logo => _logo ??= LoadLogo();
    public static Icon AppIcon => _icon ??= LoadIcon();

    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
    {
        foreach (var name in new[] { "Segoe UI Variable Display", "Segoe UI", "Arial" })
        {
            try
            {
                var f = new Font(name, size, style, GraphicsUnit.Point);
                if (f.Name == name) return f;
                f.Dispose();
            }
            catch { }
        }
        return new Font(FontFamily.GenericSansSerif, size, style);
    }

    private static Stream? Res(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    private static Bitmap LoadLogo()
    {
        // A logo.png placed next to the exe overrides the built-in one (easy re-branding).
        var external = Path.Combine(AppContext.BaseDirectory, "logo.png");
        if (File.Exists(external)) return new Bitmap(external);
        using var s = Res("logo.png");
        return s is null ? new Bitmap(1, 1) : new Bitmap(s);
    }

    private static Icon LoadIcon()
    {
        using var s = Res("afif-pbx.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }

    /// <summary>Rounded corners + shadow on Windows 11 for borderless forms.</summary>
    public static void RoundCorners(Form f)
    {
        try
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(f.Handle, 33, ref pref, sizeof(int));
            int dark = 1;
            DwmSetWindowAttribute(f.Handle, 20, ref dark, sizeof(int)); // immersive dark mode title bar
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Soft cyan glow made from the logo's alpha channel (computed once).</summary>
    public static Bitmap MakeGlow(Bitmap src, Color tint, int radius)
    {
        int pad = radius * 2;
        int w = src.Width + pad * 2, h = src.Height + pad * 2;
        var alpha = new byte[w * h];
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[data.Stride * data.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                alpha[(y + pad) * w + x + pad] = px[y * data.Stride + x * 4 + 3];
        bmp.Dispose();

        // three box-blur passes ~ gaussian
        var tmp = new byte[w * h];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(alpha, tmp, w, h, radius / 2 + 1, true);
            BoxBlur(tmp, alpha, w, h, radius / 2 + 1, false);
        }

        var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var od = outBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var opx = new byte[od.Stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * od.Stride + x * 4;
                opx[i] = tint.B; opx[i + 1] = tint.G; opx[i + 2] = tint.R;
                opx[i + 3] = (byte)Math.Min(255, alpha[y * w + x] * 2);
            }
        Marshal.Copy(opx, 0, od.Scan0, opx.Length);
        outBmp.UnlockBits(od);
        return outBmp;
    }

    private static void BoxBlur(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int len = horizontal ? w : h, lines = horizontal ? h : w;
        for (int l = 0; l < lines; l++)
        {
            int sum = 0;
            int Idx(int k) => horizontal ? l * w + k : k * w + l;
            for (int k = -r; k <= r; k++) sum += src[Idx(Math.Clamp(k, 0, len - 1))];
            for (int k = 0; k < len; k++)
            {
                dst[Idx(k)] = (byte)(sum / (2 * r + 1));
                sum += src[Idx(Math.Min(k + r + 1, len - 1))] - src[Idx(Math.Max(k - r, 0))];
            }
        }
    }

    public static float EaseOutCubic(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 3);
    public static float EaseOutBack(float t)
    {
        t = Math.Clamp(t, 0, 1);
        const float c1 = 1.70158f, c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(t - 1, 3) + c1 * MathF.Pow(t - 1, 2);
    }
}
