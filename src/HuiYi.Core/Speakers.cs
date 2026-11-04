using System.Text.Json.Nodes;
using HuiYi.Speech;
using SherpaOnnx;

namespace HuiYi;

// Telling the other side's voices apart (发言人 1、发言人 2…). Each heard line's audio becomes a
// voiceprint (3D-Speaker CAM++, Chinese, through sherpa-onnx), voiceprints are grouped as they
// arrive, and a voice is numbered once it has a couple of lines. The online rules follow Cicada's
// SpeakerID.swift; the model and threshold were chosen with `huiyi-cli speakers` on a two-host
// Chinese interview and a two-voice dialogue.

/// A name the AI found for a voice, and the line it is based on ("00:06 “Sure, Emma…”").
public sealed record VoiceGuess(string Name, string Evidence);

/// Voiceprints: unit length.
public sealed class Voiceprints : IDisposable
{
    public const double MinSeconds = 0.5;
    /// Longer lines are cut: 20 s is plenty to know a voice and bounds the work.
    public const double MaxSeconds = 20;
    private readonly SpeakerEmbeddingExtractor extractor;
    private readonly object gate = new();

    public Voiceprints(ModelStore models)
    {
        var config = new SpeakerEmbeddingExtractorConfig { Model = models.Speaker, NumThreads = 2, Provider = "cpu", Debug = 0 };
        extractor = new SpeakerEmbeddingExtractor(config);
    }

    public float[]? Embed(float[] samples)
    {
        if (samples.Length < MinSeconds * Recognizer.SampleRate) return null;
        var max = (int)(MaxSeconds * Recognizer.SampleRate);
        var clip = samples.Length > max ? samples[..max] : samples;
        lock (gate)
        {
            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(Recognizer.SampleRate, clip);
            stream.InputFinished();
            if (!extractor.IsReady(stream)) return null;
            var v = extractor.Compute(stream);
            double norm = 0;
            foreach (var x in v) norm += x * x;
            if (norm <= 0) return null;
            var n = (float)Math.Sqrt(norm);
            for (var i = 0; i < v.Length; i++) v[i] /= n;
            return v;
        }
    }

    public void Dispose() => extractor.Dispose();
}

/// Groups voiceprints as lines arrive, never revising a line already labelled. A line joins the
/// closest voice when similar enough; a long line unlike every voice starts a new one; anything else
/// stays unlabelled (对方).
public sealed class VoiceClusterer
{
    public const double NewSeconds = 2.0;
    /// For CAM++: one voice scores ~0.6–0.85 against itself, two voices ~0.3; at 0.5 the interview's
    /// guest stayed merged with the host, at 0.55 the switch fell where the words say it does.
    public const float Join = 0.55f;
    public const float StartNew = 0.55f;

    /// A stray voice: this few lines at most…
    public const int StrayLines = 3;
    /// …and none for this many lines since. A real newcomer keeps talking and outgrows it.
    public const int StrayQuiet = 10;
    /// How alike a stray voice's centre must be to a main voice to be folded into it. Centres of
    /// two real people come out 0.5–0.6 too (averaging pulls them together), which is why only stray
    /// voices are ever merged, never two main ones.
    public const float MergeAt = 0.55f;

    private readonly float mergeAt;

    /// `mergeAt`: tests use a lower value to exercise folding with exact vectors.
    public VoiceClusterer(float mergeAt = MergeAt) => this.mergeAt = mergeAt;

    public List<float[]> Sums { get; } = [];
    private readonly List<int> counts = [];
    private readonly List<int> lastLine = [];
    private int lineNo;

    /// The user said two voices are one: later lines like either go to `into`.
    public void Merge(int from, int into)
    {
        if (from == into || from < 0 || into < 0 || from >= Sums.Count || into >= Sums.Count) return;
        for (var i = 0; i < Sums[into].Length; i++) Sums[into][i] += Sums[from][i];
        Sums[from] = new float[Sums[from].Length];   // never the closest again
        counts[into] += counts[from];
        counts[from] = 0;
    }

    /// Stray voices (a few lines, then silence) folded into the main voice they sound like; each
    /// (from, into) pair, for relabelling the lines already shown. Run after each line.
    public List<(int From, int Into)> FoldStrays()
    {
        var merged = new List<(int, int)>();
        for (var s = 0; s < Sums.Count; s++)
        {
            if (counts[s] == 0 || counts[s] > StrayLines || lineNo - lastLine[s] < StrayQuiet) continue;
            int best = -1;
            float bestSim = -1;
            for (var m = 0; m < Sums.Count; m++)
            {
                if (m == s || counts[m] <= StrayLines) continue;
                var sim = Cosine(Sums[s], Sums[m]);
                if (sim > bestSim) { bestSim = sim; best = m; }
            }
            if (best < 0 || bestSim < mergeAt) continue;
            Merge(s, best);
            merged.Add((s, best));
        }
        return merged;
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (var k = 0; k < a.Length; k++)
        {
            dot += a[k] * b[k];
            na += a[k] * a[k];
            nb += b[k] * b[k];
        }
        return dot / Math.Max(MathF.Sqrt(na) * MathF.Sqrt(nb), float.Epsilon);
    }

    public int? Label(float[] v, double seconds)
    {
        lineNo++;
        int best = -1;
        float bestSim = -1;
        for (var i = 0; i < Sums.Count; i++)
        {
            var s = Sums[i];
            float dot = 0, sq = 0;
            for (var k = 0; k < v.Length; k++)
            {
                dot += v[k] * s[k];
                sq += s[k] * s[k];
            }
            var sim = dot / Math.Max(MathF.Sqrt(sq), float.Epsilon);
            if (sim > bestSim) { bestSim = sim; best = i; }
        }
        if (best >= 0 && bestSim >= Join)
        {
            for (var k = 0; k < v.Length; k++) Sums[best][k] += v[k];
            counts[best]++;
            lastLine[best] = lineNo;
            return best;
        }
        if (seconds >= NewSeconds && (Sums.Count == 0 || bestSim < StartNew))
        {
            Sums.Add((float[])v.Clone());
            counts.Add(1);
            lastLine.Add(lineNo);
            return Sums.Count - 1;
        }
        return null;
    }
}

/// Runs voiceprints and hands back a voice per line, for one meeting at a time.
public sealed class SpeakerTagger(Func<Voiceprints?> model)
{
    private VoiceClusterer clusterer = new();
    private readonly object gate = new();

    /// A stray voice was folded into a main one: lines with `from` now belong to `into`.
    public event Action<int, int>? Merged;

    /// A new meeting: forget the voices of the last one.
    public void Reset()
    {
        lock (gate) clusterer = new VoiceClusterer();
    }

    public void Merge(int from, int into)
    {
        lock (gate) clusterer.Merge(from, into);
    }

    /// The voice of a line; nil when unsure. It counts towards that voice.
    public int? Voice(float[] samples)
    {
        var m = model();
        if (m == null) return null;
        float[]? v;
        try { v = m.Embed(samples); }
        catch (Exception e)
        {
            Log.Debug($"voiceprint failed: {e.Message}");
            return null;
        }
        if (v == null) return null;
        int? voice;
        List<(int From, int Into)> merged;
        lock (gate)
        {
            voice = clusterer.Label(v, samples.Length / (double)Recognizer.SampleRate);
            merged = clusterer.FoldStrays();
        }
        foreach (var (from, into) in merged)
        {
            Log.Debug($"speakers: stray voice {from} folded into {into}");
            Merged?.Invoke(from, into);
        }
        return voice;
    }
}

/// What the transcript shows for the other side's lines. A voice gets a number once it has
/// `ShownAfter` substantive lines, even when it is the only one: people want to see the feature at
/// work from the first minute (Cicada waits for five lines and two voices; that read as "not
/// working" here). A stray voice from one short noise never reaches two real lines.
/// Numbers follow the order voices reached `ShownAfter` lines, so a number never moves once shown.
public static class SpeakerLabels
{
    public const int ShownAfter = 2;

    /// A line long enough to say whose voice it is: three words, or four CJK characters.
    public static bool Substantive(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));
        if (words >= 3) return true;
        return text.Count(c => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= '가' and <= '힣') >= 4;
    }

    /// Voice id → 1-based speaker number, for the voices on show.
    public static Dictionary<int, int> VoiceNumbers(IReadOnlyList<TranscriptLine> lines)
    {
        var count = new Dictionary<int, int>();
        var reached = new Dictionary<int, int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Speaker != "them" || l.Voice is not { } v || !Substantive(l.Src)) continue;
            count[v] = count.GetValueOrDefault(v) + 1;
            if (count[v] == ShownAfter) reached[v] = i;
        }
        return reached.OrderBy(kv => kv.Value).Select((kv, i) => (kv.Key, i + 1)).ToDictionary(x => x.Key, x => x.Item2);
    }

    /// Line id → 1-based speaker number; lines without one show as 对方.
    public static Dictionary<string, int> Numbers(IReadOnlyList<TranscriptLine> lines)
    {
        var number = VoiceNumbers(lines);
        var outp = new Dictionary<string, int>();
        foreach (var l in lines)
            if (l.Speaker == "them" && l.Voice is { } v && number.TryGetValue(v, out var n)) outp[l.Id] = n;
        return outp;
    }

    /// Speaker number → name: the user's, else the AI's guess (unless the user turned it down).
    public static Dictionary<int, string> Names(Session s)
    {
        var outp = new Dictionary<int, string>();
        if (s.VoiceNames == null && s.VoiceGuesses == null) return outp;
        foreach (var (v, n) in VoiceNumbers(s.Lines))
        {
            if (s.VoiceNames?.TryGetValue(v.ToString(), out var mine) == true)
            {
                if (mine.Length > 0) outp[n] = mine;
            }
            else if (s.VoiceGuesses?.TryGetValue(v.ToString(), out var g) == true)
            {
                outp[n] = g.Name;
            }
        }
        return outp;
    }

    /// "George", else "发言人 2".
    public static string Label(int number, IReadOnlyDictionary<int, string> names) =>
        names.TryGetValue(number, out var n) ? n : $"发言人 {number}";

    /// What a line shows as its speaker: 我, a speaker's name or number, or 发言人 while the voice has no number yet.
    public static string Who(TranscriptLine l, IReadOnlyDictionary<string, int> numbers, IReadOnlyDictionary<int, string> names) =>
        l.Speaker == "me" ? "我" : numbers.TryGetValue(l.Id, out var n) ? Label(n, names) : "发言人";

    public static int ShownCount(IReadOnlyList<TranscriptLine> lines) => Numbers(lines).Values.Distinct().Count();
}

/// Asks the AI who 发言人 1, 2… are, from how people address each other. Only text is sent; a guess is a
/// suggestion the user can change.
public static class SpeakerNameGuess
{
    public const int EvidenceReach = 2;

    /// The transcript as the AI sees it, newest 400 lines, with speaker labels; nil when no voices are shown.
    public static string? Content(Session s)
    {
        var numbers = SpeakerLabels.Numbers(s.Lines);
        if (numbers.Count == 0) return null;
        var lines = s.Lines.Skip(Math.Max(0, s.Lines.Count - 400)).ToList();
        var sb = new System.Text.StringBuilder("Meeting transcript. Speakers: 你 = the user; 发言人N = one person on the other side, told apart by voice; 对方 = someone on the other side, not identified.\n");
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var who = l.Speaker == "me" ? "你" : numbers.TryGetValue(l.Id, out var n) ? $"发言人{n}" : "对方";
            sb.Append($"[L{i + 1} {TranscriptExport.Clock(l.T)} {who}] {l.Src}\n");
        }
        return sb.ToString();
    }

    public const string Instruction =
        "Who are the people labelled 发言人N? Name one only when the transcript makes it clear: they " +
        "introduce themselves on a line labelled with their own 发言人N; someone addresses them by name just " +
        "before or after they speak (\"Thanks, George\" right after 发言人1's answer: 发言人1 is George); " +
        "or others name them as they speak. A name on a 对方 line, or far from any line of 发言人N (an advert, " +
        "a clip, people introducing themselves on another show), is not theirs. Speech recognition writes " +
        "names by ear and often misspells them: for a public figure (an official, an executive, a presenter) " +
        "recognisable from the name and what is said, write the name as the news spells it (\"Jensen Hwang\" " +
        "→ \"Jensen Huang\"); otherwise as written. Leave out anyone " +
        "you are unsure of. Reply as JSON: {\"speakers\": [{\"speaker\": 2, \"name\": \"George\", \"evidence\": \"L12\"}]}, " +
        "the evidence being the line with the name; an empty list if none. Check each name's spelling before replying.";

    public static Dictionary<int, VoiceGuess> Parse(string reply, Session s) => Read(reply, s).Found;

    /// Voice id → guess. A name with no line of that voice near its evidence is refuted; a name given
    /// to two voices goes to the one with more lines.
    public static (Dictionary<int, VoiceGuess> Found, Dictionary<int, string> Refuted) Read(string reply, Session s)
    {
        var found = new Dictionary<int, VoiceGuess>();
        var refuted = new Dictionary<int, string>();
        JsonArray? list;
        try
        {
            var a = reply.IndexOf('{');
            var b = reply.LastIndexOf('}');
            list = a >= 0 && b > a ? JsonNode.Parse(reply[a..(b + 1)])?["speakers"] as JsonArray : null;
        }
        catch { return (found, refuted); }
        if (list == null) return (found, refuted);
        var voiceOf = SpeakerLabels.VoiceNumbers(s.Lines).ToDictionary(kv => kv.Value, kv => kv.Key);
        var lines = s.Lines.Skip(Math.Max(0, s.Lines.Count - 400)).ToList();
        int Count(int v) => s.Lines.Count(l => l.Voice == v);
        static string Text(JsonNode? n) => n is JsonValue jv ? jv.ToString() : "";
        foreach (var item in list.OfType<JsonObject>())
        {
            if (!int.TryParse(Text(item["speaker"]), out var number) || !voiceOf.TryGetValue(number, out var v)) continue;
            var name = Text(item["name"]).Trim();
            if (name.Length == 0 || name.Length > 40) continue;
            var e = Text(item["evidence"]);
            var digits = new string(e.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            var ok = int.TryParse(digits, out var ln) && ln - 1 >= 0 && ln - 1 < lines.Count;
            var i = ln - 1;
            if (!ok || !lines.Skip(Math.Max(0, i - EvidenceReach)).Take(Math.Min(lines.Count - 1, i + EvidenceReach) - Math.Max(0, i - EvidenceReach) + 1)
                    .Any(l => l.Speaker == "them" && l.Voice == v))
            {
                refuted[v] = name;
                continue;
            }
            var other = found.FirstOrDefault(kv => kv.Value.Name == name);
            if (other.Value != null)
            {
                if (Count(other.Key) >= Count(v)) continue;
                found.Remove(other.Key);
            }
            var l0 = lines[i];
            var quote = l0.Src.Length > 60 ? l0.Src[..60] + "…" : l0.Src;
            found[v] = new VoiceGuess(name, $"{TranscriptExport.Clock(l0.T)} “{quote}”");
        }
        return (found, refuted);
    }
}

/// Asks the AI for names on its own: once two voices are on show, again every 20 lines, and once more
/// when the meeting ends. Guesses go into the meeting (`Session.VoiceGuesses`); the user corrects.
public sealed class SpeakerNamer(AiClient ai, SessionStore store)
{
    private readonly Dictionary<string, int> askedAt = new();
    private readonly HashSet<string> busy = [];

    public void Guess(string id, bool force = false)
    {
        if (!ai.HasKey || store.Get(id) is not { } s) return;
        lock (busy)
        {
            if (busy.Contains(id)) return;
            if (!force && s.Lines.Count < askedAt.GetValueOrDefault(id, -1000) + 20) return;
            var numbers = SpeakerLabels.VoiceNumbers(s.Lines);
            // Everyone already has a name the user gave: nothing to ask.
            if (numbers.Count == 0 || numbers.Keys.All(v => s.VoiceNames?.ContainsKey(v.ToString()) == true)) return;
            busy.Add(id);
            askedAt[id] = s.Lines.Count;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = new Session { Id = s.Id, Lines = store.LinesOf(id), VoiceNames = s.VoiceNames, VoiceGuesses = s.VoiceGuesses };
                if (SpeakerNameGuess.Content(snapshot) is not { } content) return;
                var reply = await ai.Chat(content, SpeakerNameGuess.Instruction, AiClient.Priority.Notes, json: true, maxTokens: 400);
                var (found, refuted) = SpeakerNameGuess.Read(reply, snapshot);
                if (found.Count == 0 && refuted.Count == 0) return;
                store.Mutate(id, x =>
                {
                    x.VoiceGuesses ??= new();
                    foreach (var (v, name) in refuted)
                        if (x.VoiceGuesses.TryGetValue(v.ToString(), out var g) && g.Name == name) x.VoiceGuesses.Remove(v.ToString());
                    foreach (var (v, g) in found) x.VoiceGuesses[v.ToString()] = g;
                });
                Log.Debug($"speakers: {found.Count} names guessed");
            }
            catch (Exception e)
            {
                Log.Debug($"speakers: name guess failed {(e as AiException)?.Failure.ToString() ?? e.GetType().Name}");
            }
            finally
            {
                lock (busy) busy.Remove(id);
            }
        });
    }
}
