using System.Collections.Concurrent;
using AfifPbx.Media;

namespace AfifPbx.Calls;

public enum LegState { Ringing, Answered, Ended }

/// <summary>
/// One side of a call: a SIP phone or the GSM line. Gives the call logic a common way to
/// answer, hang up, read/write audio and receive DTMF.
/// </summary>
public abstract class CallLeg : IAudioPort
{
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected readonly FrameQueue Inbound = new();

    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public abstract string Kind { get; }
    /// <summary>The other party's number (caller ID for inbound legs, dialled number for outbound ones).</summary>
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>For SIP legs: the extension this leg belongs to.</summary>
    public string Extension { get; set; } = "";
    public LegState State { get; private set; } = LegState.Ringing;
    public DateTime Created { get; } = DateTime.UtcNow;
    public DateTime? AnsweredAt { get; private set; }
    public ConcurrentQueue<char> Digits { get; } = new();

    public event Action<CallLeg, string>? Ended;
    public event Action<char>? Dtmf;

    /// <summary>Completes with the hang-up reason.</summary>
    public Task<string> EndedTask => _ended.Task;
    /// <summary>Completes true when answered, false if it ended first.</summary>
    public Task<bool> AnsweredTask => _answered.Task;
    public bool IsEnded => State == LegState.Ended;

    /// <summary>Answer an inbound leg (no-op for an already answered one).</summary>
    public abstract Task<bool> AnswerAsync();
    /// <summary>Tell an inbound caller that we are ringing the destination.</summary>
    public abstract void SendRinging();
    public abstract void Hangup(string reason = "normal");
    public virtual void SendDtmf(char digit) { }

    public bool TryRead(short[] frame) => Inbound.TryPop(frame);
    public abstract void Write(short[] frame);
    public void Flush() { Inbound.Clear(); while (Digits.TryDequeue(out _)) { } }

    protected void OnDigit(char d)
    {
        Digits.Enqueue(d);
        Dtmf?.Invoke(d);
    }

    protected void MarkAnswered()
    {
        if (State == LegState.Ended) return;
        State = LegState.Answered;
        AnsweredAt ??= DateTime.UtcNow;
        _answered.TrySetResult(true);
    }

    protected void MarkEnded(string reason)
    {
        if (State == LegState.Ended) return;
        State = LegState.Ended;
        _answered.TrySetResult(false);
        _ended.TrySetResult(reason);
        Ended?.Invoke(this, reason);
    }

    public override string ToString() => $"{Kind}:{(string.IsNullOrEmpty(Extension) ? Number : Extension)}[{Id}]";
}
