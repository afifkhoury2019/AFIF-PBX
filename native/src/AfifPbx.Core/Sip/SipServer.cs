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
public sealed record SipBinding(string Extension, string ContactUri, SIPEndPoint RemoteEndPoint, DateTime ExpiresUtc, string UserAgent)
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

    public SIPTransport Transport { get; } = new();
    public int Port { get; private set; }
    public IPAddress BindAddress { get; private set; } = IPAddress.Any;

    /// <summary>A phone placed a call: (leg from the phone, dialled number).</summary>
    public event Action<SipLeg, string>? IncomingCall;
    public event Action<string, bool>? RegistrationChanged;

    public SipServer(ConfigStore config, ILogger? log = null)
    {
        _config = config;
        _log = log ?? NullLogger.Instance;
    }

    public void Start(IPAddress bind, int port)
    {
        BindAddress = bind; Port = port;
        Transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(bind, port)));
        try { Transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(bind, port))); }
        catch (Exception ex) { _log.LogWarning("SIP over TCP not available: {Message}", ex.Message); }
        Transport.SIPTransportRequestReceived += OnRequest;
        _log.LogInformation("SIP server listening on {Address}:{Port}", bind, port);
    }

    private Extension? FindExtension(string? number) =>
        number is null ? null : _config.Current.Extensions.FirstOrDefault(e => e.Number == number && e.Enabled);

    private string Realm => _config.Current.Settings.Realm;

    private Task OnRequest(SIPEndPoint local, SIPEndPoint remote, SIPRequest req)
    {
        try
        {
            bool inDialog = !string.IsNullOrEmpty(req.Header.To?.ToTag);
            switch (req.Method)
            {
                case SIPMethodsEnum.REGISTER:
                    return HandleRegister(local, remote, req);
                case SIPMethodsEnum.INVITE when !inDialog:
                    return HandleInvite(local, remote, req);
                case SIPMethodsEnum.OPTIONS when !inDialog:
                    return Respond(req, SIPResponseStatusCodesEnum.Ok);
                case SIPMethodsEnum.SUBSCRIBE when !inDialog:
                case SIPMethodsEnum.PUBLISH:
                case SIPMethodsEnum.MESSAGE when !inDialog:
                    return Respond(req, SIPResponseStatusCodesEnum.MethodNotAllowed);
                case SIPMethodsEnum.NOTIFY when !inDialog:
                    return Respond(req, SIPResponseStatusCodesEnum.Ok);
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

    private Task Respond(SIPRequest req, SIPResponseStatusCodesEnum code, string? reason = null)
    {
        var resp = SIPResponse.GetResponse(req, code, reason);
        return Transport.SendResponseAsync(resp);
    }

    /// <summary>Returns true if the request carries valid credentials; otherwise sends the challenge/denial.</summary>
    private async Task<Extension?> AuthenticateAsync(SIPEndPoint local, SIPEndPoint remote, SIPRequest req, string? user)
    {
        var ext = FindExtension(user);
        if (ext is null)
        {
            _log.LogWarning("{Method} from unknown extension {User} at {Remote}", req.Method, user, remote);
            await Respond(req, SIPResponseStatusCodesEnum.Forbidden).ConfigureAwait(false);
            return null;
        }
        var result = SIPRequestAuthenticator.AuthenticateSIPRequest(local, remote, req, new PbxSipAccount(ext, Realm));
        if (result.Authenticated) return ext;

        var resp = SIPResponse.GetResponse(req, result.ErrorResponse, null);
        if (result.AuthenticationRequiredHeader is not null) resp.Header.AuthenticationHeaders.Add(result.AuthenticationRequiredHeader);
        await Transport.SendResponseAsync(resp).ConfigureAwait(false);
        return null;
    }

    private async Task HandleRegister(SIPEndPoint local, SIPEndPoint remote, SIPRequest req)
    {
        var ext = await AuthenticateAsync(local, remote, req, req.Header.To?.ToURI?.User).ConfigureAwait(false);
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
                    list.Add(new SipBinding(ext.Number, uri, remote, DateTime.UtcNow.AddSeconds(exp), req.Header.UserAgent ?? ""));
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
        await Transport.SendResponseAsync(resp).ConfigureAwait(false);

        if (before != after)
        {
            _log.LogInformation("Extension {Ext} {State} ({UA} at {Remote})", ext.Number, after ? "registered" : "unregistered", req.Header.UserAgent, remote);
            RegistrationChanged?.Invoke(ext.Number, after);
        }
    }

    private async Task HandleInvite(SIPEndPoint local, SIPEndPoint remote, SIPRequest req)
    {
        var ext = await AuthenticateAsync(local, remote, req, req.Header.From?.FromURI?.User).ConfigureAwait(false);
        if (ext is null) return;

        var s = _config.Current.Settings;
        var leg = SipLeg.FromInvite(Transport, req, s.RtpPortStart, s.RtpPortEnd);
        leg.Extension = ext.Number;
        leg.Number = ext.Number;
        leg.Name = string.IsNullOrWhiteSpace(ext.Name) ? ext.Number : ext.Name;
        string dialed = Uri.UnescapeDataString(req.URI.User ?? "");
        _log.LogInformation("Call from {Ext} to {Dialed}", ext.Number, dialed);
        IncomingCall?.Invoke(leg, dialed);
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
            var leg = SipLeg.Dial(Transport, b.RemoteEndPoint, b.ContactUri, callerNumber, callerName,
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
        try { Transport.Shutdown(); } catch { }
    }
}
