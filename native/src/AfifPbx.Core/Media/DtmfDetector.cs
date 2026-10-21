namespace AfifPbx.Media;

/// <summary>
/// In-band DTMF detection (Goertzel) for audio legs that do not send RFC 2833 events,
/// e.g. callers coming in over the GSM dongle.
/// </summary>
public sealed class DtmfDetector
{
    private static readonly double[] Low = { 697, 770, 852, 941 };
    private static readonly double[] High = { 1209, 1336, 1477, 1633 };
    private static readonly char[,] Keys =
    {
        { '1', '2', '3', 'A' },
        { '4', '5', '6', 'B' },
        { '7', '8', '9', 'C' },
        { '*', '0', '#', 'D' },
    };

    private char _current = '\0';
    private int _hits;
    private int _gap;
    private bool _reported;

    public event Action<char>? Digit;

    public static (double lo, double hi) Frequencies(char digit)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (Keys[r, c] == char.ToUpperInvariant(digit)) return (Low[r], High[c]);
        return (0, 0);
    }

    /// <summary>Feed one 20 ms frame.</summary>
    public void Process(ReadOnlySpan<short> frame)
    {
        char d = Detect(frame);
        if (d != '\0' && d == _current)
        {
            _hits++;
            _gap = 0;
            if (_hits >= 2 && !_reported) { _reported = true; Digit?.Invoke(d); }
        }
        else if (d != '\0')
        {
            _current = d; _hits = 1; _gap = 0; _reported = false;
        }
        else if (++_gap >= 2)
        {
            _current = '\0'; _hits = 0; _reported = false;
        }
    }

    private static char Detect(ReadOnlySpan<short> f)
    {
        double energy = 0;
        foreach (var s in f) energy += (double)s * s;
        if (energy / f.Length < 400 * 400) return '\0'; // too quiet

        double[] lo = new double[4], hi = new double[4];
        for (int i = 0; i < 4; i++) { lo[i] = Goertzel(f, Low[i]); hi[i] = Goertzel(f, High[i]); }
        int li = ArgMax(lo), hi2 = ArgMax(hi);
        double lmax = lo[li], hmax = hi[hi2];
        if (lmax <= 0 || hmax <= 0) return '\0';

        // the strongest tones must clearly dominate their group
        for (int i = 0; i < 4; i++)
        {
            if (i != li && lo[i] * 4 > lmax) return '\0';
            if (i != hi2 && hi[i] * 4 > hmax) return '\0';
        }
        // twist: the two tones must be of similar strength
        double twist = lmax / hmax;
        if (twist > 8 || twist < 1 / 8.0) return '\0';
        // tones must carry most of the frame energy
        double total = energy * f.Length / 2;
        if ((lmax + hmax) < total * 0.3) return '\0';
        return Keys[li, hi2];
    }

    private static int ArgMax(double[] a)
    {
        int m = 0;
        for (int i = 1; i < a.Length; i++) if (a[i] > a[m]) m = i;
        return m;
    }

    private static double Goertzel(ReadOnlySpan<short> f, double freq)
    {
        double k = 2 * Math.Cos(2 * Math.PI * freq / Audio.SampleRate);
        double s1 = 0, s2 = 0;
        foreach (var x in f)
        {
            double s0 = x + k * s1 - s2;
            s2 = s1; s1 = s0;
        }
        return s1 * s1 + s2 * s2 - k * s1 * s2;
    }
}
