using System.Net;
using AfifPbx.Calls;
using AfifPbx.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace AfifPbx.Sip;

/// <summary>A call leg to or from a SIP phone, with G.711 RTP audio.</summary>
public sealed class SipLeg : CallLeg
{
    private readonly SIPUserAgent _ua;
    private readonly RTPSession _rtp;
    private readonly SIPServerUserAgent? _uas;
    private readonly object _sendLock = new();
    private bool _rtpReady;

    public override string Kind => "sip";
    public bool IsInbound => _uas is not null;
    public string CallId { get; private set; } = "";

    private SipLeg(SIPUserAgent ua, RTPSession rtp, SIPServerUserAgent? uas)
    {
        _ua = ua; _rtp = rtp; _uas = uas;
        _ua.OnCallHungup += _ => Close("hangup");
        _ua.OnDtmfTone += (tone, _) => OnDigit(ToneToChar(tone));
        _rtp.OnRtpPacketReceived += (_, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            int pt = pkt.Header.PayloadType;
            if (pt != 0 && pt != 8) return; // only G.711 carries voice; 101 = DTMF events
            Inbound.Push(G711.Decode(pkt.Payload, alaw: pt == 8));
        };
        _rtp.OnTimeout += _ => { if (State == LegState.Answered) Hangup("media timeout"); };
    }

    /// <param name="bind">The local address that routes to the phone. Binding to it makes the address in the
    /// SDP and the source address of our RTP the same, which strict phones require (no one-way audio
    /// on PCs with several network adapters, e.g. Hyper-V/WSL/VPN).</param>
    internal static RTPSession CreateRtp(int portStart, int portEnd, IPAddress? bind)
    {
        var rtp = new RTPSession(false, false, false, bind, 0, new PortRange(portStart, portEnd));
        var formats = new List<SDPAudioVideoMediaFormat>
        {
            new(SDPWellKnownMediaFormatsEnum.PCMU),
            new(SDPWellKnownMediaFormatsEnum.PCMA),
        };
        rtp.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.audio, false, formats, MediaStreamStatusEnum.SendRecv));
        rtp.AcceptRtpFromAny = true; // phones behind simple NATs / multiple interfaces
        return rtp;
    }

    /// <summary>Wrap an incoming INVITE. The caller is not answered until <see cref="AnswerAsync"/>.</summary>
    public static SipLeg FromInvite(SIPTransport transport, SIPRequest invite, int rtpStart, int rtpEnd, IPAddress? bind = null)
    {
        var ua = new SIPUserAgent(transport, null);
        var uas = ua.AcceptCall(invite);
        var leg = new SipLeg(ua, CreateRtp(rtpStart, rtpEnd, bind), uas)
        {
            Number = invite.Header.From.FromURI.User ?? "",
            Name = invite.Header.From.FromName ?? "",
            CallId = invite.Header.CallId,
        };
        ua.ServerCallCancelled += (_, _) => leg.Close("cancelled");
        return leg;
    }

    /// <summary>Start ringing a phone. Await <see cref="CallLeg.AnsweredTask"/> for the outcome.</summary>
    public static SipLeg Dial(SIPTransport transport, SIPEndPoint? sendTo, string contactUri,
        string callerNumber, string callerName, string pbxHost, int ringSeconds, int rtpStart, int rtpEnd)
    {
        var ua = new SIPUserAgent(transport, sendTo);
        IPAddress? bind = IPAddress.TryParse(pbxHost, out var b) ? b : null;
        var leg = new SipLeg(ua, CreateRtp(rtpStart, rtpEnd, bind), null) { Number = contactUri };
        ua.ClientCallAnswered += (_, _) => { leg._rtpReady = true; leg.MarkAnsweredPublic(); };
        ua.ClientCallFailed += (_, err, resp) => leg.Close(resp is null ? err : $"{(int)resp.Status} {resp.ReasonPhrase}");

        string display = string.IsNullOrWhiteSpace(callerName) ? callerNumber : callerName;
        string from = $"\"{display.Replace("\"", "")}\" <sip:{callerNumber}@{pbxHost}>";
        var desc = new SIPCallDescriptor(SIPConstants.SIP_DEFAULT_USERNAME, null, contactUri, from, contactUri,
            null, null, null, SIPCallDirection.Out, SDP.SDP_MIME_CONTENTTYPE, null, null);
        _ = Task.Run(async () =>
        {
            try { await ua.InitiateCallAsync(desc, leg._rtp, ringSeconds).ConfigureAwait(false); }
            catch (Exception ex) { leg.Close("dial error: " + ex.Message); }
        });
        return leg;
    }

    private void MarkAnsweredPublic() => MarkAnswered();

    public override void SendRinging()
    {
        if (_uas is null || State != LegState.Ringing) return;
        try { _uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null); } catch { }
    }

    public override async Task<bool> AnswerAsync()
    {
        if (State == LegState.Answered) return true;
        if (State == LegState.Ended || _uas is null) return false;
        bool ok;
        try { ok = await _ua.Answer(_uas, _rtp).ConfigureAwait(false); }
        catch { ok = false; }
        if (ok)
        {
            _rtpReady = true;
            MarkAnswered();
        }
        else Close("answer failed");
        return ok;
    }

    /// <summary>Stop ringing an outbound leg that was not answered (e.g. another phone in the group picked up).</summary>
    public void Cancel()
    {
        if (State != LegState.Ringing || _uas is not null) return;
        try { _ua.Cancel(); } catch { }
        Close("cancelled");
    }

    public override void Hangup(string reason = "normal")
    {
        if (State == LegState.Ended) return;
        try
        {
            if (State == LegState.Answered) _ua.Hangup();
            else if (_uas is not null)
            {
                var code = reason switch
                {
                    "busy" => SIPResponseStatusCodesEnum.BusyHere,
                    "notfound" => SIPResponseStatusCodesEnum.NotFound,
                    "unavailable" => SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                    "forbidden" => SIPResponseStatusCodesEnum.Forbidden,
                    _ => SIPResponseStatusCodesEnum.Decline,
                };
                _uas.Reject(code, null);
            }
            else _ua.Cancel();
        }
        catch { }
        Close(reason);
    }

    public override void SendDtmf(char digit)
    {
        if (State != LegState.Answered) return;
        byte? tone = CharToTone(digit);
        if (tone is null) return;
        _ = _ua.SendDtmf(tone.Value);
    }

    public override void Write(short[] frame)
    {
        if (!_rtpReady || State != LegState.Answered) return;
        lock (_sendLock)
        {
            try
            {
                var fmt = _rtp.AudioStream?.GetSendingFormat();
                bool alaw = fmt is not null && fmt.Value.ID == 8;
                _rtp.SendAudio((uint)Audio.FrameSamples, G711.Encode(frame, alaw));
            }
            catch { }
        }
    }

    private void Close(string reason)
    {
        if (IsEnded) return;
        MarkEnded(reason);
        try { _rtp.Close(reason); } catch { }
        try { _ua.Close(); } catch { }
    }

    private static char ToneToChar(byte tone) => tone switch
    {
        <= 9 => (char)('0' + tone),
        10 => '*',
        11 => '#',
        12 => 'A', 13 => 'B', 14 => 'C', 15 => 'D',
        _ => '?',
    };

    private static byte? CharToTone(char c) => c switch
    {
        >= '0' and <= '9' => (byte)(c - '0'),
        '*' => 10, '#' => 11,
        'A' or 'a' => 12, 'B' or 'b' => 13, 'C' or 'c' => 14, 'D' or 'd' => 15,
        _ => null,
    };
}
