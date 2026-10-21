using System.Text;

namespace AfifPbx.Media;

/// <summary>Reads any PCM/µ-law/A-law WAV into 8 kHz mono samples; writes 8 kHz 16-bit WAV.</summary>
public static class Wav
{
    public static short[] Load(string path) => Load(File.ReadAllBytes(path));

    public static short[] Load(byte[] data)
    {
        using var br = new BinaryReader(new MemoryStream(data));
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a WAV file");
        br.ReadInt32();
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAV file");
        int format = 1, channels = 1, rate = 8000, bits = 16;
        byte[]? pcm = null;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            string id = Encoding.ASCII.GetString(br.ReadBytes(4));
            int size = br.ReadInt32();
            long next = br.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = br.ReadInt16(); channels = br.ReadInt16(); rate = br.ReadInt32();
                br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                if (format == unchecked((short)0xFFFE) || format == -2) format = 1; // WAVE_FORMAT_EXTENSIBLE (assume PCM)
            }
            else if (id == "data")
            {
                pcm = br.ReadBytes((int)Math.Min(size, br.BaseStream.Length - br.BaseStream.Position));
            }
            if (next > br.BaseStream.Length) break;
            br.BaseStream.Position = next;
        }
        if (pcm is null) throw new InvalidDataException("WAV has no audio data");

        // decode to mono 16-bit
        short[] mono;
        int frames;
        switch (format)
        {
            case 1 when bits == 16:
                frames = pcm.Length / (2 * channels);
                mono = new short[frames];
                for (int i = 0; i < frames; i++)
                {
                    int acc = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int o = (i * channels + c) * 2;
                        acc += (short)(pcm[o] | (pcm[o + 1] << 8));
                    }
                    mono[i] = (short)(acc / channels);
                }
                break;
            case 1 when bits == 8:
                frames = pcm.Length / channels;
                mono = new short[frames];
                for (int i = 0; i < frames; i++)
                {
                    int acc = 0;
                    for (int c = 0; c < channels; c++) acc += (pcm[i * channels + c] - 128) << 8;
                    mono[i] = (short)(acc / channels);
                }
                break;
            case 1 when bits == 24 || bits == 32:
                {
                    int bps = bits / 8;
                    frames = pcm.Length / (bps * channels);
                    mono = new short[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        long acc = 0;
                        for (int c = 0; c < channels; c++)
                        {
                            int o = (i * channels + c) * bps;
                            acc += (short)(pcm[o + bps - 2] | (pcm[o + bps - 1] << 8));
                        }
                        mono[i] = (short)(acc / channels);
                    }
                    break;
                }
            case 3 when bits == 32: // IEEE float
                frames = pcm.Length / (4 * channels);
                mono = new short[frames];
                for (int i = 0; i < frames; i++)
                {
                    double acc = 0;
                    for (int c = 0; c < channels; c++) acc += BitConverter.ToSingle(pcm, (i * channels + c) * 4);
                    mono[i] = (short)Math.Clamp(acc / channels * short.MaxValue, short.MinValue, short.MaxValue);
                }
                break;
            case 6 or 7: // A-law / µ-law
                {
                    var dec = G711.Decode(pcm, format == 6);
                    frames = dec.Length / channels;
                    mono = new short[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        int acc = 0;
                        for (int c = 0; c < channels; c++) acc += dec[i * channels + c];
                        mono[i] = (short)(acc / channels);
                    }
                    break;
                }
            default:
                throw new InvalidDataException($"Unsupported WAV encoding (format {format}, {bits} bit). Please use PCM WAV.");
        }
        return rate == Audio.SampleRate ? mono : Resample(mono, rate, Audio.SampleRate);
    }

    /// <summary>Linear-interpolation resampler with a simple low-pass when downsampling.</summary>
    public static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;
        double[] src = input.Select(x => (double)x).ToArray();
        if (toRate < fromRate)
        {
            // moving-average low-pass with a window matching the decimation ratio
            int win = Math.Max(1, (int)Math.Round((double)fromRate / toRate));
            var lp = new double[src.Length];
            double acc = 0;
            for (int i = 0; i < src.Length; i++)
            {
                acc += src[i];
                if (i >= win) acc -= src[i - win];
                lp[i] = acc / Math.Min(i + 1, win);
            }
            src = lp;
        }
        int outLen = (int)((long)input.Length * toRate / fromRate);
        var r = new short[outLen];
        double step = (double)fromRate / toRate;
        for (int i = 0; i < outLen; i++)
        {
            double pos = i * step;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            double frac = pos - i0;
            r[i] = (short)Math.Clamp(src[i0] * (1 - frac) + src[i1] * frac, short.MinValue, short.MaxValue);
        }
        return r;
    }

    public static void Save(string path, ReadOnlySpan<short> samples, int channels = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        WriteHeader(fs, samples.Length * 2, channels);
        fs.Write(Audio.SamplesToBytes(samples));
    }

    public static byte[] ToBytes(ReadOnlySpan<short> samples)
    {
        using var ms = new MemoryStream();
        WriteHeader(ms, samples.Length * 2, 1);
        ms.Write(Audio.SamplesToBytes(samples));
        return ms.ToArray();
    }

    internal static void WriteHeader(Stream s, int dataBytes, int channels)
    {
        using var bw = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(Audio.SampleRate);
        bw.Write(Audio.SampleRate * 2 * channels);
        bw.Write((short)(2 * channels));
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
    }
}

/// <summary>
/// Streams a call recording to disk as a stereo WAV (left = caller, right = callee),
/// patching the header sizes when it is closed.
/// </summary>
public sealed class WavRecorder : IDisposable
{
    private readonly FileStream _fs;
    private int _dataBytes;
    private readonly object _lock = new();
    public string Path { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds(_dataBytes / 4.0 / Audio.SampleRate);

    public WavRecorder(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        Wav.WriteHeader(_fs, 0, 2);
    }

    public void Write(ReadOnlySpan<short> left, ReadOnlySpan<short> right)
    {
        int n = Math.Max(left.Length, right.Length);
        var buf = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            short l = i < left.Length ? left[i] : (short)0;
            short r = i < right.Length ? right[i] : (short)0;
            buf[4 * i] = (byte)l; buf[4 * i + 1] = (byte)(l >> 8);
            buf[4 * i + 2] = (byte)r; buf[4 * i + 3] = (byte)(r >> 8);
        }
        lock (_lock)
        {
            if (!_fs.CanWrite) return;
            _fs.Write(buf);
            _dataBytes += buf.Length;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_fs.CanWrite) return;
            _fs.Position = 0;
            Wav.WriteHeader(_fs, _dataBytes, 2);
            _fs.Flush();
            _fs.Dispose();
        }
    }
}
