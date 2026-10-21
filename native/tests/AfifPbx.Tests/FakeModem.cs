using System.Collections.Concurrent;
using System.Text;
using AfifPbx.Gsm;
using AfifPbx.Media;

namespace AfifPbx.Tests;

/// <summary>One direction of an in-memory byte pipe.</summary>
internal sealed class PipeStream : Stream
{
    private readonly BlockingCollection<byte[]> _queue = new();
    private byte[] _current = Array.Empty<byte>();
    private int _pos;
    private PipeStream? _peer;

    public static (PipeStream a, PipeStream b) CreatePair()
    {
        var a = new PipeStream(); var b = new PipeStream();
        a._peer = b; b._peer = a;
        return (a, b);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= _current.Length)
        {
            try { _current = _queue.Take(); } catch (InvalidOperationException) { return 0; }
            _pos = 0;
        }
        int n = Math.Min(count, _current.Length - _pos);
        Array.Copy(_current, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var copy = new byte[count];
        Array.Copy(buffer, offset, copy, 0, count);
        if (!_peer!._queue.IsAddingCompleted) _peer._queue.Add(copy);
    }

    public override void Flush() { }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set { } }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { _queue.CompleteAdding(); base.Dispose(disposing); }
}

/// <summary>
/// A simulated Huawei E303: answers the AT commands AFIF-PBX sends, can place/receive calls,
/// produces a test tone on the voice port during calls and records what it is sent.
/// </summary>
internal sealed class FakeModem : IModemPorts
{
    private readonly PipeStream _atPbx, _atModem, _audioPbx, _audioModem;
    private readonly Thread _thread;
    private volatile bool _inCall;
    private volatile bool _closed;
    private string? _pendingSms;

    public ConcurrentQueue<string> Commands { get; } = new();
    public long AudioBytesFromPbx;
    public string? DialedNumber;
    public bool AnswerOnDial { get; set; } = true;
    public List<string> SentSmsPdus { get; } = new();

    public FakeModem()
    {
        (_atPbx, _atModem) = PipeStream.CreatePair();
        (_audioPbx, _audioModem) = PipeStream.CreatePair();
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
        new Thread(AudioLoop) { IsBackground = true }.Start();
        new Thread(AudioSink) { IsBackground = true }.Start();
    }

    public Stream At => _atPbx;
    public Stream? Audio => _audioPbx;
    public string AtName => "FAKE-AT";
    public string AudioName => "FAKE-AUDIO";

    public void Send(string line)
    {
        var b = Encoding.ASCII.GetBytes("\r\n" + line + "\r\n");
        _atModem.Write(b, 0, b.Length);
    }

    public void Ring(string number)
    {
        Send("RING");
        Send($"+CLIP: \"{number}\",129,,,,0");
    }

    public void RemoteHangup()
    {
        _inCall = false;
        Send("^CEND:1,10,104,16");
    }

    public void DeliverSms(int index, string pdu)
    {
        _pendingSms = pdu;
        Send($"+CMTI: \"SM\",{index}");
    }

    private void Loop()
    {
        var sb = new StringBuilder();
        var buf = new byte[512];
        while (!_closed)
        {
            int n;
            try { n = _atModem.Read(buf, 0, buf.Length); } catch { return; }
            if (n <= 0) return;
            for (int i = 0; i < n; i++)
            {
                char c = (char)buf[i];
                if (c == '\u001a')
                {
                    SentSmsPdus.Add(sb.ToString());
                    sb.Clear();
                    Send("+CMGS: 7"); Send("OK");
                    continue;
                }
                if (c != '\r') { sb.Append(c); continue; }
                var cmd = sb.ToString().Trim(); sb.Clear();
                if (cmd.Length == 0) continue;
                Commands.Enqueue(cmd);
                Handle(cmd);
            }
        }
    }

    private void Handle(string cmd)
    {
        string u = cmd.ToUpperInvariant();
        if (u == "AT+CGMI") { Send("huawei"); Send("OK"); }
        else if (u == "AT+CGMM") { Send("E303"); Send("OK"); }
        else if (u == "AT+CGMR") { Send("21.157.00.00.00"); Send("OK"); }
        else if (u == "AT+CGSN") { Send("861234567890123"); Send("OK"); }
        else if (u == "AT+CIMI") { Send("416011234567890"); Send("OK"); }
        else if (u == "AT+CPIN?") { Send("+CPIN: READY"); Send("OK"); }
        else if (u == "AT^CVOICE?") { Send("^CVOICE:0,8000,16,20"); Send("OK"); }
        else if (u == "AT+CSQ") { Send("+CSQ: 21,99"); Send("OK"); }
        else if (u == "AT+CREG?") { Send("+CREG: 2,1,\"1A2B\",\"3C4D\""); Send("OK"); }
        else if (u == "AT+COPS?") { Send("+COPS: 0,0,\"Zain JO\",2"); Send("OK"); }
        else if (u == "AT+CNUM") { Send("+CNUM: \"\",\"+962790000000\",145"); Send("OK"); }
        else if (u.StartsWith("ATD"))
        {
            DialedNumber = cmd[3..].TrimEnd(';');
            Send("OK");
            if (AnswerOnDial)
            {
                Task.Run(async () =>
                {
                    Send("^ORIG:1,0");
                    await Task.Delay(200);
                    Send("^CONF:1");
                    await Task.Delay(400);
                    _inCall = true;
                    Send("^CONN:1,0");
                });
            }
        }
        else if (u == "ATA") { _inCall = true; Send("OK"); }
        else if (u == "AT+CHUP") { Send("OK"); if (_inCall) { _inCall = false; Send("^CEND:1,5,104,16"); } }
        else if (u.StartsWith("AT+CMGR="))
        {
            Send("+CMGR: 0,,24");
            Send(_pendingSms ?? "");
            Send("OK");
        }
        else if (u.StartsWith("AT+CMGS="))
        {
            var b = Encoding.ASCII.GetBytes("\r\n> ");
            _atModem.Write(b, 0, b.Length);
        }
        else Send("OK");
    }

    /// <summary>During a call the "network" sends a 600 Hz tone.</summary>
    private void AudioLoop()
    {
        var tone = Tones.Sine(600, 1000, 0.3);
        int pos = 0;
        while (!_closed)
        {
            Thread.Sleep(20);
            if (!_inCall) continue;
            var frame = new short[Media.Audio.FrameSamples];
            for (int i = 0; i < frame.Length; i++) frame[i] = tone[(pos + i) % tone.Length];
            pos += frame.Length;
            var bytes = Media.Audio.SamplesToBytes(frame);
            try { _audioModem.Write(bytes, 0, bytes.Length); } catch { return; }
        }
    }

    private void AudioSink()
    {
        var buf = new byte[4096];
        while (!_closed)
        {
            int n;
            try { n = _audioModem.Read(buf, 0, buf.Length); } catch { return; }
            if (n <= 0) return;
            Interlocked.Add(ref AudioBytesFromPbx, n);
        }
    }

    public bool SawCommand(string prefix) => Commands.Any(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _closed = true;
        _atPbx.Dispose(); _atModem.Dispose(); _audioPbx.Dispose(); _audioModem.Dispose();
    }
}
