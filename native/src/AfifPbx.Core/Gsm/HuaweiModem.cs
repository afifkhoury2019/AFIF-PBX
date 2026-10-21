using System.Text.RegularExpressions;
using AfifPbx.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AfifPbx.Gsm;

public sealed class ModemStatus
{
    /// <summary>no-device, no-driver, port-busy, starting, ready, no-sim, no-voice, error, disabled</summary>
    public string State { get; set; } = "starting";
    public string Message { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string Imei { get; set; } = "";
    public string Imsi { get; set; } = "";
    public string Operator { get; set; } = "";
    public string OwnNumber { get; set; } = "";
    public int Rssi { get; set; } = 99;
    public bool Registered { get; set; }
    public string Registration { get; set; } = "";
    public bool? VoiceEnabled { get; set; }
    public string AtPort { get; set; } = "";
    public string AudioPort { get; set; } = "";
    public string CallState { get; set; } = "idle";
    /// <summary>Signal as a 0–100 % figure (from RSSI 0–31).</summary>
    public int SignalPercent => Rssi is >= 0 and <= 31 ? Rssi * 100 / 31 : 0;
}

/// <summary>
/// Driver for Huawei voice-capable dongles (E303, E1550, E173, E1750 ...) using the two COM
/// ports the Huawei Windows driver creates: "PC UI Interface" (AT commands) and
/// "Application Interface" (8 kHz 16-bit PCM voice), the same way chan_dongle drives them on Linux.
/// </summary>
public sealed class HuaweiModem : IDisposable
{
    private readonly Func<(IModemPorts? ports, string state, string message)> _open;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, List<IncomingSms>> _multipart = new();
    private IModemPorts? _ports;
    private AtChannel? _at;
    private Thread? _audioThread;
    private string _callState = "idle";
    private string? _clip;
    private bool _announced;
    private Task? _supervisor;
    private readonly object _audioLock = new();

    public ModemStatus Status { get; private set; } = new();
    public int RxGainDb { get; set; }
    public int TxGainDb { get; set; }

    public event Action<string>? IncomingCall;            // caller number
    public event Action<string, string>? CallState;      // state (dialing/alerting/answered/ended), detail
    public event Action<short[]>? AudioReceived;          // 160-sample frames from the network
    public event Action<IncomingSms>? SmsReceived;
    public event Action<string>? UssdReceived;
    public event Action<ModemStatus>? StatusChanged;

    public bool IsReady => Status.State == "ready" && _at is not null;
    public bool InCall => _callState != "idle";

    /// <param name="open">Opens the ports; returns null ports with a state/message when unavailable.</param>
    public HuaweiModem(Func<(IModemPorts? ports, string state, string message)> open, ILogger? log = null)
    {
        _open = open;
        _log = log ?? NullLogger.Instance;
    }

    public void Start() => _supervisor = Task.Run(() => SuperviseAsync(_cts.Token));

    private void SetStatus(Action<ModemStatus> change)
    {
        change(Status);
        StatusChanged?.Invoke(Status);
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_at is null)
            {
                try { await ConnectAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _log.LogWarning("Modem start failed: {Message}", ex.Message);
                    SetStatus(s => { s.State = "error"; s.Message = ex.Message; });
                    CloseLink();
                }
            }
            else
            {
                await PollAsync().ConfigureAwait(false);
            }
            try { await Task.Delay(_at is null ? 8000 : 20000, ct).ConfigureAwait(false); } catch { }
        }
    }

    private async Task ConnectAsync()
    {
        var (ports, state, message) = _open();
        if (ports is null)
        {
            SetStatus(s => { s.State = state; s.Message = message; });
            return;
        }
        _ports = ports;
        _at = new AtChannel(ports.At, _log);
        _at.Unsolicited += OnUnsolicited;
        _at.Failed += _ => CloseLink();
        SetStatus(s => { s.State = "starting"; s.Message = "Initialising the dongle"; s.AtPort = ports.AtName; s.AudioPort = ports.AudioName; });

        if (ports.Audio is not null)
        {
            _audioThread = new Thread(() => AudioLoop(ports.Audio)) { IsBackground = true, Name = "afif-gsm-audio" };
            _audioThread.Start();
        }

        var at = _at;
        if (!(await at.SendAsync("AT", 3000).ConfigureAwait(false)).Ok)
        {
            // first command after open is sometimes eaten
            if (!(await at.SendAsync("AT", 3000).ConfigureAwait(false)).Ok) throw new IOException("The dongle does not answer AT commands");
        }
        await at.SendAsync("ATZ").ConfigureAwait(false);
        await at.SendAsync("ATE0").ConfigureAwait(false);
        await at.SendAsync("AT+CMEE=0").ConfigureAwait(false);

        string Info(AtResult r) => r.Lines.FirstOrDefault(l => !l.StartsWith("AT"))?.Replace("+CGMI:", "").Replace("+CGMM:", "").Replace("+CGMR:", "").Trim() ?? "";
        var manu = Info(await at.SendAsync("AT+CGMI").ConfigureAwait(false));
        var model = Info(await at.SendAsync("AT+CGMM").ConfigureAwait(false));
        var fw = Info(await at.SendAsync("AT+CGMR").ConfigureAwait(false));
        var imei = Info(await at.SendAsync("AT+CGSN").ConfigureAwait(false));
        SetStatus(s => { s.Manufacturer = manu; s.Model = model; s.Firmware = fw; s.Imei = imei; });

        var cpin = await at.SendAsync("AT+CPIN?").ConfigureAwait(false);
        string pin = cpin.Line("+CPIN:")?.Substring(6).Trim() ?? cpin.Final;
        if (!pin.Contains("READY"))
        {
            SetStatus(s => { s.State = "no-sim"; s.Message = pin.Contains("SIM PIN") ? "The SIM card asks for a PIN. Disable the PIN (put the SIM in a phone and turn off SIM lock)." : $"SIM not ready ({pin})"; });
            return; // keep polling; the user may insert/unlock the SIM
        }
        var imsi = Info(await at.SendAsync("AT+CIMI").ConfigureAwait(false));
        SetStatus(s => s.Imsi = imsi);

        // voice support: ^CVOICE:0,... means voice is enabled in the firmware
        var cv = await at.SendAsync("AT^CVOICE?").ConfigureAwait(false);
        var cvLine = cv.Line("^CVOICE:");
        bool? voice = cvLine is null ? null : cvLine.Substring(8).Trim().StartsWith("0");
        SetStatus(s => s.VoiceEnabled = voice);

        await at.SendAsync("AT+COPS=3,0").ConfigureAwait(false);
        await at.SendAsync("AT+CREG=2").ConfigureAwait(false);
        await at.SendAsync("AT+CLIP=1").ConfigureAwait(false);
        await at.SendAsync("AT+CSSN=1,1").ConfigureAwait(false);
        await at.SendAsync("AT+CMGF=0").ConfigureAwait(false);
        await at.SendAsync("AT+CPMS=\"SM\",\"SM\",\"SM\"").ConfigureAwait(false);
        await at.SendAsync("AT+CNMI=2,1,0,2,0").ConfigureAwait(false);
        var cnum = await at.SendAsync("AT+CNUM").ConfigureAwait(false);
        var m = Regex.Match(cnum.Line("+CNUM:") ?? "", "\"(\\+?\\d+)\"");
        if (m.Success) SetStatus(s => s.OwnNumber = m.Groups[1].Value);

        SetStatus(s =>
        {
            s.State = voice == false ? "no-voice" : "ready";
            s.Message = voice == false
                ? "This dongle's firmware has voice disabled — calls are not possible (SMS works)."
                : "Ready";
        });
        await PollAsync().ConfigureAwait(false);
        await ReadStoredSmsAsync().ConfigureAwait(false);
        _log.LogInformation("GSM dongle ready: {Manu} {Model} on {At}/{Audio}", manu, model, ports.AtName, ports.AudioName);
    }

    private async Task PollAsync()
    {
        var at = _at;
        if (at is null) return;
        if (Status.State == "no-sim") { CloseLink(); return; } // reconnect and re-check the SIM
        var csq = await at.SendAsync("AT+CSQ").ConfigureAwait(false);
        if (csq.Final is "TIMEOUT" or "PORT CLOSED" or "CLOSED")
        {
            CloseLink();
            return;
        }
        var mc = Regex.Match(csq.Line("+CSQ:") ?? "", @"\+CSQ:\s*(\d+)");
        if (mc.Success) SetStatus(s => s.Rssi = int.Parse(mc.Groups[1].Value));
        var creg = await at.SendAsync("AT+CREG?").ConfigureAwait(false);
        ParseCreg(creg.Line("+CREG:"));
        var cops = await at.SendAsync("AT+COPS?").ConfigureAwait(false);
        var mo = Regex.Match(cops.Line("+COPS:") ?? "", "\"([^\"]*)\"");
        if (mo.Success) SetStatus(s => s.Operator = mo.Groups[1].Value);
    }

    private void ParseCreg(string? line)
    {
        if (line is null) return;
        var parts = line.Substring(6).Split(',', StringSplitOptions.TrimEntries);
        // query form: +CREG: n,stat[,lac,ci]; unsolicited: +CREG: stat[,lac,ci]
        string stat = parts.Length >= 2 && parts[1].Length == 1 ? parts[1] : parts[0];
        string text = stat switch
        {
            "1" => "Registered (home)", "5" => "Registered (roaming)", "2" => "Searching", "3" => "Registration denied",
            "0" => "Not registered", _ => "Unknown",
        };
        SetStatus(s => { s.Registered = stat is "1" or "5"; s.Registration = text; });
    }

    // ------------------------------------------------------------------ unsolicited

    private void OnUnsolicited(string line)
    {
        try
        {
            if (line == "RING" || line.StartsWith("+CRING"))
            {
                if (_callState == "idle")
                {
                    SetCall("incoming");
                    _announced = false;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(900).ConfigureAwait(false);
                        AnnounceIncoming();
                    });
                }
            }
            else if (line.StartsWith("+CLIP:"))
            {
                var m = Regex.Match(line, "\"([^\"]*)\"");
                _clip = m.Success ? m.Groups[1].Value : "";
                if (_callState == "incoming") AnnounceIncoming();
            }
            else if (line.StartsWith("^ORIG:")) SetCall("dialing");
            else if (line.StartsWith("^CONF:")) { SetCall("alerting"); CallState?.Invoke("alerting", ""); }
            else if (line.StartsWith("^CONN:")) { SetCall("active"); CallState?.Invoke("answered", ""); }
            else if (line.StartsWith("^CEND:"))
            {
                // ^CEND:idx,duration,end_status,cc_cause
                var p = line.Substring(6).Split(',');
                string cause = p.Length >= 4 ? CauseText(p[3].Trim()) : "ended";
                EndCall(cause);
            }
            else if (line is "NO CARRIER" or "BUSY" or "NO ANSWER")
            {
                EndCall(line.ToLowerInvariant());
            }
            else if (line.StartsWith("^RSSI:"))
            {
                if (int.TryParse(line.Substring(6).Trim(), out var r)) SetStatus(s => s.Rssi = r);
            }
            else if (line.StartsWith("+CREG:")) ParseCreg(line);
            else if (line.StartsWith("+CMTI:"))
            {
                var m = Regex.Match(line, @",\s*(\d+)");
                if (m.Success) _ = ReadSmsAsync(int.Parse(m.Groups[1].Value));
            }
            else if (line.StartsWith("+CUSD:"))
            {
                UssdReceived?.Invoke(DecodeUssd(line));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Error handling modem line {Line}", line); }
    }

    private void AnnounceIncoming()
    {
        if (_announced || _callState != "incoming") return;
        _announced = true;
        string number = _clip ?? "";
        _clip = null;
        _log.LogInformation("Incoming GSM call from {Number}", number);
        IncomingCall?.Invoke(number);
    }

    private void SetCall(string state)
    {
        _callState = state;
        SetStatus(s => s.CallState = state);
    }

    private void EndCall(string cause)
    {
        if (_callState == "idle") return;
        SetCall("idle");
        _announced = false;
        CallState?.Invoke("ended", cause);
    }

    private static string CauseText(string code) => code switch
    {
        "16" => "normal",
        "17" => "busy",
        "18" or "19" => "no answer",
        "21" => "rejected",
        "1" or "28" => "invalid number",
        "34" or "38" or "41" or "42" => "network congestion",
        "" => "ended",
        _ => $"cause {code}",
    };

    // ------------------------------------------------------------------ calls

    public async Task<bool> DialAsync(string number)
    {
        var at = _at;
        if (at is null || !IsReady || InCall) return false;
        SetCall("dialing");
        var r = await at.SendAsync($"ATD{number};", 15000).ConfigureAwait(false);
        if (!r.Ok)
        {
            SetCall("idle");
            _log.LogWarning("Dial {Number} failed: {Final}", number, r.Final);
            return false;
        }
        await at.SendAsync("AT^DDSETEX=2").ConfigureAwait(false);
        return true;
    }

    public async Task<bool> AnswerAsync()
    {
        var at = _at;
        if (at is null || _callState != "incoming") return false;
        var r = await at.SendAsync("ATA", 10000).ConfigureAwait(false);
        if (!r.Ok) return false;
        await at.SendAsync("AT^DDSETEX=2").ConfigureAwait(false);
        SetCall("active");
        return true;
    }

    public async Task HangupAsync()
    {
        var at = _at;
        if (at is null) return;
        if (_callState == "idle") return;
        await at.SendAsync("AT+CHUP").ConfigureAwait(false);
        EndCall("local hangup");
    }

    public Task SendDtmfAsync(char digit) =>
        _at?.SendAsync($"AT^DTMF=1,{digit}") ?? Task.FromResult(new AtResult(false, "", Array.Empty<string>()));

    /// <summary>Send a 20 ms frame of PCM to the network side of the active call.</summary>
    public void WriteAudio(short[] frame)
    {
        var audio = _ports?.Audio;
        if (audio is null || _callState != "active") return;
        var bytes = new byte[Audio.FrameBytes];
        double g = TxGainDb == 0 ? 1 : Math.Pow(10, TxGainDb / 20.0);
        for (int i = 0; i < Audio.FrameSamples; i++)
        {
            int v = g == 1 ? frame[i] : (int)Math.Clamp(frame[i] * g, short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)v; bytes[2 * i + 1] = (byte)(v >> 8);
        }
        lock (_audioLock)
        {
            try { audio.Write(bytes, 0, bytes.Length); } catch { }
        }
    }

    private void AudioLoop(Stream audio)
    {
        var buf = new byte[4096];
        var pending = new List<byte>(8192);
        try
        {
            while (!_cts.IsCancellationRequested && _ports?.Audio == audio)
            {
                int n = audio.Read(buf, 0, buf.Length);
                if (n <= 0) { Thread.Sleep(5); continue; }
                if (_callState != "active") { pending.Clear(); continue; }
                pending.AddRange(buf.AsSpan(0, n).ToArray());
                while (pending.Count >= Audio.FrameBytes)
                {
                    var frame = new short[Audio.FrameSamples];
                    double g = RxGainDb == 0 ? 1 : Math.Pow(10, RxGainDb / 20.0);
                    for (int i = 0; i < Audio.FrameSamples; i++)
                    {
                        short v = (short)(pending[2 * i] | (pending[2 * i + 1] << 8));
                        frame[i] = g == 1 ? v : (short)Math.Clamp(v * g, short.MinValue, short.MaxValue);
                    }
                    pending.RemoveRange(0, Audio.FrameBytes);
                    AudioReceived?.Invoke(frame);
                }
            }
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            _log.LogWarning("Modem audio port closed: {Message}", ex.Message);
        }
        catch { }
    }

    // ------------------------------------------------------------------ SMS

    public async Task<bool> SendSmsAsync(string number, string text)
    {
        var at = _at;
        if (at is null || Status.State is not ("ready" or "no-voice")) return false;
        foreach (var (pdu, len) in SmsPdu.EncodeSubmit(number, text))
        {
            var r = await at.SendWithPromptAsync($"AT+CMGS={len}", pdu).ConfigureAwait(false);
            if (!r.Ok)
            {
                _log.LogWarning("SMS to {Number} failed: {Final}", number, r.Final);
                return false;
            }
        }
        return true;
    }

    public async Task<string?> SendUssdAsync(string code)
    {
        var at = _at;
        if (at is null) return null;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string s) => tcs.TrySetResult(s);
        UssdReceived += Handler;
        try
        {
            var r = await at.SendAsync($"AT+CUSD=1,\"{code}\",15", 10000).ConfigureAwait(false);
            if (!r.Ok) return null;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(20000)).ConfigureAwait(false);
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        finally { UssdReceived -= Handler; }
    }

    private static string DecodeUssd(string line)
    {
        var m = Regex.Match(line, "\\+CUSD:\\s*\\d+\\s*,\\s*\"([^\"]*)\"\\s*(?:,\\s*(\\d+))?");
        if (!m.Success) return line;
        string body = m.Groups[1].Value;
        int dcs = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 15;
        try
        {
            if (dcs == 72 && body.Length % 4 == 0 && body.All(Uri.IsHexDigit))
                return System.Text.Encoding.BigEndianUnicode.GetString(Convert.FromHexString(body));
        }
        catch { }
        return body;
    }

    private async Task ReadSmsAsync(int index)
    {
        var at = _at;
        if (at is null) return;
        var r = await at.SendAsync($"AT+CMGR={index}").ConfigureAwait(false);
        var pdu = r.Lines.FirstOrDefault(l => !l.StartsWith("+CMGR"));
        if (pdu is not null) HandlePdu(pdu);
        await at.SendAsync($"AT+CMGD={index}").ConfigureAwait(false);
    }

    private async Task ReadStoredSmsAsync()
    {
        var at = _at;
        if (at is null) return;
        var r = await at.SendAsync("AT+CMGL=4", 15000).ConfigureAwait(false);
        var indexes = new List<int>();
        for (int i = 0; i < r.Lines.Count; i++)
        {
            var m = Regex.Match(r.Lines[i], @"^\+CMGL:\s*(\d+)");
            if (!m.Success) continue;
            indexes.Add(int.Parse(m.Groups[1].Value));
            if (i + 1 < r.Lines.Count && !r.Lines[i + 1].StartsWith("+CMGL")) HandlePdu(r.Lines[i + 1]);
        }
        foreach (var idx in indexes) await at.SendAsync($"AT+CMGD={idx}").ConfigureAwait(false);
    }

    private void HandlePdu(string pdu)
    {
        IncomingSms sms;
        try { sms = SmsPdu.DecodeDeliver(pdu); }
        catch (Exception ex) { _log.LogWarning("Could not decode SMS PDU: {Message}", ex.Message); return; }

        if (sms.ConcatRef is null || sms.ConcatTotal <= 1)
        {
            SmsReceived?.Invoke(sms);
            return;
        }
        string key = $"{sms.Sender}/{sms.ConcatRef}";
        List<IncomingSms> parts;
        lock (_multipart)
        {
            if (!_multipart.TryGetValue(key, out parts!)) _multipart[key] = parts = new List<IncomingSms>();
            if (parts.All(p => p.ConcatSeq != sms.ConcatSeq)) parts.Add(sms);
            if (parts.Count < sms.ConcatTotal) return;
            _multipart.Remove(key);
        }
        var text = string.Concat(parts.OrderBy(p => p.ConcatSeq).Select(p => p.Text));
        SmsReceived?.Invoke(sms with { Text = text, ConcatRef = null, ConcatTotal = 1, ConcatSeq = 1 });
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Close the ports so the supervisor reconnects (e.g. after the dongle was unplugged).</summary>
    public void Reconnect() => CloseLink();

    private void CloseLink()
    {
        var at = _at; var ports = _ports;
        _at = null; _ports = null;
        if (_callState != "idle") EndCall("dongle disconnected");
        try { at?.Dispose(); } catch { }
        try { ports?.Dispose(); } catch { }
        if (Status.State is "ready" or "starting" or "no-voice")
            SetStatus(s => { s.State = "no-device"; s.Message = "Dongle disconnected — reconnecting"; });
    }

    public void Dispose()
    {
        _cts.Cancel();
        CloseLink();
    }
}
