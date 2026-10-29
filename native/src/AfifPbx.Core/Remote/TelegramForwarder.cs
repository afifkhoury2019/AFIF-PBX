using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using AfifPbx.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AfifPbx.Remote;

/// <summary>Telegram settings (telegram.json; the bot token is encrypted for this PC).</summary>
public sealed class TelegramConfig
{
    public bool Enabled { get; set; }
    public string ProtectedToken { get; set; } = "";
    /// <summary>Chats (people or groups) that receive the messages, e.g. 1655852624.</summary>
    public List<string> ChatIds { get; set; } = new();
    public bool ForwardSms { get; set; } = true;
    public bool ForwardMissedCalls { get; set; } = true;
}

public sealed class TelegramStatus
{
    public bool Enabled { get; set; }
    public bool HasToken { get; set; }
    public List<string> ChatIds { get; set; } = new();
    public bool ForwardSms { get; set; }
    public bool ForwardMissedCalls { get; set; }
    public int Queued { get; set; }
    public DateTime? LastSentUtc { get; set; }
    public string LastError { get; set; } = "";
}

/// <summary>
/// Forwards incoming SMS and missed GSM calls to Telegram through your bot (Bot API sendMessage), like the
/// sms_to_telegram.py script did on the Raspberry Pi. Messages wait in a queue (kept on disk) while the internet is down.
/// </summary>
public sealed class TelegramForwarder : IDisposable
{
    private readonly string _path, _queuePath;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private TelegramConfig _cfg;
    private readonly List<string> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private DateTime? _lastSent;
    private string _lastError = "";

    /// <summary>Telegram's API address (tests point it at a fake).</summary>
    public string ApiBase { get; set; } = "https://api.telegram.org";

    public TelegramForwarder(string dataDir, ILogger? log = null, HttpMessageHandler? handler = null)
    {
        _path = Path.Combine(dataDir, "telegram.json");
        _queuePath = Path.Combine(dataDir, "telegram-queue.json");
        _log = log ?? NullLogger.Instance;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _cfg = Load<TelegramConfig>(_path) ?? new();
        _queue = Load<List<string>>(_queuePath) ?? new();
    }

    private static T? Load<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; } catch { return default; }
    }

    public TelegramConfig Config { get { lock (_lock) return JsonSerializer.Deserialize<TelegramConfig>(JsonSerializer.Serialize(_cfg))!; } }

    private void SaveConfig() { lock (_lock) File.WriteAllText(_path, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true })); }
    private void SaveQueue() { lock (_lock) File.WriteAllText(_queuePath, JsonSerializer.Serialize(_queue)); }

    public TelegramStatus Status()
    {
        lock (_lock)
            return new TelegramStatus
            {
                Enabled = _cfg.Enabled, HasToken = _cfg.ProtectedToken.Length > 0, ChatIds = _cfg.ChatIds.ToList(),
                ForwardSms = _cfg.ForwardSms, ForwardMissedCalls = _cfg.ForwardMissedCalls,
                Queued = _queue.Count, LastSentUtc = _lastSent, LastError = _lastError,
            };
    }

    /// <summary>Save settings. An empty token keeps the saved one.</summary>
    public void Configure(bool enabled, string? token, IEnumerable<string> chatIds, bool forwardSms, bool forwardMissedCalls)
    {
        lock (_lock)
        {
            _cfg.Enabled = enabled;
            if (!string.IsNullOrWhiteSpace(token)) _cfg.ProtectedToken = Secrets.Protect(token.Trim());
            _cfg.ChatIds = chatIds.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList();
            _cfg.ForwardSms = forwardSms;
            _cfg.ForwardMissedCalls = forwardMissedCalls;
        }
        SaveConfig();
        _wake.Release();
    }

    private string Token => Secrets.Unprotect(Config.ProtectedToken);

    // ------------------------------------------------------------------ what gets forwarded

    public void Sms(string from, string text, DateTime timeUtc)
    {
        var c = Config;
        if (!c.Enabled || !c.ForwardSms) return;
        Enqueue($"📩 New SMS\nFrom: {from}\nTime: {timeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n\n{text}");
    }

    /// <summary>A call finished; missed incoming GSM calls are forwarded.</summary>
    public void Call(CallRecord r)
    {
        var c = Config;
        if (!c.Enabled || !c.ForwardMissedCalls) return;
        if (r.Direction != "inbound" || r.Disposition == "ANSWERED") return;
        string who = string.IsNullOrWhiteSpace(r.FromNumber) ? "unknown number" : r.FromNumber;
        if (!string.IsNullOrWhiteSpace(r.FromName) && r.FromName != r.FromNumber) who += $" ({r.FromName})";
        string what = r.Disposition == "VOICEMAIL" ? "Missed call — left a voicemail" : "Missed call";
        Enqueue($"📞 {what}\nFrom: {who}\nTime: {r.Start.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    private void Enqueue(string text)
    {
        lock (_lock)
        {
            _queue.Add(text);
            while (_queue.Count > 500) _queue.RemoveAt(0);
        }
        SaveQueue();
        _wake.Release();
    }

    // ------------------------------------------------------------------ sending

    private async Task<(bool Ok, string Error)> SendAsync(string token, string chatId, string text)
    {
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = chatId, ["text"] = text });
            using var resp = await _http.PostAsync($"{ApiBase}/bot{token}/sendMessage", form, _cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return (true, "");
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            string desc = "";
            try { desc = JsonNode.Parse(body)?["description"]?.GetValue<string>() ?? ""; } catch { }
            return (false, $"Telegram said {(int)resp.StatusCode}{(desc.Length > 0 ? ": " + desc : "")}");
        }
        catch (Exception ex) { return (false, "No connection to Telegram: " + ex.Message); }
    }

    /// <summary>Send a test message to every chat now.</summary>
    public async Task<(bool Ok, string Message)> TestAsync()
    {
        var c = Config;
        string token = Token;
        if (token.Length == 0) return (false, "Enter the bot token first.");
        if (c.ChatIds.Count == 0) return (false, "Enter at least one chat ID.");
        foreach (var chat in c.ChatIds)
        {
            var (ok, err) = await SendAsync(token, chat, "✅ AFIF-PBX is connected. SMS and missed calls will be forwarded here.").ConfigureAwait(false);
            if (!ok)
            {
                string hint = err.Contains("chat not found") ? " — open your bot in Telegram and press Start first, then check the chat ID." : "";
                return (false, $"Chat {chat}: {err}{hint}");
            }
        }
        lock (_lock) { _lastSent = DateTime.UtcNow; _lastError = ""; }
        return (true, $"Sent to {c.ChatIds.Count} chat{(c.ChatIds.Count == 1 ? "" : "s")}.");
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            int backoff = 5;
            while (!_cts.IsCancellationRequested)
            {
                bool sentAll = true;
                try
                {
                    var c = Config;
                    string token = Token;
                    while (c.Enabled && token.Length > 0 && c.ChatIds.Count > 0)
                    {
                        string? next;
                        lock (_lock) next = _queue.FirstOrDefault();
                        if (next is null) break;
                        bool ok = true; string err = "";
                        foreach (var chat in c.ChatIds)
                        {
                            var r = await SendAsync(token, chat, next).ConfigureAwait(false);
                            if (!r.Ok) { ok = false; err = r.Error; }
                        }
                        if (!ok)
                        {
                            lock (_lock) _lastError = err;
                            _log.LogWarning("Telegram: {Error} (will retry)", err);
                            sentAll = false;
                            break;
                        }
                        lock (_lock) { _queue.Remove(next); _lastSent = DateTime.UtcNow; _lastError = ""; }
                        SaveQueue();
                    }
                }
                catch (Exception ex) { _log.LogWarning("Telegram forwarding failed: {Message}", ex.Message); sentAll = false; }
                backoff = sentAll ? 5 : Math.Min(300, backoff * 2);
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(sentAll ? 60 : backoff), _cts.Token).ConfigureAwait(false); } catch { break; }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
