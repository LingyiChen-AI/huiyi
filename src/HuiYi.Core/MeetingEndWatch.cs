namespace HuiYi;

/// Decides when a meeting is over, so one nobody ended doesn't run on for hours. Two signals: a
/// meeting app that was using the microphone lets go of it (hanging up), or nothing was recognised
/// for a while. While a meeting app is still in a call, silence doesn't count. Pure; ported from
/// Cicada's MeetingEnd.swift.
public sealed class MeetingEndWatch
{
    public enum Silence { Prompt, End, Off }

    public sealed record Config
    {
        public bool FollowApps { get; init; } = true;
        public Silence Silence { get; init; } = Silence.Prompt;
        public TimeSpan SilenceAfter { get; init; } = TimeSpan.FromMinutes(10);
        public TimeSpan Countdown { get; init; } = TimeSpan.FromSeconds(60);
        public TimeSpan AppGrace { get; init; } = TimeSpan.FromSeconds(30);
    }

    public abstract record Reason
    {
        public sealed record AppLeft(string Name) : Reason;
        public sealed record SilenceReason : Reason;
        public sealed record NobodySpoke : Reason;
    }

    public sealed record Pending(Reason Reason, DateTime Deadline);

    public Config Settings { get; set; }
    public Pending? Waiting { get; private set; }
    public DateTime LastSpeech { get; private set; }
    public IReadOnlyDictionary<string, string> InCall => inCall;
    private Dictionary<string, string> inCall = new();

    public MeetingEndWatch(Config config, DateTime start)
    {
        Settings = config;
        LastSpeech = start;
    }

    public void Heard(DateTime now)
    {
        LastSpeech = now;
        if (Waiting?.Reason is Reason.SilenceReason) Waiting = null;
    }

    public void KeepGoing(DateTime now)
    {
        Waiting = null;
        LastSpeech = now;
        inCall = new();
    }

    /// `calls`: meeting apps using a microphone now (id → name). `said`: the meeting has lines.
    public Reason? Tick(DateTime now, IReadOnlyDictionary<string, string> calls, bool said)
    {
        var inACall = Settings.FollowApps && calls.Count > 0;
        if (!said)
        {
            inCall = Settings.FollowApps ? new(calls) : new();
            Waiting = null;
            if (Settings.Silence != Silence.Off && !inACall && now - LastSpeech >= Settings.SilenceAfter)
                return new Reason.NobodySpoke();
            return null;
        }
        if (Settings.FollowApps)
        {
            foreach (var (id, name) in calls) inCall[id] = name;
            var hungUp = inCall.Count > 0 && inCall.Keys.All(k => !calls.ContainsKey(k));
            if (Waiting?.Reason is Reason.AppLeft && !hungUp) Waiting = null; // rejoined
            if (hungUp && (Waiting == null || Waiting.Reason is Reason.SilenceReason))
            {
                var name = inCall.OrderBy(kv => kv.Key, StringComparer.Ordinal).First().Value;
                Waiting = new Pending(new Reason.AppLeft(name), now + Settings.AppGrace);
            }
        }
        else if (Waiting?.Reason is Reason.AppLeft)
        {
            Waiting = null;
        }
        if (Waiting == null && Settings.Silence != Silence.Off && !inACall && now - LastSpeech >= Settings.SilenceAfter)
        {
            if (Settings.Silence == Silence.End) return new Reason.SilenceReason();
            Waiting = new Pending(new Reason.SilenceReason(), now + Settings.Countdown);
        }
        if ((Settings.Silence == Silence.Off || inACall) && Waiting?.Reason is Reason.SilenceReason) Waiting = null;
        if (Waiting is { } p && now >= p.Deadline)
        {
            Waiting = null;
            return p.Reason;
        }
        return null;
    }
}
