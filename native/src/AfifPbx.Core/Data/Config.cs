using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AfifPbx.Data;

public sealed class PbxSettings
{
    public int SipPort { get; set; } = 5060;
    /// <summary>More SIP ports to listen on. 5160 is where FreePBX's chan_sip listened, so phones set up for
    /// the Raspberry Pi keep working unchanged.</summary>
    public List<int> ExtraSipPorts { get; set; } = new() { 5160 };
    public int RtpPortStart { get; set; } = 10000;
    public int RtpPortEnd { get; set; } = 20000;
    /// <summary>Voice ports used for phones on the built-in secure network (relayed on the tailnet; 100 calls).</summary>
    public int TailnetRtpStart { get; set; } = 21000;
    public int TailnetRtpEnd { get; set; } = 21199;
    public int HttpPort { get; set; } = 80;
    public string Realm { get; set; } = "afif-pbx";
    public string AdminUser { get; set; } = "admin";
    public string AdminPasswordHash { get; set; } = "";
    public bool AdminMustChangePassword { get; set; } = true;

    public bool GsmEnabled { get; set; } = true;
    /// <summary>Empty = detect the Huawei "PC UI" COM port automatically.</summary>
    public string GsmAtPort { get; set; } = "";
    /// <summary>Empty = detect the Huawei "Application Interface" COM port automatically.</summary>
    public string GsmAudioPort { get; set; } = "";
    /// <summary>Where calls arriving on the SIM go, e.g. "rg:600", "ext:100", "ivr:700".</summary>
    public string GsmInboundDestination { get; set; } = "rg:600";
    /// <summary>Extensions whose phones (AFIF Phone) get incoming SMS as messages; empty = every extension.</summary>
    public List<string> SmsExtensions { get; set; } = new();
    /// <summary>Numbers matching this (and not an internal number) go out through the GSM line.</summary>
    public string OutboundPattern { get; set; } = @"^\+?\d{3,}$";
    /// <summary>Digits removed from the front of an outbound number (e.g. an outside-line "9").</summary>
    public string OutboundStripPrefix { get; set; } = "";
    public bool RecordGsmCalls { get; set; }
    public int GsmRxGain { get; set; }
    public int GsmTxGain { get; set; }

    public int RingSeconds { get; set; } = 25;
    public int VoicemailMaxSeconds { get; set; } = 180;
    /// <summary>Windows text-to-speech voice for prompts; empty = system default.</summary>
    public string TtsVoice { get; set; } = "";
}

public sealed class Extension
{
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool VoicemailEnabled { get; set; } = true;
    public string VoicemailPin { get; set; } = "1234";
    public bool RecordCalls { get; set; }
    /// <summary>Destination when not answered/unavailable. Empty = this extension's voicemail (or hang up).</summary>
    public string NoAnswerDestination { get; set; } = "";
    /// <summary>Optional uploaded voicemail greeting (file name in Prompts folder).</summary>
    public string GreetingFile { get; set; } = "";
}

public sealed class RingGroup
{
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();
    /// <summary>"ringall" or "hunt".</summary>
    public string Strategy { get; set; } = "ringall";
    public int RingSeconds { get; set; } = 20;
    public string FailoverDestination { get; set; } = "hangup";
}

public sealed class Ivr
{
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Spoken with Windows text-to-speech when no prompt file is uploaded.</summary>
    public string PromptText { get; set; } = "";
    public string PromptFile { get; set; } = "";
    /// <summary>Digit → destination (e.g. "1" → "ext:100").</summary>
    public Dictionary<string, string> Options { get; set; } = new();
    public string TimeoutDestination { get; set; } = "hangup";
    public int TimeoutSeconds { get; set; } = 6;
    public bool DirectDial { get; set; } = true;
}

public sealed class PbxConfig
{
    public PbxSettings Settings { get; set; } = new();
    public List<Extension> Extensions { get; set; } = new();
    public List<RingGroup> RingGroups { get; set; } = new();
    public List<Ivr> Ivrs { get; set; } = new();

    public static PbxConfig CreateDefault()
    {
        var c = new PbxConfig();
        c.Settings.AdminPasswordHash = Passwords.Hash("admin");
        c.Extensions.Add(new Extension { Number = "100", Name = "Reception", Secret = Passwords.NewSecret() });
        c.Extensions.Add(new Extension { Number = "101", Name = "Office", Secret = Passwords.NewSecret() });
        c.RingGroups.Add(new RingGroup { Number = "600", Name = "All phones", Members = new() { "100", "101" }, FailoverDestination = "vm:100" });
        c.Ivrs.Add(new Ivr
        {
            Number = "700",
            Name = "Main menu",
            PromptText = "Welcome to AFIF PBX. For reception, press 1. For the office, press 2. Or dial an extension number at any time.",
            Options = new() { ["1"] = "ext:100", ["2"] = "ext:101", ["0"] = "rg:600" },
            TimeoutDestination = "rg:600",
        });
        return c;
    }
}

/// <summary>Loads and saves config.json, and tells listeners when it changes.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private PbxConfig _config;

    public event Action<PbxConfig>? Changed;
    public string DataDir { get; }

    public ConfigStore(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "config.json");
        if (File.Exists(_path))
        {
            _config = JsonSerializer.Deserialize<PbxConfig>(File.ReadAllText(_path), Json) ?? PbxConfig.CreateDefault();
        }
        else
        {
            _config = PbxConfig.CreateDefault();
            Save(_config);
        }
    }

    /// <summary>A deep copy of the current configuration.</summary>
    public PbxConfig Get()
    {
        lock (_lock) return Clone(_config);
    }

    /// <summary>Read-only access to the live configuration (do not modify).</summary>
    public PbxConfig Current { get { lock (_lock) return _config; } }

    public void Update(Action<PbxConfig> change)
    {
        PbxConfig updated;
        lock (_lock)
        {
            updated = Clone(_config);
            change(updated);
            Save(updated);
            _config = updated;
        }
        Changed?.Invoke(updated);
    }

    private void Save(PbxConfig c)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static PbxConfig Clone(PbxConfig c) =>
        JsonSerializer.Deserialize<PbxConfig>(JsonSerializer.Serialize(c, Json), Json)!;

    public static string DefaultDataDir()
    {
        var env = Environment.GetEnvironmentVariable("AFIF_PBX_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AFIF-PBX")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".afif-pbx");
    }
}

public static class Passwords
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "pbkdf2") return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>A SIP password that phones accept and people can type.</summary>
    public static string NewSecret(int length = 12)
    {
        const string chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, length).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }
}
