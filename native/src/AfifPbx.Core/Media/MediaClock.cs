using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AfifPbx.Media;

/// <summary>Something that moves audio once every 20 ms (a bridge, a prompt player, a recorder...).</summary>
public interface ITickable
{
    /// <summary>Called every 20 ms on the media thread. Return false to be removed.</summary>
    bool Tick();
}

/// <summary>Audio in/out of a call leg, in 20 ms frames of 160 samples.</summary>
public interface IAudioPort
{
    /// <summary>Next frame received from the remote side, if one is waiting.</summary>
    bool TryRead(short[] frame);
    /// <summary>Send a frame to the remote side.</summary>
    void Write(short[] frame);
    /// <summary>Drop anything buffered (e.g. before starting a prompt).</summary>
    void Flush();
}

/// <summary>
/// One high-precision 20 ms clock that drives every active media operation, so all audio
/// is paced the same way regardless of how many calls are up.
/// </summary>
public sealed class MediaClock : IDisposable
{
    private readonly List<ITickable> _items = new();
    private readonly object _lock = new();
    private readonly Thread _thread;
    private volatile bool _running = true;
    public long Ticks { get; private set; }
    public Action<Exception>? OnError { get; set; }

    public MediaClock()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) TimeBeginPeriod(1);
        _thread = new Thread(Run) { IsBackground = true, Name = "afif-media-clock", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Add(ITickable t) { lock (_lock) _items.Add(t); }
    public void Remove(ITickable t) { lock (_lock) _items.Remove(t); }
    public int Count { get { lock (_lock) return _items.Count; } }

    private void Run()
    {
        var sw = Stopwatch.StartNew();
        double next = 0;
        ITickable[] snapshot = Array.Empty<ITickable>();
        while (_running)
        {
            next += Audio.FrameMs;
            lock (_lock)
            {
                if (snapshot.Length != _items.Count) snapshot = new ITickable[_items.Count];
                _items.CopyTo(snapshot);
            }
            foreach (var t in snapshot)
            {
                bool keep;
                try { keep = t.Tick(); }
                catch (Exception ex) { keep = false; OnError?.Invoke(ex); }
                if (!keep) Remove(t);
            }
            Ticks++;
            double wait = next - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -200) next = sw.Elapsed.TotalMilliseconds; // fell far behind: resync
        }
    }

    public void Dispose()
    {
        _running = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) TimeEndPeriod(1);
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);
}

/// <summary>Bounded queue of received frames (absorbs network jitter, drops the oldest when full).</summary>
public sealed class FrameQueue
{
    private readonly Queue<short[]> _q = new();
    private readonly int _max;
    private short[] _partial = Array.Empty<short>();
    private readonly object _lock = new();

    public FrameQueue(int maxFrames = 12) => _max = maxFrames;

    /// <summary>Add samples of any length; they are re-cut into 160-sample frames.</summary>
    public void Push(ReadOnlySpan<short> samples)
    {
        lock (_lock)
        {
            var all = new short[_partial.Length + samples.Length];
            _partial.CopyTo(all, 0);
            samples.CopyTo(all.AsSpan(_partial.Length));
            int i = 0;
            for (; i + Audio.FrameSamples <= all.Length; i += Audio.FrameSamples)
            {
                var f = new short[Audio.FrameSamples];
                Array.Copy(all, i, f, 0, Audio.FrameSamples);
                _q.Enqueue(f);
                while (_q.Count > _max) _q.Dequeue();
            }
            _partial = all[i..];
        }
    }

    public bool TryPop(short[] frame)
    {
        lock (_lock)
        {
            if (_q.Count == 0) return false;
            _q.Dequeue().CopyTo(frame, 0);
            return true;
        }
    }

    public void Clear() { lock (_lock) { _q.Clear(); _partial = Array.Empty<short>(); } }
}
