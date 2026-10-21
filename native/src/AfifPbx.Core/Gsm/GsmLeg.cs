using AfifPbx.Calls;
using AfifPbx.Media;

namespace AfifPbx.Gsm;

/// <summary>A call on the GSM line (the SIM in the dongle).</summary>
public sealed class GsmLeg : CallLeg
{
    private readonly HuaweiModem _modem;
    private readonly DtmfDetector _dtmf = new();
    private readonly bool _inbound;

    public override string Kind => "gsm";

    private GsmLeg(HuaweiModem modem, bool inbound, string number)
    {
        _modem = modem;
        _inbound = inbound;
        Number = number;
        Name = string.IsNullOrEmpty(number) ? "Unknown" : number;
        _dtmf.Digit += OnDigit;
        _modem.AudioReceived += OnAudio;
        _modem.CallState += OnCallState;
    }

    /// <summary>Wrap an incoming GSM call (still ringing).</summary>
    public static GsmLeg Incoming(HuaweiModem modem, string number) => new(modem, true, number);

    /// <summary>Dial out on the GSM line. Await <see cref="CallLeg.AnsweredTask"/> for the outcome.</summary>
    public static GsmLeg Dial(HuaweiModem modem, string number)
    {
        var leg = new GsmLeg(modem, false, number);
        _ = Task.Run(async () =>
        {
            if (!await modem.DialAsync(number).ConfigureAwait(false)) leg.End("dial failed");
        });
        return leg;
    }

    private void OnAudio(short[] frame)
    {
        if (State != LegState.Answered) return;
        Inbound.Push(frame);
        _dtmf.Process(frame);
    }

    private void OnCallState(string state, string detail)
    {
        if (state == "answered" && !_inbound) MarkAnswered();
        else if (state == "ended") End(detail);
    }

    public override async Task<bool> AnswerAsync()
    {
        if (State == LegState.Answered) return true;
        if (State == LegState.Ended || !_inbound) return false;
        if (await _modem.AnswerAsync().ConfigureAwait(false)) { MarkAnswered(); return true; }
        End("answer failed");
        return false;
    }

    public override void SendRinging() { /* the network plays ringback to the GSM caller */ }

    public override void Hangup(string reason = "normal")
    {
        if (IsEnded) return;
        _ = _modem.HangupAsync();
        End(reason);
    }

    public override void SendDtmf(char digit) => _ = _modem.SendDtmfAsync(digit);

    public override void Write(short[] frame)
    {
        if (State == LegState.Answered) _modem.WriteAudio(frame);
    }

    private void End(string reason)
    {
        if (IsEnded) return;
        _modem.AudioReceived -= OnAudio;
        _modem.CallState -= OnCallState;
        MarkEnded(reason);
    }
}
