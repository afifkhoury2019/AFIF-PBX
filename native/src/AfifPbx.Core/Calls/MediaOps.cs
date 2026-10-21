using AfifPbx.Media;

namespace AfifPbx.Calls;

/// <summary>Plays prompts, records, collects digits and bridges legs, all paced by the media clock.</summary>
public sealed class MediaOps
{
    private readonly MediaClock _clock;
    public MediaOps(MediaClock clock) => _clock = clock;

    private sealed class Ticker : ITickable
    {
        private readonly Func<bool> _tick;
        public Ticker(Func<bool> tick) => _tick = tick;
        public bool Tick() => _tick();
    }

    /// <summary>
    /// Play audio to a leg. Returns true if it played to the end; false if the caller pressed a key
    /// (when <paramref name="interruptible"/>) or hung up.
    /// </summary>
    public Task<bool> PlayAsync(CallLeg leg, short[] samples, bool interruptible = false, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int pos = 0;
        var scratch = Audio.NewFrame();
        _clock.Add(new Ticker(() =>
        {
            if (leg.IsEnded || ct.IsCancellationRequested) { tcs.TrySetResult(false); return false; }
            if (interruptible && !leg.Digits.IsEmpty) { tcs.TrySetResult(false); return false; }
            while (leg.TryRead(scratch)) { } // discard caller audio while prompting (DTMF still detected)
            if (pos >= samples.Length) { tcs.TrySetResult(true); return false; }
            var frame = Audio.NewFrame();
            Array.Copy(samples, pos, frame, 0, Math.Min(Audio.FrameSamples, samples.Length - pos));
            leg.Write(frame);
            pos += Audio.FrameSamples;
            return true;
        }));
        return tcs.Task;
    }

    /// <summary>Send silence for a while (keeps the audio stream flowing while we wait).</summary>
    public Task<bool> PauseAsync(CallLeg leg, int ms, bool interruptible = true, CancellationToken ct = default) =>
        PlayAsync(leg, Audio.Silence(ms), interruptible, ct);

    /// <summary>Record what the caller says, until hang-up, '#', or the time limit.</summary>
    public Task<short[]> RecordAsync(CallLeg leg, int maxSeconds, bool stopOnHash = true)
    {
        var tcs = new TaskCompletionSource<short[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new List<short>(Audio.SampleRate * 30);
        int maxSamples = maxSeconds * Audio.SampleRate;
        var frame = Audio.NewFrame();
        var silence = Audio.NewFrame();
        _clock.Add(new Ticker(() =>
        {
            if (leg.IsEnded) { tcs.TrySetResult(samples.ToArray()); return false; }
            if (stopOnHash && leg.Digits.TryPeek(out var d) && d == '#')
            {
                leg.Digits.TryDequeue(out _);
                tcs.TrySetResult(samples.ToArray());
                return false;
            }
            bool got = leg.TryRead(frame);
            samples.AddRange(got ? frame : silence);
            while (leg.TryRead(frame)) samples.AddRange(frame); // catch up on bursts
            leg.Write(silence);
            if (samples.Count >= maxSamples) { tcs.TrySetResult(samples.ToArray()); return false; }
            return true;
        }));
        return tcs.Task;
    }

    /// <summary>
    /// Wait for keypresses. Returns the digits ("" on timeout). Stops early at <paramref name="maxDigits"/>,
    /// at '#', or when the gap between digits exceeds <paramref name="interDigit"/>.
    /// </summary>
    public async Task<string> CollectDigitsAsync(CallLeg leg, int maxDigits, TimeSpan firstDigit, TimeSpan interDigit)
    {
        var digits = "";
        var deadline = DateTime.UtcNow + firstDigit;
        using var stopSilence = new CancellationTokenSource();
        _ = PauseAsync(leg, (int)(firstDigit + interDigit * maxDigits).TotalMilliseconds + 500, interruptible: false, stopSilence.Token);
        try
        {
            while (!leg.IsEnded && DateTime.UtcNow < deadline)
            {
                if (leg.Digits.TryDequeue(out var d))
                {
                    if (d == '#') break;
                    digits += d;
                    if (digits.Length >= maxDigits) break;
                    deadline = DateTime.UtcNow + interDigit;
                    continue;
                }
                await Task.Delay(40).ConfigureAwait(false);
            }
        }
        finally { stopSilence.Cancel(); }
        return digits;
    }

    /// <summary>
    /// Connect two answered legs (and optionally record them). Completes when either side hangs up.
    /// </summary>
    public async Task BridgeAsync(CallLeg a, CallLeg b, WavRecorder? recorder)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fa = Audio.NewFrame();
        var fb = Audio.NewFrame();
        a.Flush(); b.Flush();
        _clock.Add(new Ticker(() =>
        {
            if (a.IsEnded || b.IsEnded) { done.TrySetResult(); return false; }
            if (!a.TryRead(fa)) Array.Clear(fa);
            if (!b.TryRead(fb)) Array.Clear(fb);
            a.Write(fb);
            b.Write(fa);
            recorder?.Write(fa, fb);
            // keypad from a phone travels as RFC 2833 events, so pass it on; GSM DTMF is already in the audio
            if (a.Kind == "sip") while (a.Digits.TryDequeue(out var d)) b.SendDtmf(d);
            if (b.Kind == "sip") while (b.Digits.TryDequeue(out var d)) a.SendDtmf(d);
            return true;
        }));
        await Task.WhenAny(a.EndedTask, b.EndedTask, done.Task).ConfigureAwait(false);
        done.TrySetResult();
    }

    /// <summary>Echo test: the caller hears themselves (useful to check audio both ways).</summary>
    public Task EchoAsync(CallLeg leg, TimeSpan max)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var until = DateTime.UtcNow + max;
        var f = Audio.NewFrame();
        _clock.Add(new Ticker(() =>
        {
            if (leg.IsEnded || DateTime.UtcNow > until) { tcs.TrySetResult(); return false; }
            if (leg.TryRead(f)) leg.Write(f); else leg.Write(Audio.NewFrame());
            return true;
        }));
        return tcs.Task;
    }
}
