using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AfifPbx;
using AfifPbx.Data;
using AfifPbx.Gsm;
using AfifPbx.Media;
using AfifPbx.Server;
using AfifPbx.Sip;
using Microsoft.AspNetCore.Http.Json;

// ---------------------------------------------------------------- start-up
string dataDir = ConfigStore.DefaultDataDir();
for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--data") dataDir = args[i + 1];
Directory.CreateDirectory(dataDir);

var logProvider = new PbxLogProvider(dataDir);
using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logProvider).SetMinimumLevel(LogLevel.Information));
var startLog = loggerFactory.CreateLogger("AFIF-PBX");
string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
startLog.LogInformation("AFIF-PBX {Version} starting (data: {Dir})", version, dataDir);

var engine = new PbxEngine(dataDir, loggerFactory);
try { engine.Start(); }
catch (SocketException ex)
{
    startLog.LogError("SIP port {Port} is in use by another program: {Message}", engine.Config.Current.Settings.SipPort, ex.Message);
    throw;
}

// HTTP port: the configured one, or 8080 if something (IIS, Skype...) already holds it
int httpPort = engine.Config.Current.Settings.HttpPort;
if (!PortFree(httpPort))
{
    startLog.LogWarning("Port {Port} is in use; the web panel will use 8080", httpPort);
    httpPort = PortFree(8080) ? 8080 : 8088;
}
File.WriteAllText(Path.Combine(dataDir, "http-port.txt"), httpPort.ToString());

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Host.UseWindowsService(o => o.ServiceName = "AFIF-PBX");
builder.Logging.ClearProviders();
builder.Logging.AddProvider(logProvider);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Any, httpPort);
    k.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
});
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.WriteIndented = false;
});

var app = builder.Build();
var sessions = new ConcurrentDictionary<string, DateTime>();
const string Cookie = "afif_session";

app.Lifetime.ApplicationStopping.Register(() =>
{
    startLog.LogInformation("AFIF-PBX stopping");
    engine.Dispose();
});

// ---------------------------------------------------------------- auth
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/") && path != "/api/login" && !path.StartsWith("/api/local/"))
    {
        bool ok = ctx.Request.Cookies.TryGetValue(Cookie, out var token) && token is not null
                  && sessions.TryGetValue(token, out var exp) && exp > DateTime.UtcNow;
        if (!ok) { ctx.Response.StatusCode = 401; return; }
        sessions[token!] = DateTime.UtcNow.AddHours(12);
    }
    if (path.StartsWith("/api/local/") && !IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? IPAddress.None))
    {
        ctx.Response.StatusCode = 403; return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

var api = app.MapGroup("/api");

api.MapPost("/login", (LoginRequest req, HttpContext ctx) =>
{
    var s = engine.Config.Current.Settings;
    if (!string.Equals(req.User?.Trim(), s.AdminUser, StringComparison.OrdinalIgnoreCase) || !Passwords.Verify(req.Password ?? "", s.AdminPasswordHash))
    {
        Thread.Sleep(600);
        return Results.Json(new { error = "Wrong user name or password" }, statusCode: 401);
    }
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    sessions[token] = DateTime.UtcNow.AddHours(12);
    ctx.Response.Cookies.Append(Cookie, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
    return Results.Ok(new { user = s.AdminUser, mustChangePassword = s.AdminMustChangePassword });
});

api.MapPost("/logout", (HttpContext ctx) =>
{
    if (ctx.Request.Cookies.TryGetValue(Cookie, out var t) && t is not null) sessions.TryRemove(t, out _);
    ctx.Response.Cookies.Delete(Cookie);
    return Results.Ok();
});

api.MapGet("/me", () => new
{
    user = engine.Config.Current.Settings.AdminUser,
    mustChangePassword = engine.Config.Current.Settings.AdminMustChangePassword,
    version,
});

api.MapPost("/password", (PasswordRequest req) =>
{
    var s = engine.Config.Current.Settings;
    if (!Passwords.Verify(req.Current ?? "", s.AdminPasswordHash)) return Results.Json(new { error = "Current password is wrong" }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(req.New) || req.New.Length < 6) return Results.Json(new { error = "Use at least 6 characters" }, statusCode: 400);
    engine.Config.Update(c =>
    {
        c.Settings.AdminPasswordHash = Passwords.Hash(req.New);
        c.Settings.AdminMustChangePassword = false;
        if (!string.IsNullOrWhiteSpace(req.User)) c.Settings.AdminUser = req.User.Trim();
    });
    return Results.Ok();
});

// ---------------------------------------------------------------- dashboard
object StatusDto() => new
{
    version,
    startedUtc = engine.StartedUtc,
    uptimeSeconds = (long)(DateTime.UtcNow - engine.StartedUtc).TotalSeconds,
    sipPort = engine.Config.Current.Settings.SipPort,
    httpPort,
    addresses = SipServer.LocalAddresses(),
    extensions = engine.Config.Current.Extensions.Select(e => new
    {
        e.Number, e.Name, e.Enabled,
        registered = engine.Sip.IsRegistered(e.Number),
        devices = engine.Sip.Bindings(e.Number).Select(b => new { b.UserAgent, address = b.RemoteEndPoint.ToString(), expires = b.ExpiresUtc }),
    }),
    modem = engine.Modem?.Status,
    gsmEnabled = engine.Config.Current.Settings.GsmEnabled,
    activeCalls = ActiveCallsDto(),
    newVoicemails = engine.Db.Voicemails().Count(v => !v.Heard),
    recentCalls = engine.Db.Calls(8),
    recentSms = engine.Db.Sms(5),
};

object ActiveCallsDto() => engine.Calls.ActiveCalls.Select(c => new
{
    c.Id, c.From, c.FromName, c.To, c.Direction, c.State, c.ConnectedTo, c.Start, c.Answer, recording = !string.IsNullOrEmpty(c.Recording),
}).ToList();

api.MapGet("/status", StatusDto);
app.MapGet("/api/local/status", () => new
{
    running = true,
    version,
    httpPort,
    sipPort = engine.Config.Current.Settings.SipPort,
    addresses = SipServer.LocalAddresses(),
    registered = engine.Config.Current.Extensions.Count(e => engine.Sip.IsRegistered(e.Number)),
    extensions = engine.Config.Current.Extensions.Count,
    activeCalls = engine.Calls.ActiveCalls.Count,
    modem = engine.Modem?.Status,
    newVoicemails = engine.Db.Voicemails().Count(v => !v.Heard),
});
app.MapPost("/api/local/gsm/reconnect", () => { engine.Modem?.Reconnect(); return Results.Ok(); });

// ---------------------------------------------------------------- extensions
api.MapGet("/extensions", () => engine.Config.Current.Extensions.OrderBy(e => e.Number, NumberOrder.Instance));
api.MapPost("/extensions", (Extension ext) =>
{
    var err = ValidateNumber(ext.Number, null);
    if (err is not null) return Results.Json(new { error = err }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(ext.Secret)) ext.Secret = Passwords.NewSecret();
    engine.Config.Update(c => c.Extensions.Add(ext));
    return Results.Ok(ext);
});
api.MapPut("/extensions/{number}", (string number, Extension ext) =>
{
    var err = ValidateNumber(ext.Number, number);
    if (err is not null) return Results.Json(new { error = err }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(ext.Secret)) ext.Secret = Passwords.NewSecret();
    bool found = false;
    engine.Config.Update(c =>
    {
        int i = c.Extensions.FindIndex(e => e.Number == number);
        if (i >= 0) { c.Extensions[i] = ext; found = true; }
        if (number != ext.Number) RenameReferences(c, "ext:" + number, "ext:" + ext.Number, number, ext.Number);
    });
    return found ? Results.Ok(ext) : Results.NotFound();
});
api.MapDelete("/extensions/{number}", (string number) =>
{
    engine.Config.Update(c =>
    {
        c.Extensions.RemoveAll(e => e.Number == number);
        foreach (var rg in c.RingGroups) rg.Members.Remove(number);
    });
    return Results.Ok();
});

// ---------------------------------------------------------------- ring groups
api.MapGet("/ringgroups", () => engine.Config.Current.RingGroups.OrderBy(r => r.Number, NumberOrder.Instance));
api.MapPost("/ringgroups", (RingGroup rg) =>
{
    var err = ValidateNumber(rg.Number, null);
    if (err is not null) return Results.Json(new { error = err }, statusCode: 400);
    engine.Config.Update(c => c.RingGroups.Add(rg));
    return Results.Ok(rg);
});
api.MapPut("/ringgroups/{number}", (string number, RingGroup rg) =>
{
    var err = ValidateNumber(rg.Number, number);
    if (err is not null) return Results.Json(new { error = err }, statusCode: 400);
    engine.Config.Update(c =>
    {
        int i = c.RingGroups.FindIndex(r => r.Number == number);
        if (i >= 0) c.RingGroups[i] = rg;
        if (number != rg.Number) RenameReferences(c, "rg:" + number, "rg:" + rg.Number, null, null);
    });
    return Results.Ok(rg);
});
api.MapDelete("/ringgroups/{number}", (string number) =>
{
    engine.Config.Update(c => c.RingGroups.RemoveAll(r => r.Number == number));
    return Results.Ok();
});

// ---------------------------------------------------------------- IVRs
api.MapGet("/ivrs", () => engine.Config.Current.Ivrs.OrderBy(r => r.Number, NumberOrder.Instance));
api.MapPost("/ivrs", (Ivr ivr) =>
{
    var err = ValidateNumber(ivr.Number, null);
    if (err is not null) return Results.Json(new { error = err }, statusCode: 400);
    engine.Config.Update(c => c.Ivrs.Add(ivr));
    return Results.Ok(ivr);
});
api.MapPut("/ivrs/{number}", (string number, Ivr ivr) =>
{
    var err = ValidateNumber(ivr.Number, number);
    if (err is not null) return Results.Json(new { error = err }, statusCode: 400);
    engine.Config.Update(c =>
    {
        int i = c.Ivrs.FindIndex(r => r.Number == number);
        if (i >= 0) c.Ivrs[i] = ivr;
        if (number != ivr.Number) RenameReferences(c, "ivr:" + number, "ivr:" + ivr.Number, null, null);
    });
    return Results.Ok(ivr);
});
api.MapDelete("/ivrs/{number}", (string number) =>
{
    engine.Config.Update(c => c.Ivrs.RemoveAll(r => r.Number == number));
    return Results.Ok();
});

// ---------------------------------------------------------------- settings
api.MapGet("/settings", () =>
{
    var s = engine.Config.Get().Settings;
    s.AdminPasswordHash = "";
    return s;
});
api.MapPut("/settings", (PbxSettings incoming) =>
{
    bool restartNeeded = false;
    engine.Config.Update(c =>
    {
        var cur = c.Settings;
        restartNeeded = cur.SipPort != incoming.SipPort || cur.HttpPort != incoming.HttpPort || cur.GsmEnabled != incoming.GsmEnabled
                        || cur.RtpPortStart != incoming.RtpPortStart || cur.RtpPortEnd != incoming.RtpPortEnd;
        incoming.AdminPasswordHash = cur.AdminPasswordHash;
        incoming.AdminUser = cur.AdminUser;
        incoming.AdminMustChangePassword = cur.AdminMustChangePassword;
        bool reopen = cur.GsmAtPort != incoming.GsmAtPort || cur.GsmAudioPort != incoming.GsmAudioPort;
        if (cur.TtsVoice != incoming.TtsVoice) engine.Prompts.ClearCache();
        c.Settings = incoming;
        if (reopen) engine.Modem?.Reconnect();
    });
    return Results.Ok(new { restartNeeded });
});

// ---------------------------------------------------------------- GSM line
api.MapGet("/gsm", () => new
{
    enabled = engine.Config.Current.Settings.GsmEnabled,
    status = engine.Modem?.Status,
    ports = PortDiscovery.ListComPorts(),
    huaweiDevices = PortDiscovery.HuaweiDevices(),
});
api.MapPost("/gsm/reconnect", () => { engine.Modem?.Reconnect(); return Results.Ok(); });
api.MapPost("/gsm/ussd", async (UssdRequest req) =>
{
    if (engine.Modem is null) return Results.Json(new { error = "GSM line is off" }, statusCode: 400);
    var reply = await engine.Modem.SendUssdAsync(req.Code ?? "");
    return reply is null ? Results.Json(new { error = "No reply from the network" }, statusCode: 504) : Results.Ok(new { reply });
});

// ---------------------------------------------------------------- SMS
api.MapGet("/sms", () => engine.Db.Sms());
api.MapPost("/sms", async (SmsRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Number) || string.IsNullOrWhiteSpace(req.Text))
        return Results.Json(new { error = "Number and message are required" }, statusCode: 400);
    bool ok = await engine.SendSmsAsync(req.Number.Trim(), req.Text);
    return ok ? Results.Ok() : Results.Json(new { error = "The dongle could not send the SMS" }, statusCode: 502);
});
api.MapDelete("/sms/{id:long}", (long id) => { engine.Db.DeleteSms(id); return Results.Ok(); });

// ---------------------------------------------------------------- voicemail
api.MapGet("/voicemail", (string? mailbox) => engine.Db.Voicemails(string.IsNullOrWhiteSpace(mailbox) ? null : mailbox));
api.MapGet("/voicemail/{id:long}/audio", (long id) =>
{
    var m = engine.Db.Voicemail(id);
    var path = m is null ? null : SafePath(engine.Calls.VoicemailDir, m.File);
    return path is null || !File.Exists(path) ? Results.NotFound() : Results.File(path, "audio/wav", Path.GetFileName(path), enableRangeProcessing: true);
});
api.MapPost("/voicemail/{id:long}/heard", (long id) => { engine.Db.MarkVoicemailHeard(id); return Results.Ok(); });
api.MapDelete("/voicemail/{id:long}", (long id) => { engine.Calls.DeleteVoicemail(id); return Results.Ok(); });

// ---------------------------------------------------------------- recordings
api.MapGet("/recordings", () =>
{
    var dir = engine.Calls.RecordingsDir;
    if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<object>());
    var byFile = engine.Db.Calls(5000).Where(c => !string.IsNullOrEmpty(c.Recording)).GroupBy(c => c.Recording).ToDictionary(g => g.Key, g => g.First());
    var list = Directory.GetFiles(dir, "*.wav", SearchOption.AllDirectories)
        .Select(f => new FileInfo(f))
        .OrderByDescending(f => f.LastWriteTimeUtc)
        .Take(1000)
        .Select(f =>
        {
            var rel = Path.GetRelativePath(dir, f.FullName).Replace('\\', '/');
            byFile.TryGetValue(rel, out var call);
            return new
            {
                path = rel, size = f.Length, time = f.LastWriteTimeUtc,
                seconds = (int)Math.Max(0, (f.Length - 44) / 4 / Audio.SampleRate),
                from = call?.FromNumber ?? "", to = call?.ToNumber ?? "", direction = call?.Direction ?? "",
            };
        });
    return Results.Ok(list);
});
api.MapGet("/recordings/file", (string path) =>
{
    var full = SafePath(engine.Calls.RecordingsDir, path);
    return full is null || !File.Exists(full) ? Results.NotFound() : Results.File(full, "audio/wav", Path.GetFileName(full), enableRangeProcessing: true);
});
api.MapDelete("/recordings", (string path) =>
{
    var full = SafePath(engine.Calls.RecordingsDir, path);
    if (full is null || !File.Exists(full)) return Results.NotFound();
    File.Delete(full);
    engine.Db.ClearRecording(path);
    return Results.Ok();
});

// ---------------------------------------------------------------- call log / live calls
api.MapGet("/calls", (string? search, int? limit) => engine.Db.Calls(Math.Clamp(limit ?? 300, 1, 5000), search));
api.MapGet("/calls/active", ActiveCallsDto);
api.MapPost("/calls/{id}/hangup", (string id) => engine.Calls.HangupCall(id) ? Results.Ok() : Results.NotFound());

// ---------------------------------------------------------------- prompts / speech
api.MapGet("/prompts", () => engine.Prompts.Files());
api.MapPost("/prompts", async (HttpRequest req) =>
{
    if (!req.HasFormContentType) return Results.BadRequest();
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null) return Results.Json(new { error = "Choose a WAV file" }, statusCode: 400);
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    try { return Results.Ok(new { name = engine.Prompts.SaveUpload(file.FileName, ms.ToArray()) }); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
});
api.MapDelete("/prompts/{name}", (string name) => { engine.Prompts.Delete(name); return Results.Ok(); });
api.MapGet("/prompts/{name}/audio", (string name) =>
{
    var full = SafePath(engine.Prompts.PromptDir, name);
    return full is null || !File.Exists(full) ? Results.NotFound() : Results.File(full, "audio/wav");
});
api.MapPost("/speech/preview", (SpeechRequest req) =>
{
    var samples = engine.Prompts.Say(string.IsNullOrWhiteSpace(req.Text) ? "Welcome to AFIF PBX." : req.Text);
    return Results.File(Wav.ToBytes(samples), "audio/wav");
});
api.MapGet("/voices", () => engine.Prompts.Voices());

// ---------------------------------------------------------------- logs & backup
api.MapGet("/logs", () => logProvider.Recent.Reverse().Take(500));
api.MapGet("/backup", () =>
{
    var json = File.ReadAllBytes(Path.Combine(dataDir, "config.json"));
    return Results.File(json, "application/json", $"afif-pbx-config-{DateTime.Now:yyyyMMdd}.json");
});
api.MapPost("/restore", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var text = await reader.ReadToEndAsync();
    PbxConfig? cfg;
    try { cfg = JsonSerializer.Deserialize<PbxConfig>(text, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); }
    catch (Exception ex) { return Results.Json(new { error = "Not a valid AFIF-PBX backup: " + ex.Message }, statusCode: 400); }
    if (cfg is null) return Results.BadRequest();
    engine.Config.Update(c =>
    {
        var keepAdmin = c.Settings;
        c.Extensions = cfg.Extensions; c.RingGroups = cfg.RingGroups; c.Ivrs = cfg.Ivrs;
        cfg.Settings.AdminPasswordHash = keepAdmin.AdminPasswordHash;
        cfg.Settings.AdminUser = keepAdmin.AdminUser;
        cfg.Settings.AdminMustChangePassword = keepAdmin.AdminMustChangePassword;
        c.Settings = cfg.Settings;
    });
    return Results.Ok();
});

// SPA fallback
app.MapFallbackToFile("index.html");

startLog.LogInformation("Web panel on http://localhost:{Port}/  — SIP on port {Sip}", httpPort, engine.Config.Current.Settings.SipPort);
app.Run();

// ---------------------------------------------------------------- helpers
string? ValidateNumber(string? number, string? current)
{
    if (string.IsNullOrWhiteSpace(number) || !number.All(char.IsDigit) || number.Length is < 2 or > 6)
        return "Use 2–6 digits for the number";
    if (number == current) return null;
    var c = engine.Config.Current;
    if (c.Extensions.Any(e => e.Number == number) || c.RingGroups.Any(r => r.Number == number) || c.Ivrs.Any(i => i.Number == number))
        return $"{number} is already used";
    return null;
}

static void RenameReferences(PbxConfig c, string oldDest, string newDest, string? oldExt, string? newExt)
{
    string Fix(string d) => d == oldDest ? newDest : d;
    c.Settings.GsmInboundDestination = Fix(c.Settings.GsmInboundDestination);
    foreach (var e in c.Extensions) e.NoAnswerDestination = Fix(e.NoAnswerDestination);
    foreach (var r in c.RingGroups)
    {
        r.FailoverDestination = Fix(r.FailoverDestination);
        if (oldExt is not null && newExt is not null) r.Members = r.Members.Select(m => m == oldExt ? newExt : m).ToList();
    }
    foreach (var i in c.Ivrs)
    {
        i.TimeoutDestination = Fix(i.TimeoutDestination);
        foreach (var k in i.Options.Keys.ToList()) i.Options[k] = Fix(i.Options[k]);
    }
}

static string? SafePath(string root, string relative)
{
    if (string.IsNullOrWhiteSpace(relative)) return null;
    var full = Path.GetFullPath(Path.Combine(root, relative));
    var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
    return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
}

static bool PortFree(int port)
{
    try
    {
        var l = new TcpListener(IPAddress.Any, port);
        l.Start();
        l.Stop();
        return true;
    }
    catch { return false; }
}

record LoginRequest(string? User, string? Password);
record PasswordRequest(string? Current, string? New, string? User);
record SmsRequest(string? Number, string? Text);
record UssdRequest(string? Code);
record SpeechRequest(string? Text);

sealed class NumberOrder : IComparer<string>
{
    public static readonly NumberOrder Instance = new();
    public int Compare(string? a, string? b) =>
        long.TryParse(a, out var x) && long.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);
}
