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
        Sip.Start(sipBind ?? IPAddress.Any, s.SipPort);

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
        Modem?.Start();

        Config.Changed += c =>
        {
            if (Modem is not null) { Modem.RxGainDb = c.Settings.GsmRxGain; Modem.TxGainDb = c.Settings.GsmTxGain; }
        };
    }

    private void OnSms(IncomingSms sms)
    {
        long id = Db.AddSms("in", sms.Sender, sms.Text, "received");
        var msg = Db.Sms(1).FirstOrDefault(m => m.Id == id);
        if (msg is not null) SmsReceived?.Invoke(msg);
    }

    public async Task<bool> SendSmsAsync(string number, string text)
    {
        long id = Db.AddSms("out", number, text, "sending");
        bool ok = Modem is not null && await Modem.SendSmsAsync(number, text).ConfigureAwait(false);
        Db.SetSmsStatus(id, ok ? "sent" : "failed");
        return ok;
    }

    public void Dispose()
    {
        Modem?.Dispose();
        Sip.Dispose();
        Clock.Dispose();
    }
}
