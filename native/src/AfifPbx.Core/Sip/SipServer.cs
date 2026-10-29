using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AfifPbx.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace AfifPbx.Sip;

/// <summary>Where a phone registered from.</summary>
public sealed record SipBinding(string Extension, string ContactUri, SIPEndPoint RemoteEndPoint, DateTime ExpiresUtc, string UserAgent,
    SIPTransport Transport, int LocalPort)
{
    public bool IsExpired => DateTime.UtcNow >= ExpiresUtc;
}

internal sealed class PbxSipAccount : ISIPAccount
{
    public PbxSipAccount(Extension ext, string realm)
    {
        ID = ext.Number; SIPUsername = ext.Number; SIPPassword = ext.Secret; SIPDomain = realm; IsDisabled = !ext.Enabled;
    }
    public string ID { get; }
    public string SIPUsername { get; }
    public string SIPPassword { get; }
    public string HA1Digest => null!;
    public string SIPDomain { get; }
    public bool IsDisabled { get; }
}

/// <summary>
/// The AFIF-PBX SIP server: registrar with digest authentication for extensions, and the entry
/// point for calls placed by phones.
/// </summary>
public sealed class SipServer : IDisposable
{
    private readonly ConfigStore _config;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, List<SipBinding>> _bindings = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(SIPTransport Transport, int Port)> _transports = new();
    /// <summary>Transports that receive phones through the built-in secure network relay: transport → its tailnet port.</summary>
    private readonly ConcurrentDictionary<SIPTransport, int> _relayPorts = new();
    private readonly List<(int Public, int Local, SIPTransport Transport)> _relays = new();
    /// <summary>INVITEs already being handled, by transaction key — a phone resends its INVITE when the answer is slow
    /// (common over 4G/VPN), and each copy must not become a second call.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _seenInvites = new();

    /// <summary>The transport of the main SIP port.</summary>
    public SIPTransport Transport => _transports.Count > 0 ? _transports[0].Transport : throw new InvalidOperationException("SIP server not started");
    public int Port { get; private set; }
    /// <summary>Every port the PBX is listening on (main port first).</summary>
    public IReadOnlyList<int> Ports => _transports.Select(t => t.Port).ToList();
    public IPAddress BindAddress { get; private set; } = IPAddress.Any;

    /// <summary>The PBX's address on the built-in secure network (tailnet IPv4), or null while it is off.</summary>
    public IPAddress? TailnetAddress { get; set; }

    /// <summary>"tailnet port:local port" pairs for the secure network relay, e.g. "5060:53122,5160:53123".</summary>
    public string RelaySipMap => string.Join(",", _relays.Select(r => $"{r.Public}:{r.Local}"));

    /// <summary>
    /// A phone sent a text (SIP MESSAGE) to an outside number: (extension, number, text) → true once it went out on the SIM.
    /// </summary>
    public Func<string, string, string, Task<bool>>? TextFromPhone { get; set; }

    /// <summary>Final answers to MESSAGEs already handled (a phone resends while the SMS is still being sent).</summary>
    private readonly ConcurrentDictionary<string, (DateTime At, int Code)> _seenMessages = new();

    /// <summary>A phone placed a call: (leg from the phone, dialled number).</summary>
    public event Action<SipLeg, string>? IncomingCall;
    public event Action<string, bool>? RegistrationChanged;

    public SipServer(ConfigStore config, ILogger? log = null)
    {
        _config = config;
        _log = log ?? NullLogger.Instance;
        SipLeg.Log = _log;
    }

    /// <summary>
    /// Listen on the main port and any extra ports (e.g. 5160, the port FreePBX's chan_sip used, so phones
    /// configured for the old Raspberry Pi keep working). Each port gets its own transport, so a phone is always
    /// called from the same port it registered on.
    /// </summary>
    public void Start(IPAddress bind, int port, IEnumerable<int>? extraPorts = null)
    {
        BindAddress = bind; Port = port;
        foreach (var p in new[] { port }.Concat(extraPorts ?? Array.Empty<int>()).Where(p => p is > 0 and < 65536).Distinct())
        {
            var t = new SIPTransport();
            try { t.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(bind, p))); }
            catch (Exception ex)
            {
                t.Shutdown();
                if (p == port) throw;
                _log.LogWarning("Extra SIP port {Port} is not available: {Message}", p, ex.Message);
                continue;
            }
            try { t.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(bind, p))); }
            catch (Exception ex) { _log.LogWarning("SIP over TCP on port {Port} not available: {Message}", p, ex.Message); }
            var transport = t;
            t.SIPTransportRequestReceived += (local, remote, req) => OnRequest(transport, local, remote, req);
            Trace(t);
            _transports.Add((t, p));
            _log.LogInformation("SIP server listening on {Address}:{Port}", bind, p);
        }
    }

    /// <summary>Logs the call-setup messages that explain dropped calls: offer/answer SDP and who sent BYE (with its Reason).</summary>
    private void Trace(SIPTransport t)
    {
        static string Sdp(string? body)
        {
            if (string.IsNullOrEmpty(body)) return "(no SDP)";
            var keep = body.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("c=") || l.StartsWith("m=") || l.StartsWith("a=rtpmap") || l.StartsWith("a=crypto") ||
                            l.StartsWith("a=sendrecv") || l.StartsWith("a=sendonly") || l.StartsWith("a=recvonly") || l.StartsWith("a=inactive") ||
                            l.StartsWith("a=rtcp-mux") || l.StartsWith("a=ice") || l.StartsWith("a=zrtp") || l.StartsWith("a=encryption"));
            return string.Join(" | ", keep);
        }
        void Req(string dir, SIPEndPoint remote, SIPRequest req)
        {
            try
            {
                if (req.Method == SIPMethodsEnum.INVITE)
                    _log.LogInformation("SIP {Dir} INVITE {Remote} call {CallId}: {Sdp}", dir, remote, req.Header.CallId, Sdp(req.Body));
                else if (req.Method == SIPMethodsEnum.ACK)
                    _log.LogInformation("SIP {Dir} ACK {Remote} call {CallId}", dir, remote, req.Header.CallId);
                else if (req.Method == SIPMethodsEnum.BYE || req.Method == SIPMethodsEnum.CANCEL)
                    _log.LogInformation("SIP {Dir} {Method} {Remote} call {CallId} reason: {Reason}", dir, req.Method, remote, req.Header.CallId,
                        req.Header.Reason ?? "(none)");
            }
            catch { }
        }
        void Resp(string dir, SIPEndPoint remote, SIPResponse resp)
        {
            try
            {
                if (resp.Header.CSeqMethod == SIPMethodsEnum.INVITE && (int)resp.Status >= 180)
                    _log.LogInformation("SIP {Dir} {Status} {Remote} call {CallId} contact {Contact}: {Sdp}", dir, (int)resp.Status, remote, resp.Header.CallId,
                        resp.Header.Contact?.FirstOrDefault()?.ContactURI?.ToString() ?? "(none)",
                        (int)resp.Status == 200 || (int)resp.Status == 183 ? Sdp(resp.Body) : resp.ReasonPhrase);
            }
            catch { }
        }
        t.SIPRequestInTraceEvent += (_, remote, req) => Req("in", remote, req);
        t.SIPRequestOutTraceEvent += (_, remote, req) => Req("out", remote, req);
        t.SIPResponseInTraceEvent += (_, remote, resp) => Resp("in", remote, resp);
        t.SIPResponseOutTraceEvent += (_, remote, resp) => Resp("out", remote, resp);
    }

    /// <summary>
    /// Open one loopback-only SIP transport per public port for phones that come through the built-in secure network.
    /// The relay (afif-net) receives on tailnetIP:publicPort and forwards to these. Everything we send on them is
    /// rewritten to say tailnetIP:publicPort (Via, Contact) so the phone answers through the relay too.
    /// </summary>
    public void StartRelay(IEnumerable<int> publicPorts)
    {
        foreach (var pub in publicPorts.Where(p => p is > 0 and < 65536).Distinct())
        {
            if (_relays.Any(r => r.Public == pub)) continue;
            var t = new SIPTransport();
            var ch = new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0));
            t.AddSIPChannel(ch);
            int local = ch.ListeningEndPoint.Port;
            int publicPort = pub;
            t.CustomiseRequestHeader = (_, _, req) => RewriteForTailnet(req.Header, publicPort, rewriteVia: true);
            t.CustomiseResponseHeader = (_, _, resp) => RewriteForTailnet(resp.Header, publicPort, rewriteVia: false);
            var transport = t;
            t.SIPTransportRequestReceived += (localEp, remote, req) => OnRequest(transport, localEp, remote, req);
            Trace(t);
            _relayPorts[t] = pub;
            _relays.Add((pub, local, t));
            _log.LogInformation("Secure network SIP {Public} → 127.0.0.1:{Local}", pub, local);
        }
    }

    private SIPHeader? RewriteForTailnet(SIPHeader header, int publicPort, bool rewriteVia)
    {
        var ip = TailnetAddress;
        if (ip is null) return null;
        var copy = header.Copy();
        string host = $"{ip}:{publicPort}";
        if (copy.Contact is { Count: > 0 } && copy.Contact[0].ContactURI is not null)
            copy.Contact[0].ContactURI.Host = host;
        if (rewriteVia && copy.Vias.Via.Count > 0)
        {
            copy.Vias.Via[0].Host = ip.ToString();
            copy.Vias.Via[0].Port = publicPort;
        }
        return copy;
    }

    public bool IsRelay(SIPTransport t) => _relayPorts.ContainsKey(t);

    private Extension? FindExtension(string? number) =>
        number is null ? null : _config.Current.Extensions.FirstOrDefault(e => e.Number == number && e.Enabled);

    private string Realm => _config.Current.Settings.Realm;

    private Task OnRequest(SIPTransport t, SIPEndPoint local, SIPEndPoint remote, SIPRequest req)
    {
        try
        {
            bool inDialog = !string.IsNullOrEmpty(req.Header.To?.ToTag);
            switch (req.Method)
            {
                case SIPMethodsEnum.REGISTER:
                    return HandleRegister(t, local, remote, req);
                case SIPMethodsEnum.INVITE when !inDialog:
                    return HandleInvite(t, local, remote, req);
                case SIPMethodsEnum.OPTIONS when !inDialog:
                    return Respond(t, req, SIPResponseStatusCodesEnum.Ok);
                case SIPMethodsEnum.MESSAGE when !inDialog:
                    return HandleMessage(t, local, remote, req);
                case SIPMethodsEnum.SUBSCRIBE when !inDialog:
                case SIPMethodsEnum.PUBLISH:
                    return Respond(t, req, SIPResponseStatusCodesEnum.MethodNotAllowed);
                case SIPMethodsEnum.NOTIFY when !inDialog:
                    return Respond(t, req, SIPResponseStatusCodesEnum.Ok);
                default:
                    return Task.CompletedTask; // in-dialog requests belong to the call's user agent
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error handling {Method}", req.Method);
            return Task.CompletedTask;
        }
    }

    private static Task Respond(SIPTransport t, SIPRequest req, SIPResponseStatusCodesEnum code, string? reason = null)
    {
        var resp = SIPResponse.GetResponse(req, code, reason);
        return t.SendResponseAsync(resp);
    }

    /// <summary>Returns true if the request carries valid credentials; otherwise sends the challenge/denial.</summary>
    private async Task<Extension?> AuthenticateAsync(SIPTransport t, SIPEndPoint local, SIPEndPoint remote, SIPRequest req, string? user)
    {
        var ext = FindExtension(user);
        if (ext is null)
        {
            _log.LogWarning("{Method} from unknown extension {User} at {Remote}", req.Method, user, remote);
            await Respond(t, req, SIPResponseStatusCodesEnum.Forbidden).ConfigureAwait(false);
            return null;
        }
        var result = SIPRequestAuthenticator.AuthenticateSIPRequest(local, remote, req, new PbxSipAccount(ext, Realm));
        if (result.Authenticated) return ext;

        var resp = SIPResponse.GetResponse(req, result.ErrorResponse, null);
        if (result.AuthenticationRequiredHeader is not null) resp.Header.AuthenticationHeaders.Add(result.AuthenticationRequiredHeader);
        await t.SendResponseAsync(resp).ConfigureAwait(false);
        return null;
    }

    private async Task HandleRegister(SIPTransport t, SIPEndPoint local, SIPEndPoint remote, SIPRequest req)
    {
        var ext = await AuthenticateAsync(t, local, remote, req, req.Header.To?.ToURI?.User).ConfigureAwait(false);
        if (ext is null) return;

        var list = _bindings.GetOrAdd(ext.Number, _ => new List<SipBinding>());
        bool before, after;
        lock (list)
        {
            list.RemoveAll(b => b.IsExpired);
            before = list.Count > 0;
            bool wildcard = req.Header.Contact.Count == 1 && req.Header.Contact[0].ToString().Trim() == "*";
            if (wildcard || req.Header.Contact.Count == 0 && req.Header.Expires == 0)
            {
                list.Clear();
            }
            foreach (var c in wildcard ? new List<SIPContactHeader>() : req.Header.Contact)
            {
                if (c.ContactURI is null) continue;
                long exp = c.Expires >= 0 ? c.Expires : req.Header.Expires >= 0 ? req.Header.Expires : 3600;
                if (exp > 0) exp = Math.Clamp(exp, 60, 600); // short re-registration keeps bridged (Cloudflare One) phones reachable
                string uri = c.ContactURI.ToString();
                list.RemoveAll(b => string.Equals(b.ContactUri, uri, StringComparison.OrdinalIgnoreCase));
                if (exp > 0)
                    list.Add(new SipBinding(ext.Number, uri, remote, DateTime.UtcNow.AddSeconds(exp), req.Header.UserAgent ?? "", t, local.Port));
            }
            after = list.Count > 0;
        }

        var resp = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
        resp.Header.Contact = Bindings(ext.Number).Select(b =>
        {
            var h = new SIPContactHeader(null, SIPURI.ParseSIPURIRelaxed(b.ContactUri));
            h.Expires = (long)Math.Max(0, (b.ExpiresUtc - DateTime.UtcNow).TotalSeconds);
            return h;
        }).ToList();
        await t.SendResponseAsync(resp).ConfigureAwait(false);

        if (before != after)
        {
            _log.LogInformation("Extension {Ext} {State} ({UA} at {Remote})", ext.Number, after ? "registered" : "unregistered", req.Header.UserAgent, remote);
            RegistrationChanged?.Invoke(ext.Number, after);
        }
    }

    private async Task HandleInvite(SIPTransport t, SIPEndPoint local, SIPEndPoint remote, SIPRequest req)
    {
        var ext = await AuthenticateAsync(t, local, remote, req, req.Header.From?.FromURI?.User).ConfigureAwait(false);
        if (ext is null) return;

        string key = $"{req.Header.CallId}|{req.Header.CSeq}|{req.Header.Vias.TopViaHeader?.Branch}";
        var now = DateTime.UtcNow;
        if (!_seenInvites.TryAdd(key, now))
        {
            _log.LogDebug("Ignoring retransmitted INVITE {Key}", key);
            return;
        }
        if (_seenInvites.Count > 200)
            foreach (var old in _seenInvites.Where(kv => now - kv.Value > TimeSpan.FromSeconds(64)).Select(kv => kv.Key).ToList())
                _seenInvites.TryRemove(old, out _);

        var s = _config.Current.Settings;
        SipLeg leg;
        if (IsRelay(t))
        {
            var ip = TailnetAddress;
            if (ip is null) { await Respond(t, req, SIPResponseStatusCodesEnum.ServiceUnavailable).ConfigureAwait(false); return; }
            leg = SipLeg.FromInvite(t, req, s.TailnetRtpStart, s.TailnetRtpEnd, IPAddress.Loopback, ip, remote);
        }
        else
        {
            IPAddress? bind = IPAddress.TryParse(HostFor(remote), out var b) ? b : null;
            leg = SipLeg.FromInvite(t, req, s.RtpPortStart, s.RtpPortEnd, bind);
        }
        leg.Extension = ext.Number;
        leg.Number = ext.Number;
        leg.Name = string.IsNullOrWhiteSpace(ext.Name) ? ext.Number : ext.Name;
        string dialed = Uri.UnescapeDataString(req.URI.User ?? "");
        _log.LogInformation("Call from {Ext} to {Dialed}", ext.Number, dialed);
        IncomingCall?.Invoke(leg, dialed);
    }

    // ------------------------------------------------------------------ text messages (SMS ⇄ phones)

    private async Task HandleMessage(SIPTransport t, SIPEndPoint local, SIPEndPoint remote, SIPRequest req)
    {
        string key = $"{req.Header.CallId}|{req.Header.CSeq}";
        if (_seenMessages.TryGetValue(key, out var seen))
        {
            if (seen.Code > 0) await Respond(t, req, (SIPResponseStatusCodesEnum)seen.Code).ConfigureAwait(false);
            return; // still sending the first copy
        }
        var ext = await AuthenticateAsync(t, local, remote, req, req.Header.From?.FromURI?.User).ConfigureAwait(false);
        if (ext is null) return;
        if (!_seenMessages.TryAdd(key, (DateTime.UtcNow, 0))) return;
        foreach (var old in _seenMessages.Where(kv => DateTime.UtcNow - kv.Value.At > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList())
            _seenMessages.TryRemove(old, out _);

        var code = SIPResponseStatusCodesEnum.Ok;
        try
        {
            var (type, text) = MessageText(req.Header.ContentType, req.Body);
            if (!type.StartsWith("text/plain"))
            {
                // delivery/read receipts (IMDN), "is typing" and other non-text payloads: accept and ignore
            }
            else if (string.IsNullOrWhiteSpace(text)) code = SIPResponseStatusCodesEnum.BadRequest;
            else
            {
                string to = Uri.UnescapeDataString(req.URI?.User ?? req.Header.To?.ToURI?.User ?? "").Trim();
                if (FindExtension(to) is not null)
                {
                    _log.LogInformation("Message from {Ext} to extension {To}", ext.Number, to);
                    code = await SendTextAsync(to, ext.Number, text).ConfigureAwait(false) ? SIPResponseStatusCodesEnum.Ok : SIPResponseStatusCodesEnum.TemporarilyUnavailable;
                }
                else if (to.Length == 0 || TextFromPhone is null) code = SIPResponseStatusCodesEnum.NotFound;
                else
                {
                    _log.LogInformation("SMS from {Ext} to {To} ({Len} characters)", ext.Number, to, text.Length);
                    code = await TextFromPhone(ext.Number, to, text).ConfigureAwait(false) ? SIPResponseStatusCodesEnum.Ok : SIPResponseStatusCodesEnum.ServiceUnavailable;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Message from {Ext} failed", ext.Number);
            code = SIPResponseStatusCodesEnum.InternalServerError;
        }
        _seenMessages[key] = (DateTime.UtcNow, (int)code);
        await Respond(t, req, code).ConfigureAwait(false);
    }

    /// <summary>The content type and text of a SIP MESSAGE, unwrapping Message/CPIM (what Linphone sends).</summary>
    public static (string Type, string Text) MessageText(string? contentType, string? body)
    {
        string type = (contentType ?? "text/plain").Trim().ToLowerInvariant();
        body ??= "";
        if (type.StartsWith("message/cpim"))
        {
            // CPIM: message headers, blank line, MIME headers, blank line, content
            var b = body.Replace("\r\n", "\n");
            int first = b.IndexOf("\n\n", StringComparison.Ordinal);
            if (first < 0) return ("text/plain", b.Trim());
            var rest = b[(first + 2)..];
            int second = rest.IndexOf("\n\n", StringComparison.Ordinal);
            string mime = second < 0 ? "" : rest[..second];
            string content = second < 0 ? rest : rest[(second + 2)..];
            var ct = mime.Split('\n').FirstOrDefault(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));
            type = ct is null ? "text/plain" : ct[13..].Trim().ToLowerInvariant();
            body = content;
        }
        return (type, body.TrimEnd('\n', '\r'));
    }

    /// <summary>
    /// Send a text to every phone of an extension (SIP MESSAGE, UTF-8 — Arabic and English). The sender shows as
    /// <paramref name="fromNumber"/>, so replying in the phone's message screen goes back to that number.
    /// True when at least one phone accepted it.
    /// </summary>
    public async Task<bool> SendTextAsync(string extension, string fromNumber, string text)
    {
        var tasks = new List<Task<bool>>();
        foreach (var b in Bindings(extension))
        {
            string? host = IsRelay(b.Transport) ? TailnetAddress?.ToString() : HostFor(b.RemoteEndPoint);
            if (host is null) continue;
            try
            {
                var uri = SIPURI.ParseSIPURIRelaxed(b.ContactUri);
                var req = SIPRequest.GetRequest(SIPMethodsEnum.MESSAGE, uri,
                    new SIPToHeader(null, new SIPURI(extension, host, null), null),
                    new SIPFromHeader(null, new SIPURI(fromNumber, host, null), CallProperties.CreateNewTag()));
                req.Header.ContentType = "text/plain;charset=UTF-8";
                req.Body = text;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var tx = new SIPNonInviteTransaction(b.Transport, req, b.RemoteEndPoint);
                tx.NonInviteTransactionFinalResponseReceived += (_, _, _, resp) =>
                {
                    tcs.TrySetResult(resp.StatusCode is >= 200 and < 300);
                    return Task.FromResult(SocketError.Success);
                };
                tx.NonInviteTransactionFailed += (_, _) => tcs.TrySetResult(false);
                tx.SendRequest();
                tasks.Add(tcs.Task.WaitAsync(TimeSpan.FromSeconds(35)).ContinueWith(x => x.IsCompletedSuccessfully && x.Result));
            }
            catch (Exception ex) { _log.LogWarning("Message to {Ext} at {Contact} failed: {Message}", extension, b.ContactUri, ex.Message); }
        }
        if (tasks.Count == 0) return false;
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Any(r => r);
    }

    /// <summary>Current (non-expired) registrations for an extension.</summary>
    public IReadOnlyList<SipBinding> Bindings(string extension)
    {
        if (!_bindings.TryGetValue(extension, out var list)) return Array.Empty<SipBinding>();
        lock (list)
        {
            list.RemoveAll(b => b.IsExpired);
            return list.ToList();
        }
    }

    public IReadOnlyList<SipBinding> AllBindings() =>
        _bindings.Keys.SelectMany(Bindings).ToList();

    public bool IsRegistered(string extension) => Bindings(extension).Count > 0;

    /// <summary>Ring every phone registered for an extension.</summary>
    public List<SipLeg> Ring(string extension, string callerNumber, string callerName, int ringSeconds)
    {
        var s = _config.Current.Settings;
        var legs = new List<SipLeg>();
        foreach (var b in Bindings(extension))
        {
            SipLeg leg;
            if (IsRelay(b.Transport))
            {
                var ip = TailnetAddress;
                if (ip is null) continue; // secure network is off: this phone can't be reached now
                leg = SipLeg.Dial(b.Transport, b.RemoteEndPoint, b.ContactUri, callerNumber, callerName,
                    ip.ToString(), ringSeconds, s.TailnetRtpStart, s.TailnetRtpEnd, IPAddress.Loopback, ip);
            }
            else
                leg = SipLeg.Dial(b.Transport, b.RemoteEndPoint, b.ContactUri, callerNumber, callerName,
                    HostFor(b.RemoteEndPoint), ringSeconds, s.RtpPortStart, s.RtpPortEnd);
            leg.Extension = extension;
            legs.Add(leg);
        }
        return legs;
    }

    /// <summary>The local IP a phone at this address can reach the PBX on (used in From headers).</summary>
    private string HostFor(SIPEndPoint remote)
    {
        if (!BindAddress.Equals(IPAddress.Any)) return BindAddress.ToString();
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(remote.Address, Math.Max(1, remote.Port));
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return LocalAddresses().FirstOrDefault() ?? "127.0.0.1"; }
    }

    public static IReadOnlyList<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254"))
            .Select(a => a.Address.ToString()).Distinct().ToList();

    public void Dispose()
    {
        foreach (var (t, _) in _transports) { try { t.Shutdown(); } catch { } }
        foreach (var r in _relays) { try { r.Transport.Shutdown(); } catch { } }
    }
}
