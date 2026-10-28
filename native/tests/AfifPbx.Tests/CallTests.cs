using System.Net;
using System.Net.Sockets;
using AfifPbx.Data;
using AfifPbx.Gsm;
using AfifPbx.Media;
using AfifPbx.Tts;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AfifPbx.Tests;

/// <summary>A software SIP phone used to exercise the PBX end to end.</summary>
internal sealed class TestPhone : IDisposable
{
    private readonly string _ext, _secret;
    private readonly int _pbxPort;
    private System.Threading.Timer? _toneTimer;
    private int _tonePos;
    private readonly short[] _tone;
    public SIPTransport Transport { get; } = new();
    public SIPUserAgent? Call { get; private set; }
    public RTPSession? Rtp { get; private set; }
    public bool AutoAnswer { get; set; }
    public int LoudFrames;
    public TaskCompletionSource<SIPRequest> IncomingInvite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SIPRegistrationUserAgent? _reg;
    private readonly SIPUserAgent _listener;

    public TestPhone(string ext, string secret, int pbxPort, double toneHz = 900)
    {
        _ext = ext; _secret = secret; _pbxPort = pbxPort;
        _tone = Tones.Sine(toneHz, 1000, 0.3);
        Transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Loopback, 0));
        _listener = new SIPUserAgent(Transport, null);
        _listener.OnIncomingCall += async (ua, req) =>
        {
            IncomingInvite.TrySetResult(req);
            if (!AutoAnswer) return;
            var uas = ua.AcceptCall(req);
            Rtp = MakeRtp();
            Call = ua;
            bool ok = await ua.Answer(uas, Rtp);
            if (ok) StartTone();
            Answered.TrySetResult(ok);
        };
    }

    private RTPSession MakeRtp()
    {
        var rtp = new RTPSession(false, false, false, IPAddress.Loopback);
        rtp.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.audio, false,
            new List<SDPAudioVideoMediaFormat> { new(SDPWellKnownMediaFormatsEnum.PCMU) }, MediaStreamStatusEnum.SendRecv));
        rtp.OnRtpPacketReceived += (_, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio || pkt.Header.PayloadType != 0) return;
            if (Audio.Rms(G711.Decode(pkt.Payload, false)) > 800) Interlocked.Increment(ref LoudFrames);
        };
        return rtp;
    }

    public async Task<bool> RegisterAsync(string? password = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reg = new SIPRegistrationUserAgent(Transport, _ext, password ?? _secret, $"127.0.0.1:{_pbxPort}", 120,
            maxRegistrationAttemptTimeout: 5, maxRegisterAttempts: 3);
        _reg.RegistrationSuccessful += (_, _) => tcs.TrySetResult(true);
        _reg.RegistrationFailed += (_, _, _) => tcs.TrySetResult(false);
        _reg.RegistrationTemporaryFailure += (_, _, _) => tcs.TrySetResult(false);
        _reg.Start();
        var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
        return done == tcs.Task && tcs.Task.Result;
    }

    public async Task<bool> DialAsync(string number, int timeoutSeconds = 20)
    {
        Call = new SIPUserAgent(Transport, null);
        Rtp = MakeRtp();
        bool ok = await Call.Call($"sip:{number}@127.0.0.1:{_pbxPort}", _ext, _secret, Rtp, timeoutSeconds);
        if (ok) StartTone();
        return ok;
    }

    private void StartTone()
    {
        _toneTimer = new System.Threading.Timer(_ =>
        {
            var frame = new short[Audio.FrameSamples];
            for (int i = 0; i < frame.Length; i++) frame[i] = _tone[(_tonePos + i) % _tone.Length];
            _tonePos += frame.Length;
            try { Rtp?.SendAudio((uint)Audio.FrameSamples, G711.Encode(frame, false)); } catch { }
        }, null, 0, 20);
    }

    public Task SendDtmf(byte tone) => Call?.SendDtmf(tone) ?? Task.CompletedTask;

    public void Hangup()
    {
        _toneTimer?.Dispose();
        try { Call?.Hangup(); } catch { }
    }

    public void Dispose()
    {
        Hangup();
        try { _reg?.Stop(false); } catch { }
        try { Rtp?.Close("done"); } catch { }
        Transport.Shutdown();
    }
}

/// <summary>Sends the PBX's own log lines to the test output, so a failing test shows what the engine did.</summary>
internal sealed class TestLogs : Microsoft.Extensions.Logging.ILoggerFactory
{
    private readonly ITestOutputHelper _out;
    public TestLogs(ITestOutputHelper output) => _out = output;
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string category) => new L(_out, category);
    public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class L : Microsoft.Extensions.Logging.ILogger
    {
        private readonly ITestOutputHelper _o; private readonly string _c;
        public L(ITestOutputHelper o, string c) { _o = o; _c = c; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level >= Microsoft.Extensions.Logging.LogLevel.Debug;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f)
        {
            try { _o.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {_c}: {f(state, ex)}{(ex is null ? "" : " — " + ex.Message)}"); } catch { }
        }
    }
}

public sealed class CallTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<IDisposable> _cleanup = new();

    public CallTests(ITestOutputHelper output) => _out = output;

    private static int FreePort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    private (PbxEngine engine, int port) StartEngine(FakeModem? modem = null, Action<PbxConfig>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "afif-test-" + Guid.NewGuid().ToString("N")[..8]);
        int port = FreePort();
        Func<(IModemPorts?, string, string)>? opener = modem is null ? null : () => (modem, "starting", "");
        var engine = new PbxEngine(dir, new TestLogs(_out), new FallbackSpeech(), opener);
        engine.Config.Update(c =>
        {
            c.Settings.SipPort = port;
            c.Settings.GsmEnabled = modem is not null;
            c.Settings.RingSeconds = 8;
            c.Settings.VoicemailMaxSeconds = 20;
            c.Extensions = new()
            {
                new Extension { Number = "100", Name = "Afif", Secret = "s100" },
                new Extension { Number = "101", Name = "Office", Secret = "s101" },
                new Extension { Number = "102", Name = "Store", Secret = "s102" },
            };
            c.RingGroups = new() { new RingGroup { Number = "600", Name = "All", Members = new() { "100", "101" }, RingSeconds = 8 } };
            c.Ivrs = new() { new Ivr { Number = "700", Name = "Menu", PromptText = "Press one", Options = new() { ["1"] = "ext:101" }, TimeoutSeconds = 6 } };
            c.Settings.ExtraSipPorts = new(); // tests use free ports only
            configure?.Invoke(c);
        });
        engine.Start(IPAddress.Loopback);
        _cleanup.Add(engine);
        if (modem is not null) _cleanup.Add(modem);
        return (engine, port);
    }

    private TestPhone Phone(string ext, int port, double hz = 900)
    {
        var p = new TestPhone(ext, "s" + ext, port, hz);
        _cleanup.Add(p);
        return p;
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(100);
        }
        return cond();
    }

    [Fact]
    public async Task Extensions_register_and_call_each_other_with_audio_both_ways()
    {
        var (engine, port) = StartEngine();
        var a = Phone("100", port, 900);
        var b = Phone("101", port, 1300);
        Assert.True(await a.RegisterAsync(), "100 should register");
        Assert.True(await b.RegisterAsync(), "101 should register");
        Assert.True(engine.Sip.IsRegistered("101"));
        b.AutoAnswer = true;

        Assert.True(await a.DialAsync("101"), "call should be answered");
        Assert.True(await Eventually(() => a.LoudFrames > 15 && b.LoudFrames > 15, 15000),
            $"audio should flow both ways (100 heard {a.LoudFrames}, 101 heard {b.LoudFrames})");
        Assert.Single(engine.Calls.ActiveCalls);

        a.Hangup();
        Assert.True(await Eventually(() => engine.Calls.ActiveCalls.Count == 0));
        var log = engine.Db.Calls();
        Assert.Contains(log, c => c.FromNumber == "100" && c.Disposition == "ANSWERED");
    }

    [Fact]
    public async Task Phone_on_the_extra_sip_port_is_called_from_that_port()
    {
        int extra = FreePort();
        var (engine, port) = StartEngine(configure: c => c.Settings.ExtraSipPorts = new() { extra });
        Assert.Equal(new[] { port, extra }, engine.Sip.Ports);
        var a = Phone("100", port, 900);
        var b = new TestPhone("101", "s101", extra, 1300); // Linphone set up for the Pi's port 5160
        _cleanup.Add(b);
        Assert.True(await a.RegisterAsync());
        Assert.True(await b.RegisterAsync(), "101 should register on the extra port");
        b.AutoAnswer = true;

        Assert.True(await a.DialAsync("101"), "call should be answered");
        var invite = await b.IncomingInvite.Task;
        Assert.Equal(extra, invite.RemoteSIPEndPoint.Port);
        Assert.True(await Eventually(() => a.LoudFrames > 15 && b.LoudFrames > 15, 15000),
            $"audio both ways (100 heard {a.LoudFrames}, 101 heard {b.LoudFrames})");
        a.Hangup();
    }

    [Fact]
    public async Task Wrong_password_cannot_register()
    {
        var (_, port) = StartEngine();
        var p = Phone("100", port);
        Assert.False(await p.RegisterAsync("wrong-password"));
    }

    [Fact]
    public async Task Unavailable_extension_goes_to_voicemail()
    {
        var (engine, port) = StartEngine();
        var a = Phone("100", port);
        Assert.True(await a.RegisterAsync());
        Assert.True(await a.DialAsync("102"), "voicemail should answer");
        await Task.Delay(6000); // greeting + beep + ~3 s of message
        a.Hangup();
        Assert.True(await Eventually(() => engine.Db.Voicemails("102").Count == 1, 5000), "a voicemail should be saved");
        var vm = engine.Db.Voicemails("102")[0];
        Assert.True(vm.DurationSeconds >= 1, $"duration {vm.DurationSeconds}");
        Assert.True(File.Exists(Path.Combine(engine.Calls.VoicemailDir, vm.File)));
    }

    [Fact]
    public async Task Ring_group_rings_every_member_and_connects_the_one_who_answers()
    {
        var (engine, port) = StartEngine();
        var caller = Phone("102", port);
        var m1 = Phone("100", port);
        var m2 = Phone("101", port, 1300);
        Assert.True(await caller.RegisterAsync());
        Assert.True(await m1.RegisterAsync());
        Assert.True(await m2.RegisterAsync());
        m2.AutoAnswer = true;

        Assert.True(await caller.DialAsync("600"));
        Assert.True(m1.IncomingInvite.Task.IsCompleted, "member 100 should have rung too");
        Assert.True(await Eventually(() => caller.LoudFrames > 15), "caller should hear the member who answered");
        caller.Hangup();
    }

    [Fact]
    public async Task Ivr_sends_the_caller_where_they_press()
    {
        var (_, port) = StartEngine();
        var a = Phone("100", port);
        var b = Phone("101", port);
        Assert.True(await a.RegisterAsync());
        Assert.True(await b.RegisterAsync());
        b.AutoAnswer = true;

        Assert.True(await a.DialAsync("700"), "IVR should answer");
        await Task.Delay(1500);
        await a.SendDtmf(1);
        var invite = await Task.WhenAny(b.IncomingInvite.Task, Task.Delay(8000));
        Assert.True(invite == b.IncomingInvite.Task, "pressing 1 should ring extension 101");
        Assert.True(await Eventually(() => a.LoudFrames > 10), "caller should be connected to 101");
        a.Hangup();
    }

    [Fact]
    public async Task Calling_an_outside_number_uses_the_gsm_line()
    {
        var modem = new FakeModem();
        var (engine, port) = StartEngine(modem);
        Assert.True(await Eventually(() => engine.Modem!.IsReady, 10000), $"modem should be ready: {engine.Modem!.Status.State} {engine.Modem.Status.Message}");
        Assert.Equal("Zain JO", engine.Modem.Status.Operator);

        var a = Phone("100", port);
        Assert.True(await a.RegisterAsync());
        Assert.True(await a.DialAsync("0791234567"), "GSM call should be answered");
        Assert.Equal("0791234567", modem.DialedNumber);
        Assert.True(modem.SawCommand("AT^DDSETEX=2"), "voice stream must be enabled");
        Assert.True(await Eventually(() => a.LoudFrames > 15 && Interlocked.Read(ref modem.AudioBytesFromPbx) > 3200),
            $"audio both ways (phone heard {a.LoudFrames} frames, dongle got {modem.AudioBytesFromPbx} bytes)");
        a.Hangup();
        Assert.True(await Eventually(() => modem.SawCommand("AT+CHUP")), "hanging up the phone must hang up the GSM call");
    }

    [Fact]
    public async Task Incoming_gsm_call_rings_the_inbound_destination()
    {
        var modem = new FakeModem();
        var (engine, port) = StartEngine(modem, c => c.Settings.GsmInboundDestination = "ext:100");
        Assert.True(await Eventually(() => engine.Modem!.IsReady, 10000));
        var a = Phone("100", port);
        Assert.True(await a.RegisterAsync());
        a.AutoAnswer = true;

        modem.Ring("0791112233");
        var got = await Task.WhenAny(a.IncomingInvite.Task, Task.Delay(8000));
        Assert.True(got == a.IncomingInvite.Task, "extension 100 should ring");
        Assert.Equal("0791112233", a.IncomingInvite.Task.Result.Header.From.FromURI.User);
        Assert.True(await Eventually(() => modem.SawCommand("ATA")), "the GSM call should be answered when the phone picks up");
        Assert.True(await Eventually(() => a.LoudFrames > 15), "phone should hear the GSM caller");
        modem.RemoteHangup();
        Assert.True(await Eventually(() => engine.Calls.ActiveCalls.Count == 0), "remote hang-up ends the call");
        Assert.Contains(engine.Db.Calls(), c => c.Direction == "inbound" && c.Disposition == "ANSWERED");
    }

    [Fact]
    public async Task Sms_are_received_and_sent()
    {
        var modem = new FakeModem();
        var (engine, _) = StartEngine(modem);
        Assert.True(await Eventually(() => engine.Modem!.IsReady, 10000));
        modem.DeliverSms(3, "00040C916972191122330008621070210000001406450631062D0628062700200041004600490046");
        Assert.True(await Eventually(() => engine.Db.Sms().Any(s => s.Text == "مرحبا AFIF" && s.Number == "+962791112233")), "SMS should be stored");
        Assert.True(await Eventually(() => modem.SawCommand("AT+CMGD=3")), "SMS should be deleted from the SIM");

        Assert.True(await engine.SendSmsAsync("0791234567", "Hello from AFIF-PBX"));
        Assert.Single(modem.SentSmsPdus);
        Assert.Contains(engine.Db.Sms(), s => s.Direction == "out" && s.Status == "sent");
    }

    [Fact]
    public async Task Calls_are_recorded_when_enabled()
    {
        var (engine, port) = StartEngine(configure: c => c.Extensions.First(e => e.Number == "100").RecordCalls = true);
        var a = Phone("100", port);
        var b = Phone("101", port, 1300);
        Assert.True(await a.RegisterAsync());
        Assert.True(await b.RegisterAsync());
        b.AutoAnswer = true;
        Assert.True(await a.DialAsync("101"));
        await Task.Delay(2500);
        a.Hangup();
        Assert.True(await Eventually(() => engine.Db.Calls().Any(c => !string.IsNullOrEmpty(c.Recording))));
        var rec = engine.Db.Calls().First(c => !string.IsNullOrEmpty(c.Recording)).Recording;
        var path = Path.Combine(engine.Calls.RecordingsDir, rec);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 8000 * 4, "recording should hold at least a second of stereo audio");
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_cleanup)) { try { d.Dispose(); } catch { } }
    }
}
