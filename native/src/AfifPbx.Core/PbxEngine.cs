using System.Net;
using AfifPbx.Calls;
using AfifPbx.Data;
using AfifPbx.Gsm;
using AfifPbx.Media;
using AfifPbx.Sip;
using AfifPbx.Tts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AfifPbx;

/// <summary>Everything that makes up a running AFIF-PBX.</summary>
public sealed class PbxEngine : IDisposable
{
    private readonly ILoggerFactory _logs;
    private readonly Func<(IModemPorts?, string, string)>? _modemOpener;

    public ConfigStore Config { get; }
    public Database Db { get; }
    public MediaClock Clock { get; } = new();
    public MediaOps Media { get; }
    public PromptLibrary Prompts { get; }
    public SipServer Sip { get; }
    public HuaweiModem? Modem { get; private set; }
    public CallManager Calls { get; private set; } = null!;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public string DataDir => Config.DataDir;

    public event Action<SmsMessage>? SmsReceived;

    /// <param name="modemOpener">Override how the dongle is opened (tests use a simulated modem).</param>
    public PbxEngine(string dataDir, ILoggerFactory? logs = null, ISpeech? speech = null,
        Func<(IModemPorts?, string, string)>? modemOpener = null)
    {
        _logs = logs ?? NullLoggerFactory.Instance;
        _modemOpener = modemOpener;
        Config = new ConfigStore(dataDir);
        Db = new Database(Path.Combine(dataDir, "afif-pbx.db"));
        Media = new MediaOps(Clock);
        Prompts = new PromptLibrary(speech ?? new WindowsSpeech(() => Config.Current.Settings.TtsVoice), dataDir);
        Sip = new SipServer(Config, _logs.CreateLogger("SIP"));
        var clockLog = _logs.CreateLogger("Media");
        Clock.OnError = ex => clockLog.LogError(ex, "Media error");
    }

    public void Start(IPAddress? sipBind = null)
    {
        var s = Config.Current.Settings;
        Sip.Start(sipBind ?? IPAddress.Any, s.SipPort, s.ExtraSipPorts);
        // loopback transports for phones that come through the built-in secure network (afif-net relays to them)
        Sip.StartRelay(new[] { s.SipPort }.Concat(s.ExtraSipPorts ?? new List<int>()));

        if (s.GsmEnabled)
        {
            var opener = _modemOpener ?? PortDiscovery.Opener(() =>
            {
                var cur = Config.Current.Settings;
                return (cur.GsmAtPort, cur.GsmAudioPort);
            });
            Modem = new HuaweiModem(opener, _logs.CreateLogger("GSM")) { RxGainDb = s.GsmRxGain, TxGainDb = s.GsmTxGain };
            Modem.SmsReceived += OnSms;
        }

        Calls = new CallManager(Config, Sip, Modem, Media, Db, Prompts, DataDir, _logs.CreateLogger("Calls"));

        // SMS ⇄ phones: texts written in AFIF Phone go out on the SIM; SMS that waited for an offline phone go out when it registers
        Sip.TextFromPhone = async (ext, number, text) => await SendSmsAsync(CleanNumber(number), text).ConfigureAwait(false);
        Sip.RegistrationChanged += (ext, registered) => { if (registered) _ = DeliverPendingSmsAsync(ext); };
        _smsRetry = new Timer(_ => _ = DeliverPendingSmsAsync(null), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        Modem?.Start();

        Config.Changed += c =>
        {
            if (Modem is not null) { Modem.RxGainDb = c.Settings.GsmRxGain; Modem.TxGainDb = c.Settings.GsmTxGain; }
        };
    }

    private Timer? _smsRetry;
    private readonly SemaphoreSlim _smsDelivery = new(1, 1);

    private void OnSms(IncomingSms sms)
    {
        long id = Db.AddSms("in", sms.Sender, sms.Text, "received");
        var msg = Db.Sms(1).FirstOrDefault(m => m.Id == id);
        foreach (var ext in SmsTargets()) Db.AddSmsPending(id, ext);
        _ = DeliverPendingSmsAsync(null);
        if (msg is not null) SmsReceived?.Invoke(msg);
    }

    /// <summary>Extensions that receive incoming SMS on their phones.</summary>
    private List<string> SmsTargets()
    {
        var c = Config.Current;
        var chosen = c.Settings.SmsExtensions ?? new List<string>();
        return c.Extensions.Where(e => e.Enabled && (chosen.Count == 0 || chosen.Contains(e.Number))).Select(e => e.Number).ToList();
    }

    /// <summary>Send waiting SMS to the phones that are registered now (one extension, or all when null).</summary>
    public async Task DeliverPendingSmsAsync(string? ext)
    {
        if (!await _smsDelivery.WaitAsync(0).ConfigureAwait(false))
        {
            // another delivery round is running; try again shortly so nothing is missed
            _ = Task.Delay(3000).ContinueWith(_ => DeliverPendingSmsAsync(ext));
            return;
        }
        try
        {
            foreach (var (pid, target, sms) in Db.SmsPending(ext))
            {
                if (!Sip.IsRegistered(target)) continue;
                string text = sms.Text;
                if (DateTime.UtcNow - sms.Time > TimeSpan.FromMinutes(5))
                    text += $"\n\n({sms.Time.ToLocalTime():yyyy-MM-dd HH:mm})"; // it waited for the phone: say when it really arrived
                if (await Sip.SendTextAsync(target, sms.Number, text).ConfigureAwait(false))
                {
                    Db.RemoveSmsPending(pid);
                    Db.SetSmsStatus(sms.Id, "delivered");
                }
            }
        }
        catch { }
        finally { _smsDelivery.Release(); }
    }

    private static string CleanNumber(string n) =>
        new string(n.Trim().Where(c => char.IsDigit(c) || c == '+' || c == '*' || c == '#').ToArray());

    public async Task<bool> SendSmsAsync(string number, string text)
    {
        long id = Db.AddSms("out", number, text, "sending");
        bool ok = Modem is not null && await Modem.SendSmsAsync(number, text).ConfigureAwait(false);
        Db.SetSmsStatus(id, ok ? "sent" : "failed");
        return ok;
    }

    public void Dispose()
    {
        _smsRetry?.Dispose();
        Modem?.Dispose();
        Sip.Dispose();
        Clock.Dispose();
    }
}
