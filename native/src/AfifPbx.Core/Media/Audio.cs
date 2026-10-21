namespace AfifPbx.Media;

/// <summary>Internal audio format: 8 kHz, 16-bit signed, mono, 20 ms frames.</summary>
public static class Audio
{
    public const int SampleRate = 8000;
    public const int FrameMs = 20;
    public const int FrameSamples = SampleRate * FrameMs / 1000; // 160
    public const int FrameBytes = FrameSamples * 2;              // 320

    public static short[] NewFrame() => new short[FrameSamples];

    public static double Rms(ReadOnlySpan<short> s)
    {
        if (s.IsEmpty) return 0;
        double sum = 0;
        foreach (var v in s) sum += (double)v * v;
        return Math.Sqrt(sum / s.Length);
    }

    /// <summary>Split a long sample buffer into 20 ms frames (last one zero-padded).</summary>
    public static IEnumerable<short[]> Frames(short[] samples)
    {
        for (int i = 0; i < samples.Length; i += FrameSamples)
        {
            var f = NewFrame();
            Array.Copy(samples, i, f, 0, Math.Min(FrameSamples, samples.Length - i));
            yield return f;
        }
    }

    public static short[] BytesToSamples(ReadOnlySpan<byte> b)
    {
        var s = new short[b.Length / 2];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(b[2 * i] | (b[2 * i + 1] << 8));
        return s;
    }

    public static byte[] SamplesToBytes(ReadOnlySpan<short> s)
    {
        var b = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++) { b[2 * i] = (byte)s[i]; b[2 * i + 1] = (byte)(s[i] >> 8); }
        return b;
    }

    public static short[] Concat(params short[][] parts)
    {
        var r = new short[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }

    public static short[] Silence(int ms) => new short[SampleRate * ms / 1000];
}

/// <summary>ITU-T G.711 µ-law and A-law.</summary>
public static class G711
{
    private static readonly short[] UlawDecode = new short[256];
    private static readonly short[] AlawDecode = new short[256];
    private static readonly byte[] UlawEncode = new byte[65536];
    private static readonly byte[] AlawEncode = new byte[65536];

    static G711()
    {
        for (int i = 0; i < 256; i++)
        {
            UlawDecode[i] = DecodeUlaw((byte)i);
            AlawDecode[i] = DecodeAlaw((byte)i);
        }
        for (int i = 0; i < 65536; i++)
        {
            short s = (short)(i - 32768);
            UlawEncode[i] = EncUlaw(s);
            AlawEncode[i] = EncAlaw(s);
        }
    }

    public static short[] Decode(ReadOnlySpan<byte> data, bool alaw)
    {
        var table = alaw ? AlawDecode : UlawDecode;
        var r = new short[data.Length];
        for (int i = 0; i < data.Length; i++) r[i] = table[data[i]];
        return r;
    }

    public static byte[] Encode(ReadOnlySpan<short> pcm, bool alaw)
    {
        var table = alaw ? AlawEncode : UlawEncode;
        var r = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++) r[i] = table[pcm[i] + 32768];
        return r;
    }

    private static short DecodeUlaw(byte u)
    {
        u = (byte)~u;
        int t = ((u & 0x0F) << 3) + 0x84;
        t <<= (u & 0x70) >> 4;
        return (short)((u & 0x80) != 0 ? 0x84 - t : t - 0x84);
    }

    private static short DecodeAlaw(byte a)
    {
        a ^= 0x55;
        int t = (a & 0x0F) << 4;
        int seg = (a & 0x70) >> 4;
        switch (seg)
        {
            case 0: t += 8; break;
            case 1: t += 0x108; break;
            default: t += 0x108; t <<= seg - 1; break;
        }
        return (short)((a & 0x80) != 0 ? t : -t);
    }

    private static byte EncUlaw(short pcm)
    {
        const int BIAS = 0x84, CLIP = 32635;
        int sign = (pcm >> 8) & 0x80;
        int v = pcm;
        if (sign != 0) v = -v;
        if (v > CLIP) v = CLIP;
        v += BIAS;
        int exponent = 7;
        for (int mask = 0x4000; (v & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        int mantissa = (v >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    private static byte EncAlaw(short pcm)
    {
        int sign = (~pcm >> 8) & 0x80;
        int v = pcm;
        if (sign == 0) v = -v;
        if (v > 32635) v = 32635;
        byte compressed;
        if (v >= 256)
        {
            int exponent = 7;
            for (int mask = 0x4000; (v & mask) == 0 && exponent > 1; exponent--, mask >>= 1) { }
            int mantissa = (v >> (exponent + 3)) & 0x0F;
            compressed = (byte)((exponent << 4) | mantissa);
        }
        else compressed = (byte)(v >> 4);
        return (byte)(compressed ^ (sign ^ 0x55));
    }
}

/// <summary>Tone generation (beeps, ringback, test tones).</summary>
public static class Tones
{
    public static short[] Sine(double hz, int ms, double amplitude = 0.3, double hz2 = 0)
    {
        int n = Audio.SampleRate * ms / 1000;
        var s = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Audio.SampleRate;
            double v = Math.Sin(2 * Math.PI * hz * t);
            if (hz2 > 0) v = (v + Math.Sin(2 * Math.PI * hz2 * t)) / 2;
            // 5 ms fade in/out to avoid clicks
            double fade = Math.Min(1, Math.Min(i, n - i) / 40.0);
            s[i] = (short)(v * amplitude * fade * short.MaxValue);
        }
        return s;
    }

    public static short[] Beep() => Sine(1000, 350, 0.35);

    public static short[] Dtmf(char digit, int ms = 120)
    {
        var (lo, hi) = DtmfDetector.Frequencies(digit);
        return lo == 0 ? Audio.Silence(ms) : Sine(lo, ms, 0.4, hi);
    }
}
