using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AfifPbx.Media;

namespace AfifPbx.Tts;

/// <summary>Turns text into 8 kHz call audio.</summary>
public interface ISpeech
{
    short[] Speak(string text);
    IReadOnlyList<string> Voices();
}

/// <summary>Windows text-to-speech (SAPI voices installed on the PC, including Arabic ones if added).</summary>
public sealed class WindowsSpeech : ISpeech
{
    private readonly Func<string> _voice;
    private readonly object _lock = new();

    public WindowsSpeech(Func<string> voice) => _voice = voice;

    public short[] Speak(string text)
    {
        if (!OperatingSystem.IsWindows()) return FallbackSpeech.Render(text);
        try { return SpeakWindows(text); }
        catch { return FallbackSpeech.Render(text); } // no voices installed / SAPI unavailable
    }

    private short[] SpeakWindows(string text)
    {
        lock (_lock)
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            var v = _voice();
            if (!string.IsNullOrWhiteSpace(v))
            {
                try { synth.SelectVoice(v); } catch { }
            }
            else
            {
                // Prefer an Arabic voice when the text is Arabic
                bool arabic = text.Any(c => c >= '؀' && c <= 'ۿ');
                if (arabic)
                {
                    var ar = synth.GetInstalledVoices().FirstOrDefault(x => x.Enabled && x.VoiceInfo.Culture.TwoLetterISOLanguageName == "ar");
                    if (ar is not null) synth.SelectVoice(ar.VoiceInfo.Name);
                }
            }
            synth.Rate = 0;
            using var ms = new MemoryStream();
            synth.SetOutputToAudioStream(ms, new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                Audio.SampleRate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            synth.Speak(text);
            synth.SetOutputToNull();
            return Audio.BytesToSamples(ms.ToArray());
        }
    }

    public IReadOnlyList<string> Voices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        return synth.GetInstalledVoices().Where(v => v.Enabled)
            .Select(v => v.VoiceInfo.Name).ToList();
    }
}

/// <summary>Used where no speech engine exists (tests, non-Windows): a short soft tone per word.</summary>
public sealed class FallbackSpeech : ISpeech
{
    public short[] Speak(string text) => Render(text);
    public IReadOnlyList<string> Voices() => Array.Empty<string>();

    internal static short[] Render(string text)
    {
        int words = Math.Max(1, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var parts = new List<short[]>();
        for (int i = 0; i < Math.Min(words, 12); i++) { parts.Add(Tones.Sine(440, 60, 0.08)); parts.Add(Audio.Silence(60)); }
        return Audio.Concat(parts.ToArray());
    }
}

/// <summary>
/// Supplies prompt audio: uploaded WAV files from the Prompts folder, or spoken text (cached on disk).
/// </summary>
public sealed class PromptLibrary
{
    private readonly ISpeech _speech;
    private readonly string _dir;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, short[]> _memory = new();

    public string PromptDir => _dir;

    public PromptLibrary(ISpeech speech, string dataDir)
    {
        _speech = speech;
        _dir = Path.Combine(dataDir, "Prompts");
        _cacheDir = Path.Combine(dataDir, "Cache", "tts");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_cacheDir);
    }

    public short[] Say(string text)
    {
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        return _memory.GetOrAdd(key, _ =>
        {
            var file = Path.Combine(_cacheDir, key + ".wav");
            if (File.Exists(file))
            {
                try { return Wav.Load(file); } catch { }
            }
            var samples = _speech.Speak(text);
            try { Wav.Save(file, samples); } catch { }
            return samples;
        });
    }

    /// <summary>Uploaded prompt (by file name) if present, otherwise the spoken text.</summary>
    public short[] FileOrSay(string? fileName, string text)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var path = Path.Combine(_dir, Path.GetFileName(fileName));
            if (File.Exists(path))
            {
                try { return Wav.Load(path); } catch { }
            }
        }
        return Say(text);
    }

    public IReadOnlyList<string> Files() =>
        Directory.GetFiles(_dir, "*.wav").Select(Path.GetFileName).OfType<string>().OrderBy(x => x).ToList();

    /// <summary>Store an uploaded WAV after converting it to call format.</summary>
    public string SaveUpload(string fileName, byte[] data)
    {
        var samples = Wav.Load(data); // validates and converts to 8 kHz mono
        string safe = string.Concat(Path.GetFileNameWithoutExtension(fileName).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        if (string.IsNullOrEmpty(safe)) safe = "prompt";
        var name = safe + ".wav";
        Wav.Save(Path.Combine(_dir, name), samples);
        return name;
    }

    public void Delete(string fileName)
    {
        var path = Path.Combine(_dir, Path.GetFileName(fileName));
        if (File.Exists(path)) File.Delete(path);
    }

    public void ClearCache()
    {
        _memory.Clear();
        foreach (var f in Directory.GetFiles(_cacheDir, "*.wav")) { try { File.Delete(f); } catch { } }
    }

    public IReadOnlyList<string> Voices() => _speech.Voices();
}
