using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AfifPbx.Gsm;

/// <summary>The two serial channels of a GSM dongle: AT commands and raw voice audio.</summary>
public interface IModemPorts : IDisposable
{
    Stream At { get; }
    Stream? Audio { get; }
    string AtName { get; }
    string AudioName { get; }
}

/// <summary>Real COM ports (Windows names like COM5, or /dev/ttyUSB2 elsewhere).</summary>
public sealed class SerialModemPorts : IModemPorts
{
    private readonly SerialPort _at;
    private readonly SerialPort? _audio;

    public SerialModemPorts(string atPort, string audioPort)
    {
        _at = Open(atPort);
        try { _audio = string.IsNullOrEmpty(audioPort) ? null : Open(audioPort); }
        catch { _at.Dispose(); throw; }
        AtName = atPort; AudioName = audioPort;
    }

    private static SerialPort Open(string name)
    {
        var p = new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 64 * 1024,
        };
        p.Open();
        return p;
    }

    public Stream At => _at.BaseStream;
    public Stream? Audio => _audio?.BaseStream;
    public string AtName { get; }
    public string AudioName { get; }

    public void Dispose()
    {
        try { _at.Dispose(); } catch { }
        try { _audio?.Dispose(); } catch { }
    }
}

public sealed record AtResult(bool Ok, string Final, IReadOnlyList<string> Lines)
{
    public string? Line(string prefix) => Lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>
/// Sends AT commands one at a time and splits what the modem says into command responses and
/// unsolicited notifications (RING, +CLIP, ^CEND, +CMTI ...).
/// </summary>
public sealed class AtChannel : IDisposable
{
    private static readonly string[] Finals = { "OK", "ERROR", "NO CARRIER", "BUSY", "NO ANSWER", "NO DIALTONE", "COMMAND NOT SUPPORT", "TOO MANY PARAMETERS" };
    private static readonly string[] UnsolicitedPrefixes =
    {
        "RING", "+CRING", "+CLIP", "+CMTI", "+CDSI", "+CUSD", "+CCWA", "+CSSI", "+CSSU", "+CDS:", "+CMT:",
        "^RSSI", "^BOOT", "^MODE", "^SRVST", "^SIMST", "^ORIG", "^CONF", "^CONN", "^CEND", "^DSFLOWRPT",
        "^SMMEMFULL", "^STIN", "^HCSQ", "^SYSSTART", "^NWTIME", "^RFSWITCH", "^ACTIVEBAND", "^EONS", "^CPIN", "^DTMF",
    };

    private readonly Stream _stream;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Thread _reader;
    private readonly StringBuilder _line = new();
    private readonly object _lock = new();
    private Pending? _pending;
    private volatile bool _closed;

    public event Action<string>? Unsolicited;
    public event Action<Exception>? Failed;

    private sealed class Pending
    {
        public string Prefix = "";
        public bool AcceptAll;
        public bool AcceptHex;
        public bool WantPrompt;
        public readonly List<string> Lines = new();
        public readonly TaskCompletionSource<AtResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Prompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public AtChannel(Stream stream, ILogger? log = null)
    {
        _stream = stream;
        _log = log ?? NullLogger.Instance;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "afif-gsm-at" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[1024];
        try
        {
            while (!_closed)
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n <= 0) { if (_closed) break; Thread.Sleep(5); continue; }
                for (int i = 0; i < n; i++) Feed((char)buf[i]);
            }
        }
        catch (Exception ex) when (!_closed)
        {
            _log.LogWarning("Modem AT port closed: {Message}", ex.Message);
            Failed?.Invoke(ex);
            lock (_lock) _pending?.Done.TrySetResult(new AtResult(false, "PORT CLOSED", Array.Empty<string>()));
        }
        catch { }
    }

    private void Feed(char c)
    {
        if (c == '\r' || c == '\n')
        {
            if (_line.Length > 0) { var l = _line.ToString(); _line.Clear(); OnLine(l); }
            return;
        }
        _line.Append(c);
        if (_line.Length == 2 && _line[0] == '>' && _line[1] == ' ')
        {
            _line.Clear();
            lock (_lock) _pending?.Prompt.TrySetResult(true);
        }
    }

    private void OnLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        _log.LogDebug("AT < {Line}", line);
        Pending? p;
        lock (_lock) p = _pending;

        if (p is not null)
        {
            if (Finals.Contains(line) || line.StartsWith("+CME ERROR") || line.StartsWith("+CMS ERROR"))
            {
                // NO CARRIER/BUSY are only finals for a pending ATD; otherwise they are call events
                bool callEvent = line is "NO CARRIER" or "BUSY" or "NO ANSWER" && !p.Prefix.StartsWith("D");
                if (!callEvent)
                {
                    lock (_lock) _pending = null;
                    p.Done.TrySetResult(new AtResult(line == "OK", line, p.Lines.ToList()));
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(p.Prefix) && line.StartsWith(p.Prefix, StringComparison.Ordinal))
            {
                p.Lines.Add(line); return;
            }
            else if (p.AcceptHex && IsHex(line))
            {
                p.Lines.Add(line); return;
            }
            else if (p.AcceptAll && !IsUnsolicited(line))
            {
                p.Lines.Add(line); return;
            }
        }
        if (line == "OK") return; // stray OK
        Unsolicited?.Invoke(line);
    }

    private static bool IsHex(string s) => s.Length >= 8 && s.All(Uri.IsHexDigit);

    private static bool IsUnsolicited(string line) =>
        UnsolicitedPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)) ||
        line is "NO CARRIER" or "BUSY" or "NO ANSWER";

    /// <summary>Send a command (without the trailing CR) and wait for OK/ERROR.</summary>
    public async Task<AtResult> SendAsync(string command, int timeoutMs = 8000, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var p = NewPending(command);
            lock (_lock) _pending = p;
            await WriteAsync(command + "\r").ConfigureAwait(false);
            return await WaitAsync(p, timeoutMs, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Send a command that answers with a "> " prompt (AT+CMGS), then the payload + Ctrl-Z.</summary>
    public async Task<AtResult> SendWithPromptAsync(string command, string payload, int timeoutMs = 30000)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var p = NewPending(command);
            p.WantPrompt = true;
            lock (_lock) _pending = p;
            await WriteAsync(command + "\r").ConfigureAwait(false);
            var first = await Task.WhenAny(p.Prompt.Task, p.Done.Task, Task.Delay(5000)).ConfigureAwait(false);
            if (first != p.Prompt.Task)
            {
                await WriteAsync("\u001b").ConfigureAwait(false); // ESC aborts
                lock (_lock) _pending = null;
                return p.Done.Task.IsCompleted ? p.Done.Task.Result : new AtResult(false, "NO PROMPT", p.Lines.ToList());
            }
            await WriteAsync(payload + "\u001a").ConfigureAwait(false);
            return await WaitAsync(p, timeoutMs, default).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static Pending NewPending(string command)
    {
        var p = new Pending();
        string c = command.StartsWith("AT", StringComparison.OrdinalIgnoreCase) ? command[2..] : command;
        // "+CSQ" from "AT+CSQ", "^CVOICE" from "AT^CVOICE?", "D" for dial
        int end = c.IndexOfAny(new[] { '=', '?' });
        string name = end >= 0 ? c[..end] : c;
        if (name.StartsWith('+') || name.StartsWith('^')) p.Prefix = name + ":";
        else if (name.StartsWith('D')) p.Prefix = "D";
        if (name is "+CGSN" or "+CIMI" or "+CGMI" or "+CGMM" or "+CGMR" or "+GSN" or "I") { p.AcceptAll = true; p.Prefix = ""; }
        if (name is "+CMGR" or "+CMGL") p.AcceptHex = true;
        return p;
    }

    private async Task<AtResult> WaitAsync(Pending p, int timeoutMs, CancellationToken ct)
    {
        var done = await Task.WhenAny(p.Done.Task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
        if (done == p.Done.Task) return p.Done.Task.Result;
        lock (_lock) if (_pending == p) _pending = null;
        return new AtResult(false, "TIMEOUT", p.Lines.ToList());
    }

    private async Task WriteAsync(string s)
    {
        _log.LogDebug("AT > {Cmd}", s.TrimEnd('\r'));
        var bytes = Encoding.ASCII.GetBytes(s);
        await _stream.WriteAsync(bytes).ConfigureAwait(false);
        await _stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _closed = true;
        lock (_lock) _pending?.Done.TrySetResult(new AtResult(false, "CLOSED", Array.Empty<string>()));
    }
}
