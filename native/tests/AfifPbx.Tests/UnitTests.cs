using AfifPbx.Gsm;
using AfifPbx.Media;
using Xunit;

namespace AfifPbx.Tests;

public class MediaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void G711_round_trip_is_close(bool alaw)
    {
        var tone = Tones.Sine(440, 100, 0.5);
        var back = G711.Decode(G711.Encode(tone, alaw), alaw);
        double err = 0;
        for (int i = 0; i < tone.Length; i++) err += Math.Abs(tone[i] - back[i]);
        err /= tone.Length;
        Assert.True(err < 300, $"mean error {err}");
        Assert.InRange(Audio.Rms(back), Audio.Rms(tone) * 0.9, Audio.Rms(tone) * 1.1);
    }

    [Fact]
    public void Dtmf_detects_every_key_and_ignores_noise()
    {
        foreach (var key in "0123456789*#")
        {
            var det = new DtmfDetector();
            var seen = new List<char>();
            det.Digit += seen.Add;
            foreach (var f in Audio.Frames(Audio.Concat(Tones.Dtmf(key, 120), Audio.Silence(60)))) det.Process(f);
            Assert.Equal(new[] { key }, seen);
        }
        var noise = new DtmfDetector();
        int hits = 0;
        noise.Digit += _ => hits++;
        var rnd = new Random(1);
        for (int i = 0; i < 300; i++)
        {
            var f = new short[Audio.FrameSamples];
            for (int j = 0; j < f.Length; j++) f[j] = (short)rnd.Next(-6000, 6000);
            noise.Process(f);
        }
        foreach (var f in Audio.Frames(Tones.Sine(1000, 1000, 0.5))) noise.Process(f);
        Assert.Equal(0, hits);
    }

    [Fact]
    public void Dtmf_survives_g711()
    {
        var det = new DtmfDetector();
        var seen = new List<char>();
        det.Digit += seen.Add;
        var audio = G711.Decode(G711.Encode(Audio.Concat(Tones.Dtmf('7', 140), Audio.Silence(80), Tones.Dtmf('3', 140)), false), false);
        foreach (var f in Audio.Frames(audio)) det.Process(f);
        Assert.Equal("73", new string(seen.ToArray()));
    }

    [Fact]
    public void Wav_round_trip_and_resample()
    {
        var path = Path.Combine(Path.GetTempPath(), $"afif-{Guid.NewGuid():N}.wav");
        var tone = Tones.Sine(500, 250);
        Wav.Save(path, tone);
        var back = Wav.Load(path);
        Assert.Equal(tone, back);
        File.Delete(path);

        var at16k = Wav.Resample(Tones.Sine(500, 1000), 8000, 16000);
        Assert.Equal(16000, at16k.Length);
        var down = Wav.Resample(at16k, 16000, 8000);
        Assert.Equal(8000, down.Length);
    }
}

public class SmsPduTests
{
    [Fact]
    public void Decodes_gsm7_reference_pdu()
    {
        var sms = SmsPdu.DecodeDeliver("07911326040000F0040B911346610089F60000208062917314080CC8F71D14969741F977FD07");
        Assert.Equal("+31641600986", sms.Sender);
        Assert.Equal("How are you?", sms.Text);
    }

    [Fact]
    public void Decodes_ucs2_arabic()
    {
        var sms = SmsPdu.DecodeDeliver("00040C916972191122330008621070210000001406450631062D0628062700200041004600490046");
        Assert.Equal("+962791112233", sms.Sender);
        Assert.Equal("مرحبا AFIF", sms.Text);
        Assert.NotNull(sms.Timestamp);
    }

    [Fact]
    public void Decodes_multipart_headers()
    {
        var p1 = SmsPdu.DecodeDeliver("00440C916972191122330000621070210000000C0500032A0201906536FB0D");
        var p2 = SmsPdu.DecodeDeliver("00440C916972191122330000621070210000000D0500032A020240D7B79C4D06");
        Assert.Equal("Hello", p1.Text);
        Assert.Equal(" World", p2.Text);
        Assert.Equal(0x2A, p1.ConcatRef);
        Assert.Equal(2, p1.ConcatTotal);
        Assert.Equal(1, p1.ConcatSeq);
        Assert.Equal(2, p2.ConcatSeq);
    }

    [Fact]
    public void Encodes_reference_submit()
    {
        var parts = SmsPdu.EncodeSubmit("+46708251358", "hellohello");
        Assert.Single(parts);
        Assert.Equal("0001000B916407281553F800000AE8329BFD4697D9EC37", parts[0].Pdu);
        Assert.Equal(22, parts[0].TpduLength);
    }

    [Fact]
    public void Splits_long_arabic_into_parts()
    {
        var text = string.Concat(Enumerable.Repeat("مرحبا ", 30)); // 180 chars
        var parts = SmsPdu.EncodeSubmit("0791234567", text);
        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.Equal((p.Pdu.Length - 2) / 2, p.TpduLength));
    }
}
