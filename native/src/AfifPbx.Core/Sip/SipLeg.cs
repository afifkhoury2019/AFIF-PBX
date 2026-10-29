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
    /// <summary>Reached through the built-in secure network: our audio may only go where the phone's audio came from.</summary>
    private bool _viaRelay;
    private long _rxPackets, _txFrames, _txErrors;
    private string _lastTxError = "";

    /// <summary>Diagnostics for every SIP leg (set by the SIP server).</summary>
    public static Microsoft.Extensions.Logging.ILogger Log { get; set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public override string Kind => "sip";
    public bool IsInbound => _uas is not null;
    public string CallId { get; private set; } = "";

    private SipLeg(SIPUserAgent ua, RTPSession rtp, SIPServerUserAgent? uas)
    {
        _ua = ua; _rtp = rtp; _uas = uas;
        _ua.OnCallHungup += _ => Close("hangup");
        _ua.OnDtmfTone += (tone, _) => OnDigit(ToneToChar(tone));
        _rtp.OnRtpPacketReceived += (from, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            int pt = pkt.Header.PayloadType;
            if (pt != 0 && pt != 8) return; // only G.711 carries voice; 101 = DTMF events
            if (Interlocked.Increment(ref _rxPackets) == 1) Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Log, "Leg {Leg}: first RTP from {Remote}", this, from?.ToString());
            Inbound.Push(G711.Decode(pkt.Payload, alaw: pt == 8));
        };
        _rtp.OnTimeout += _ => { if (State == LegState.Answered) Hangup("media timeout"); };
        _rtp.OnRtpClosed += reason => Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Log, "Leg {Leg}: media closed ({Reason})", this, reason ?? "");
    }

    /// <param name="bind">The local address that routes to the phone. Binding to it makes the address in the
    /// SDP and the source address of our RTP the same, which strict phones require (no one-way audio
    /// on PCs with several network adapters, e.g. Hyper-V/WSL/VPN).</param>
    /// <param name="announce">Address to put in our SDP instead of the bound one (the PBX's tailnet address when the
    /// phone comes through the built-in secure network, whose relay forwards that port to <paramref name="bind"/>).</param>
    internal static RTPSession CreateRtp(int portStart, int portEnd, IPAddress? bind, bool sdesSrtp = false, IPAddress? announce = null)
    {
        // Phones that insist on encrypted audio (Linphone / AFIF Phone offer RTP/SAVP + a=crypto) get SDES-SRTP;
        // everything else gets plain RTP.
        var rtp = new AnnouncedRtpSession(announce, new RtpSessionConfig
        {
            IsMediaMultiplexed = false,
            IsRtcpMultiplexed = false,
            RtpSecureMediaOption = sdesSrtp ? RtpSecureMediaOptionEnum.SdpCryptoNegotiation : RtpSecureMediaOptionEnum.None,
            BindAddress = bind,
            BindPort = 0,
            RtpPortRange = new PortRange(portStart, portEnd),
        });
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
    public static SipLeg FromInvite(SIPTransport transport, SIPRequest invite, int rtpStart, int rtpEnd, IPAddress? bind = null,
        IPAddress? announce = null, SIPEndPoint? outboundProxy = null)
    {
        // through the secure network, in-dialog requests (our BYE) must go back through the relay, not to the
        // phone's tailnet Contact address that this PC cannot reach directly
        var ua = new SIPUserAgent(transport, outboundProxy);
        var uas = ua.AcceptCall(invite);
        var leg = new SipLeg(ua, CreateRtp(rtpStart, rtpEnd, bind, OffersSrtp(invite.Body), announce), uas)
        {
            _viaRelay = announce is not null,
            Number = invite.Header.From.FromURI.User ?? "",
            Name = invite.Header.From.FromName ?? "",
            CallId = invite.Header.CallId,
        };
        ua.ServerCallCancelled += (_, _) => leg.Close("cancelled");
        return leg;
    }

    /// <summary>True when the caller's SDP offers only secure audio (RTP/SAVP with SDES keys).</summary>
    internal static bool OffersSrtp(string? sdp) =>
        !string.IsNullOrEmpty(sdp) && sdp.Contains("RTP/SAVP") && !sdp.Contains("RTP/SAVPF") && sdp.Contains("a=crypto:");

    /// <summary>Start ringing a phone. Await <see cref="CallLeg.AnsweredTask"/> for the outcome.</summary>
    public static SipLeg Dial(SIPTransport transport, SIPEndPoint? sendTo, string contactUri,
        string callerNumber, string callerName, string pbxHost, int ringSeconds, int rtpStart, int rtpEnd,
        IPAddress? rtpBind = null, IPAddress? announce = null)
    {
        var ua = new SIPUserAgent(transport, sendTo);
        IPAddress? bind = rtpBind ?? (IPAddress.TryParse(pbxHost, out var b) ? b : null);
        var leg = new SipLeg(ua, CreateRtp(rtpStart, rtpEnd, bind, false, announce), null) { Number = contactUri, _viaRelay = announce is not null };
        ua.ClientCallAnswered += (_, _) => { leg._rtpReady = true; leg.MarkAnsweredPublic(); leg.Describe("answered"); };
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
            Describe("answered");
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
        // through the secure network the phone's address is only reachable via the relay; wait until the phone's
        // first packet has told us which relay socket that is (RTP latching)
        if (_viaRelay && Interlocked.Read(ref _rxPackets) == 0) return;
        lock (_sendLock)
        {
            try
            {
                var fmt = _rtp.AudioStream?.GetSendingFormat();
                bool alaw = fmt is not null && fmt.Value.ID == 8;
                _rtp.SendAudio((uint)Audio.FrameSamples, G711.Encode(frame, alaw));
            }
            catch (Exception ex)
            {
                _txErrors++;
                if (_lastTxError != ex.Message) { _lastTxError = ex.Message; Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(Log, "Leg {Leg}: RTP send failed: {Error}", this, ex.Message); }
                return;
            }
            _txFrames++;
        }
    }

    private void Describe(string what)
    {
        try
        {
            var ch = _rtp.AudioStream?.GetRTPChannel();
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Log, "Leg {Leg} {What}: local {Local} → remote {Remote}, codec {Codec}",
                this, what, ch?.RTPLocalEndPoint, _rtp.AudioDestinationEndPoint, _rtp.AudioStream?.GetSendingFormat().Name());
        }
        catch (Exception ex) { Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Log, "Leg {Leg} {What} ({Error})", this, what, ex.Message); }
    }

    private void Close(string reason)
    {
        if (IsEnded) return;
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Log, "Leg {Leg} ended ({Reason}): received {Rx} RTP packets, sent {Tx} frames, {Err} send errors",
            this, reason, Interlocked.Read(ref _rxPackets), _txFrames, _txErrors);
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

/// <summary>An RTP session that announces a different address in its SDP than the one it is bound to.</summary>
internal sealed class AnnouncedRtpSession : RTPSession
{
    private readonly IPAddress? _announce;

    public AnnouncedRtpSession(IPAddress? announce, RtpSessionConfig config) : base(config) => _announce = announce;

    public override SDP CreateOffer(IPAddress? connectionAddress = null) => Announce(base.CreateOffer(connectionAddress!));

    public override SDP CreateAnswer(IPAddress? connectionAddress) => Announce(base.CreateAnswer(connectionAddress!));

    private SDP Announce(SDP sdp)
    {
        if (_announce is null || sdp is null) return sdp!;
        string ip = _announce.ToString();
        if (sdp.Connection is not null) sdp.Connection.ConnectionAddress = ip;
        foreach (var m in sdp.Media)
            if (m.Connection is not null) m.Connection.ConnectionAddress = ip;
        return sdp;
    }
}
