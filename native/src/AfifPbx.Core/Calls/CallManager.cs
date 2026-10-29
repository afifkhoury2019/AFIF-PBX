using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AfifPbx.Data;
using AfifPbx.Gsm;
using AfifPbx.Media;
using AfifPbx.Sip;
using AfifPbx.Tts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AfifPbx.Calls;

/// <summary>A call in progress, as shown on the dashboard.</summary>
public sealed class ActiveCall
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Start { get; } = DateTime.UtcNow;
    public DateTime? Answer { get; set; }
    public string From { get; set; } = "";
    public string FromName { get; set; } = "";
    public string To { get; set; } = "";
    public string Direction { get; set; } = "internal";
    public string State { get; set; } = "ringing";
    public string Disposition { get; set; } = "NO ANSWER";
    public string Recording { get; set; } = "";
    public string ConnectedTo { get; set; } = "";
    internal readonly List<CallLeg> Legs = new();
}

/// <summary>Routes every call: extensions, ring groups, IVRs, voicemail, the GSM line, recording and the call log.</summary>
public sealed class CallManager
{
    private readonly ConfigStore _config;
    private readonly SipServer _sip;
    private readonly HuaweiModem? _modem;
    private readonly MediaOps _media;
    private readonly Database _db;
    private readonly PromptLibrary _prompts;
    private readonly string _dataDir;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, ActiveCall> _calls = new();

    public event Action? CallsChanged;
    public event Action<VoicemailMessage>? VoicemailSaved;

    public IReadOnlyCollection<ActiveCall> ActiveCalls => _calls.Values.ToList();
    /// <summary>A call finished and was written to the call log (Telegram uses it for missed GSM calls).</summary>
    public event Action<CallRecord>? CallLogged;
    public string RecordingsDir => Path.Combine(_dataDir, "Recordings");
    public string VoicemailDir => Path.Combine(_dataDir, "Voicemail");

    public CallManager(ConfigStore config, SipServer sip, HuaweiModem? modem, MediaOps media, Database db,
        PromptLibrary prompts, string dataDir, ILogger? log = null)
    {
        _config = config; _sip = sip; _modem = modem; _media = media; _db = db; _prompts = prompts; _dataDir = dataDir;
        _log = log ?? NullLogger.Instance;
        _sip.IncomingCall += (leg, dialed) => _ = HandleCallAsync(leg, dialed, null);
        if (_modem is not null)
        {
            _modem.IncomingCall += number =>
            {
                var leg = GsmLeg.Incoming(_modem, number);
                _ = HandleCallAsync(leg, "", _config.Current.Settings.GsmInboundDestination);
            };
        }
    }

    private PbxConfig Cfg => _config.Current;
    private Extension? Ext(string n) => Cfg.Extensions.FirstOrDefault(e => e.Number == n && e.Enabled);

    // ------------------------------------------------------------------ dialplan

    /// <summary>Turn a dialled number into a destination like "ext:100", "gsm:0791234567".</summary>
    public string Resolve(string dialed, CallLeg caller)
    {
        dialed = dialed.Trim();
        if (dialed == "*97") return "vmmenu:" + caller.Extension;
        if (dialed == "*43") return "echo:";
        if (Ext(dialed) is not null) return "ext:" + dialed;
        if (Cfg.RingGroups.Any(r => r.Number == dialed)) return "rg:" + dialed;
        if (Cfg.Ivrs.Any(i => i.Number == dialed)) return "ivr:" + dialed;
        if (dialed.StartsWith("*") && Ext(dialed[1..]) is not null) return "vm:" + dialed[1..]; // *100 = leave voicemail for 100
        var s = Cfg.Settings;
        if (caller.Kind == "sip")
        {
            string number = dialed;
            if (!string.IsNullOrEmpty(s.OutboundStripPrefix) && number.StartsWith(s.OutboundStripPrefix))
                number = number[s.OutboundStripPrefix.Length..];
            try
            {
                if (Regex.IsMatch(dialed, s.OutboundPattern)) return "gsm:" + number;
            }
            catch (ArgumentException) { if (Regex.IsMatch(dialed, @"^\+?\d{3,}$")) return "gsm:" + number; }
        }
        return "notfound:" + dialed;
    }

    private async Task HandleCallAsync(CallLeg caller, string dialed, string? destination)
    {
        var call = new ActiveCall
        {
            From = caller.Number,
            FromName = caller.Name,
            Direction = caller.Kind == "gsm" ? "inbound" : "internal",
        };
        call.Legs.Add(caller);
        try
        {
            destination ??= Resolve(dialed, caller);
            call.To = string.IsNullOrEmpty(dialed) ? destination.Split(':').Last() : dialed;
            if (destination.StartsWith("gsm:")) call.Direction = "outbound";
            _calls[call.Id] = call;
            CallsChanged?.Invoke();
            _log.LogInformation("Call {Id}: {From} → {Dest}", call.Id, caller, destination);
            await RunAsync(call, caller, destination, 0).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Call {Id} failed", call.Id);
            call.Disposition = "FAILED";
        }
        finally
        {
            foreach (var l in call.Legs.ToList()) if (!l.IsEnded) l.Hangup();
            _calls.TryRemove(call.Id, out _);
            var end = DateTime.UtcNow;
            int dur = call.Answer is null ? 0 : (int)(end - call.Answer.Value).TotalSeconds;
            var record = new CallRecord(0, call.Start, call.Answer, end, call.From, call.FromName,
                string.IsNullOrEmpty(call.ConnectedTo) ? call.To : $"{call.To} ({call.ConnectedTo})",
                call.Direction, call.Disposition, dur, call.Recording);
            try { _db.AddCall(record); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not write call log"); }
            try { CallLogged?.Invoke(record); } catch (Exception ex) { _log.LogWarning(ex, "Call log listener failed"); }
            CallsChanged?.Invoke();
        }
    }

    private async Task RunAsync(ActiveCall call, CallLeg caller, string destination, int depth)
    {
        while (!caller.IsEnded && depth++ < 10)
        {
            var parts = destination.Split(':', 2);
            string type = parts[0], arg = parts.Length > 1 ? parts[1] : "";
            string? next;
            switch (type)
            {
                case "ext":
                    {
                        var ext = Ext(arg);
                        if (ext is null) { next = "hangup:"; break; }
                        bool handled = await DialExtensionsAsync(call, caller, new[] { arg }, Cfg.Settings.RingSeconds, ringAll: true).ConfigureAwait(false);
                        if (handled) return;
                        next = !string.IsNullOrWhiteSpace(ext.NoAnswerDestination) ? ext.NoAnswerDestination
                             : ext.VoicemailEnabled ? "vm:" + ext.Number : "hangup:";
                        break;
                    }
                case "rg":
                    {
                        var rg = Cfg.RingGroups.FirstOrDefault(r => r.Number == arg);
                        if (rg is null) { next = "hangup:"; break; }
                        bool handled = await DialExtensionsAsync(call, caller, rg.Members, rg.RingSeconds,
                            ringAll: !string.Equals(rg.Strategy, "hunt", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
                        if (handled) return;
                        next = rg.FailoverDestination;
                        break;
                    }
                case "ivr":
                    {
                        var ivr = Cfg.Ivrs.FirstOrDefault(i => i.Number == arg);
                        next = ivr is null ? "hangup:" : await RunIvrAsync(call, caller, ivr).ConfigureAwait(false);
                        break;
                    }
                case "vm":
                    await RunVoicemailAsync(call, caller, arg).ConfigureAwait(false);
                    return;
                case "vmmenu":
                    await RunVoicemailMenuAsync(call, caller, arg).ConfigureAwait(false);
                    return;
                case "gsm":
                    await DialGsmAsync(call, caller, arg).ConfigureAwait(false);
                    return;
                case "echo":
                    await RunEchoAsync(call, caller).ConfigureAwait(false);
                    return;
                case "notfound":
                    if (caller.State == LegState.Answered)
                    {
                        await _media.PlayAsync(caller, _prompts.Say("The number you dialed is not available.")).ConfigureAwait(false);
                        caller.Hangup();
                    }
                    else caller.Hangup("notfound");
                    call.Disposition = "FAILED";
                    return;
                default: // hangup
                    if (caller.State == LegState.Answered)
                        await _media.PlayAsync(caller, _prompts.Say("Goodbye.")).ConfigureAwait(false);
                    caller.Hangup("unavailable");
                    return;
            }
            destination = string.IsNullOrWhiteSpace(next) ? "hangup:" : next;
            if (!destination.Contains(':')) destination += ":";
        }
    }

    // ------------------------------------------------------------------ ringing phones

    /// <summary>Ring extensions. Returns true when the call was dealt with (connected, or the caller hung up).</summary>
    private async Task<bool> DialExtensionsAsync(ActiveCall call, CallLeg caller, IEnumerable<string> extensions, int ringSeconds, bool ringAll)
    {
        var targets = extensions.Where(e => e != caller.Extension && Ext(e) is not null).Distinct().ToList();
        caller.SendRinging();
        if (ringAll)
        {
            var legs = targets.SelectMany(e => _sip.Ring(e, caller.Number, caller.Name, ringSeconds)).Cast<CallLeg>().ToList();
            return await RingAndConnectAsync(call, caller, legs, ringSeconds).ConfigureAwait(false);
        }
        foreach (var ext in targets)
        {
            var legs = _sip.Ring(ext, caller.Number, caller.Name, ringSeconds).Cast<CallLeg>().ToList();
            if (legs.Count == 0) continue;
            if (await RingAndConnectAsync(call, caller, legs, ringSeconds).ConfigureAwait(false)) return true;
            if (caller.IsEnded) return true;
        }
        return caller.IsEnded;
    }

    private async Task<bool> RingAndConnectAsync(ActiveCall call, CallLeg caller, List<CallLeg> legs, int ringSeconds)
    {
        if (legs.Count == 0) return caller.IsEnded;
        lock (call.Legs) call.Legs.AddRange(legs);
        // The first phone to answer wins. "Nobody answered" is only decided once every phone has given up —
        // deciding it from a separate "all finished" task raced with the answer and hung up the phone that had
        // just picked up (the caller then went to voicemail).
        var winner = new TaskCompletionSource<CallLeg?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int ringing = legs.Count;
        foreach (var leg in legs)
        {
            var l = leg;
            _ = l.AnsweredTask.ContinueWith(t =>
            {
                if (t.Result)
                {
                    if (!winner.TrySetResult(l)) l.Hangup(); // someone else already answered, or we stopped waiting
                }
                else if (Interlocked.Decrement(ref ringing) == 0) winner.TrySetResult(null);
            }, TaskScheduler.Default);
        }
        await Task.WhenAny(winner.Task, caller.EndedTask, Task.Delay((ringSeconds + 2) * 1000)).ConfigureAwait(false);
        winner.TrySetResult(null); // stop accepting answers (timeout / caller gone)
        var answered = winner.Task.Result;

        foreach (var l in legs.Where(l => l != answered && !l.IsEnded))
        {
            if (l is SipLeg s) s.Cancel(); else l.Hangup();
        }
        if (answered is null) return caller.IsEnded;
        if (caller.IsEnded) { answered.Hangup(); return true; }

        if (!await caller.AnswerAsync().ConfigureAwait(false)) { answered.Hangup(); return true; }
        await ConnectAsync(call, caller, answered).ConfigureAwait(false);
        return true;
    }

    private async Task ConnectAsync(ActiveCall call, CallLeg a, CallLeg b)
    {
        call.Answer ??= DateTime.UtcNow;
        call.State = "connected";
        call.Disposition = "ANSWERED";
        call.ConnectedTo = string.IsNullOrEmpty(b.Extension) ? b.Number : b.Extension;
        CallsChanged?.Invoke();

        WavRecorder? rec = null;
        if (ShouldRecord(a, b))
        {
            try
            {
                var dir = Path.Combine(RecordingsDir, DateTime.Now.ToString("yyyy-MM"));
                var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{Safe(call.From)}_{Safe(call.ConnectedTo)}.wav";
                rec = new WavRecorder(Path.Combine(dir, name));
                call.Recording = Path.GetRelativePath(RecordingsDir, rec.Path).Replace('\\', '/');
            }
            catch (Exception ex) { _log.LogWarning("Could not start recording: {Message}", ex.Message); }
        }
        _log.LogInformation("Call {Id}: connected {A} ⇄ {B}", call.Id, a, b);
        try { await _media.BridgeAsync(a, b, rec).ConfigureAwait(false); }
        finally
        {
            rec?.Dispose();
            _log.LogInformation("Call {Id}: bridge ended ({A}: {Ra}; {B}: {Rb})", call.Id, a,
                a.IsEnded ? a.EndedTask.Result : "up", b, b.IsEnded ? b.EndedTask.Result : "up");
            if (!a.IsEnded) a.Hangup();
            if (!b.IsEnded) b.Hangup();
        }
    }

    private bool ShouldRecord(CallLeg a, CallLeg b)
    {
        bool gsm = a.Kind == "gsm" || b.Kind == "gsm";
        if (gsm && Cfg.Settings.RecordGsmCalls) return true;
        return new[] { a.Extension, b.Extension }.Any(e => !string.IsNullOrEmpty(e) && Ext(e)?.RecordCalls == true);
    }

    private static string Safe(string s) => string.Concat((s ?? "").Select(c => char.IsLetterOrDigit(c) || c == '+' ? c : '-'));

    // ------------------------------------------------------------------ GSM out

    private async Task DialGsmAsync(ActiveCall call, CallLeg caller, string number)
    {
        if (_modem is null || !_modem.IsReady)
        {
            await SayAndHangup(caller, "The mobile line is not available at the moment.").ConfigureAwait(false);
            call.Disposition = "FAILED";
            return;
        }
        if (_modem.InCall)
        {
            await SayAndHangup(caller, "The mobile line is busy. Please try again later.").ConfigureAwait(false);
            call.Disposition = "BUSY";
            return;
        }
        caller.SendRinging();
        var gsm = GsmLeg.Dial(_modem, number);
        lock (call.Legs) call.Legs.Add(gsm);
        await Task.WhenAny(gsm.AnsweredTask, caller.EndedTask, Task.Delay(75_000)).ConfigureAwait(false);
        if (caller.IsEnded) { gsm.Hangup(); return; }
        if (gsm.State != LegState.Answered)
        {
            string reason = gsm.IsEnded ? gsm.EndedTask.Result : "no answer";
            gsm.Hangup();
            call.Disposition = reason.Contains("busy") ? "BUSY" : "NO ANSWER";
            caller.Hangup(reason.Contains("busy") ? "busy" : "unavailable");
            return;
        }
        if (!await caller.AnswerAsync().ConfigureAwait(false)) { gsm.Hangup(); return; }
        await ConnectAsync(call, caller, gsm).ConfigureAwait(false);
    }

    private async Task SayAndHangup(CallLeg caller, string text)
    {
        if (await caller.AnswerAsync().ConfigureAwait(false))
        {
            await _media.PauseAsync(caller, 300, false).ConfigureAwait(false);
            await _media.PlayAsync(caller, _prompts.Say(text)).ConfigureAwait(false);
        }
        caller.Hangup("unavailable");
    }

    // ------------------------------------------------------------------ IVR

    private async Task<string> RunIvrAsync(ActiveCall call, CallLeg caller, Ivr ivr)
    {
        if (!await caller.AnswerAsync().ConfigureAwait(false)) return "hangup:";
        call.Answer ??= DateTime.UtcNow;
        call.State = "ivr " + ivr.Name;
        call.Disposition = "ANSWERED";
        CallsChanged?.Invoke();
        caller.Flush();
        await _media.PauseAsync(caller, 400, false).ConfigureAwait(false);
        var prompt = _prompts.FileOrSay(ivr.PromptFile, string.IsNullOrWhiteSpace(ivr.PromptText) ? "Please choose an option." : ivr.PromptText);

        for (int attempt = 0; attempt < 3 && !caller.IsEnded; attempt++)
        {
            await _media.PlayAsync(caller, prompt, interruptible: true).ConfigureAwait(false);
            if (caller.IsEnded) break;
            var first = await _media.CollectDigitsAsync(caller, 1, TimeSpan.FromSeconds(Math.Max(2, ivr.TimeoutSeconds)), TimeSpan.Zero).ConfigureAwait(false);
            if (first.Length == 0) continue; // timeout → repeat menu

            string digits = first;
            // allow longer input when the digit could start an extension/group number
            bool couldBeLonger = ivr.DirectDial && InternalNumbers().Any(n => n.Length > 1 && n.StartsWith(digits));
            if (couldBeLonger)
            {
                var more = await _media.CollectDigitsAsync(caller, 6, TimeSpan.FromMilliseconds(2500), TimeSpan.FromMilliseconds(2500)).ConfigureAwait(false);
                digits += more;
            }
            if (ivr.Options.TryGetValue(digits, out var dest)) return dest;
            if (ivr.DirectDial)
            {
                if (Ext(digits) is not null) return "ext:" + digits;
                if (Cfg.RingGroups.Any(r => r.Number == digits)) return "rg:" + digits;
            }
            await _media.PlayAsync(caller, _prompts.Say("Sorry, that is not a valid option.")).ConfigureAwait(false);
        }
        return ivr.TimeoutDestination;
    }

    private IEnumerable<string> InternalNumbers() =>
        Cfg.Extensions.Select(e => e.Number).Concat(Cfg.RingGroups.Select(r => r.Number));

    // ------------------------------------------------------------------ voicemail

    private async Task RunVoicemailAsync(ActiveCall call, CallLeg caller, string mailbox)
    {
        var ext = Ext(mailbox);
        if (ext is null || !ext.VoicemailEnabled) { caller.Hangup("unavailable"); return; }
        if (!await caller.AnswerAsync().ConfigureAwait(false)) return;
        call.Answer ??= DateTime.UtcNow;
        call.State = "voicemail " + mailbox;
        call.Disposition = "VOICEMAIL";
        CallsChanged?.Invoke();

        await _media.PauseAsync(caller, 400, false).ConfigureAwait(false);
        string spoken = string.Join(" ", mailbox.ToCharArray());
        var greeting = _prompts.FileOrSay(ext.GreetingFile,
            $"The person at extension {spoken}{(string.IsNullOrWhiteSpace(ext.Name) ? "" : ", " + ext.Name + ",")} is not available. " +
            "Please leave your message after the tone. When you have finished, hang up, or press the pound key.");
        await _media.PlayAsync(caller, greeting, interruptible: false).ConfigureAwait(false);
        caller.Flush();
        await _media.PlayAsync(caller, Tones.Beep()).ConfigureAwait(false);
        if (caller.IsEnded) return;

        var samples = await _media.RecordAsync(caller, Cfg.Settings.VoicemailMaxSeconds).ConfigureAwait(false);
        samples = TrimSilence(samples);
        int seconds = samples.Length / Audio.SampleRate;
        if (seconds >= 1)
        {
            var dir = Path.Combine(VoicemailDir, mailbox);
            var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Safe(caller.Number)}.wav");
            Wav.Save(file, samples);
            var rel = Path.GetRelativePath(VoicemailDir, file).Replace('\\', '/');
            long id = _db.AddVoicemail(mailbox, string.IsNullOrEmpty(caller.Name) || caller.Name == caller.Number ? caller.Number : $"{caller.Name} <{caller.Number}>", seconds, rel);
            _log.LogInformation("Voicemail for {Mailbox} from {Caller} ({Seconds}s)", mailbox, caller.Number, seconds);
            var msg = _db.Voicemail(id);
            if (msg is not null) VoicemailSaved?.Invoke(msg);
        }
        if (!caller.IsEnded)
        {
            await _media.PlayAsync(caller, _prompts.Say("Your message has been saved. Goodbye.")).ConfigureAwait(false);
            caller.Hangup();
        }
    }

    private static short[] TrimSilence(short[] s)
    {
        int end = s.Length;
        while (end > 0)
        {
            int start = Math.Max(0, end - Audio.FrameSamples);
            if (Audio.Rms(s.AsSpan(start, end - start)) > 150) break;
            end = start;
        }
        return s[..end];
    }

    private async Task RunVoicemailMenuAsync(ActiveCall call, CallLeg caller, string mailbox)
    {
        if (string.IsNullOrEmpty(mailbox) || Ext(mailbox) is null) { caller.Hangup("notfound"); return; }
        if (!await caller.AnswerAsync().ConfigureAwait(false)) return;
        call.Answer ??= DateTime.UtcNow;
        call.State = "voicemail menu";
        call.Disposition = "ANSWERED";
        CallsChanged?.Invoke();
        await _media.PauseAsync(caller, 400, false).ConfigureAwait(false);

        var messages = _db.Voicemails(mailbox).Where(m => !m.Heard).OrderBy(m => m.Id).ToList();
        string intro = messages.Count switch
        {
            0 => "You have no new messages.",
            1 => "You have one new message.",
            _ => $"You have {messages.Count} new messages.",
        };
        await _media.PlayAsync(caller, _prompts.Say(intro)).ConfigureAwait(false);
        int i = 0;
        foreach (var m in messages)
        {
            if (caller.IsEnded) return;
            i++;
            var from = new string(m.Caller.Where(char.IsDigit).ToArray());
            await _media.PlayAsync(caller, _prompts.Say($"Message {i}, from {(from.Length > 0 ? string.Join(" ", from.ToCharArray()) : "an unknown caller")}."), true).ConfigureAwait(false);
            caller.Flush();
            var path = Path.Combine(VoicemailDir, m.File);
            if (File.Exists(path)) await _media.PlayAsync(caller, Wav.Load(path), true).ConfigureAwait(false);
            _db.MarkVoicemailHeard(m.Id);
            caller.Flush();
            await _media.PlayAsync(caller, _prompts.Say("Press 7 to delete this message, or any other key for the next one."), true).ConfigureAwait(false);
            var d = await _media.CollectDigitsAsync(caller, 1, TimeSpan.FromSeconds(4), TimeSpan.Zero).ConfigureAwait(false);
            if (d == "7")
            {
                DeleteVoicemail(m.Id);
                await _media.PlayAsync(caller, _prompts.Say("Deleted.")).ConfigureAwait(false);
            }
        }
        if (!caller.IsEnded)
        {
            if (messages.Count > 0) await _media.PlayAsync(caller, _prompts.Say("No more messages. Goodbye.")).ConfigureAwait(false);
            caller.Hangup();
        }
    }

    public void DeleteVoicemail(long id)
    {
        var m = _db.Voicemail(id);
        if (m is null) return;
        try { File.Delete(Path.Combine(VoicemailDir, m.File)); } catch { }
        _db.DeleteVoicemail(id);
    }

    private async Task RunEchoAsync(ActiveCall call, CallLeg caller)
    {
        if (!await caller.AnswerAsync().ConfigureAwait(false)) return;
        call.Answer ??= DateTime.UtcNow;
        call.State = "echo test";
        call.Disposition = "ANSWERED";
        CallsChanged?.Invoke();
        await _media.PauseAsync(caller, 300, false).ConfigureAwait(false);
        await _media.PlayAsync(caller, _prompts.Say("Echo test. Speak, and you will hear yourself. Hang up when you are done.")).ConfigureAwait(false);
        caller.Flush();
        await _media.EchoAsync(caller, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        caller.Hangup();
    }

    /// <summary>Hang up an active call from the dashboard.</summary>
    public bool HangupCall(string id)
    {
        if (!_calls.TryGetValue(id, out var call)) return false;
        foreach (var l in call.Legs.ToList()) l.Hangup();
        return true;
    }
}
