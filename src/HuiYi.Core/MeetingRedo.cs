using HuiYi.Speech;

namespace HuiYi;

/// After a meeting, who said what is checked again over the whole recording. Live, a line is
/// labelled with the voices heard so far: early in a meeting there are none yet, so quick exchanges
/// then are only split by comparing neighbouring windows (~74% of turns found, against ~91% once the
/// voices are known). Afterwards the meeting's voices are grouped from all its lines at once
/// (`MeetingVoices`), lines still holding two people are cut (and recognized again), and each line
/// gets the voice it sounds most like. (A standard offline diarization, pyannote + global
/// clustering, was tried first: on a two-person Bilibili interview it put both people in one voice,
/// so it isn't used.)
/// A meeting's recording (16 kHz mono 16-bit WAV), read a stretch at a time. Seven hours of it are
/// 1.6 GB as floats: loading it whole made the after-meeting check page the machine for hours.
public sealed class RecordingReader : IDisposable
{
    private readonly NAudio.Wave.WaveFileReader reader;
    private readonly object gate = new();

    public RecordingReader(string path)
    {
        reader = new NAudio.Wave.WaveFileReader(path);
        if (reader.WaveFormat.Encoding != NAudio.Wave.WaveFormatEncoding.Pcm || reader.WaveFormat.BitsPerSample != 16
            || reader.WaveFormat.Channels != 1 || reader.WaveFormat.SampleRate != Recognizer.SampleRate)
        {
            reader.Dispose();
            throw new InvalidDataException($"recording is {reader.WaveFormat}, not 16 kHz mono 16-bit");
        }
    }

    public double Seconds => reader.SampleCount / (double)Recognizer.SampleRate;

    /// The samples from `from` to `to` seconds (clipped to the recording).
    public float[] Slice(double from, double to)
    {
        lock (gate)
        {
            var count = reader.SampleCount;
            var a = Math.Clamp((long)(from * Recognizer.SampleRate), 0, count);
            var b = Math.Clamp((long)(to * Recognizer.SampleRate), a, count);
            var bytes = new byte[(b - a) * 2];
            reader.Position = a * 2;
            var read = 0;
            while (read < bytes.Length && reader.Read(bytes, read, bytes.Length - read) is var n and > 0) read += n;
            var samples = new float[read / 2];
            for (var i = 0; i < samples.Length; i++) samples[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8) / 32768f;
            return samples;
        }
    }

    public void Dispose() => reader.Dispose();
}

/// A whole meeting's voices, grouped at once after it ends. Live, lines are grouped as they come:
/// early on every voice has a line or two, a line a little unlike them starts a voice of its own,
/// and a voice once started is never undone (issue #5: five people shown as twelve). With every line
/// at hand, lines are grouped by how alike they all are (average linkage on cosine similarity), and
/// the number of people follows from it. On five recordings with the number known (2, 4, about 7
/// and two of 8 people), this gave 2, 4, 8, 7 and 8; on the AMI meeting, whose speakers are
/// annotated, 95% of speech went to the right person against 88% live.
public sealed class MeetingVoices
{
    /// Groups merge while their lines are this alike on average. 0.45–0.55 all did about as well on
    /// those recordings; above, a meeting of eight came out as four or six, below 0.5 the interview
    /// of two came out as three.
    public const float MinSimilarity = 0.5f;
    /// Lines shorter than this are too unsteady to shape the groups; they take the closest one after.
    public const double MinLine = 1.0;
    /// A group with less speech than this in all is a stray (a laugh, a cough, music): its lines go to
    /// the closest group that is a person.
    public const double MinSeconds = 8.0;

    private readonly List<float[]> centroids;

    private MeetingVoices(List<float[]> centroids) => this.centroids = centroids;

    public int Known => centroids.Count;

    /// `prints`: each line's voiceprint (null: none) and its length in seconds.
    public static MeetingVoices Group(IReadOnlyList<(float[]? Print, double Seconds)> prints)
    {
        var used = Enumerable.Range(0, prints.Count).Where(i => prints[i].Print != null && prints[i].Seconds >= MinLine).ToList();
        if (used.Count == 0) return new MeetingVoices([]);
        var groups = AverageLink(used.Select(i => Unit(prints[i].Print!)).ToList(), MinSimilarity);
        var dim = prints[used[0]].Print!.Length;
        var sums = new Dictionary<int, (float[] Sum, double Seconds)>();
        for (var j = 0; j < used.Count; j++)
        {
            var (p, s) = prints[used[j]];
            if (!sums.TryGetValue(groups[j], out var g)) sums[groups[j]] = g = (new float[dim], 0);
            var u = Unit(p!);
            for (var k = 0; k < dim; k++) g.Sum[k] += u[k] * (float)s;
            sums[groups[j]] = (g.Sum, g.Seconds + s);
        }
        var people = sums.Values.Where(g => g.Seconds >= MinSeconds).ToList();
        if (people.Count == 0) people = [sums.Values.MaxBy(g => g.Seconds)];
        return new MeetingVoices(people.OrderByDescending(g => g.Seconds).Select(g => Unit(g.Sum)).ToList());
    }

    /// The group `v` sounds most like (index), however little.
    public int? Closest(float[] v) => Nearest(v, -1, 0);

    /// The group `v` clearly sounds like: at least `minSim` alike and `margin` ahead of the next.
    public int? Nearest(float[] v, float minSim, float margin)
    {
        var u = Unit(v);
        int best = -1;
        float bestSim = -2, second = -2;
        for (var i = 0; i < centroids.Count; i++)
        {
            var sim = Dot(u, centroids[i]);
            if (sim > bestSim) { second = bestSim; bestSim = sim; best = i; }
            else if (sim > second) second = sim;
        }
        return best >= 0 && bestSim >= minSim && (centroids.Count == 1 || bestSim - second >= margin) ? best : null;
    }

    /// Average-linkage grouping of unit vectors: two groups merge while the mean similarity of their
    /// members, pair by pair, is at least `minSim`. For unit vectors that mean is the dot product of
    /// the two groups' sums over the product of their sizes, so a group is kept as its sum and size,
    /// and the nearest-neighbour chain finds the merges in O(n²) time and O(n) memory (a seven-hour
    /// meeting has thousands of lines; their pairwise table alone would be hundreds of MB).
    /// Returns a group number per vector.
    public static int[] AverageLink(IReadOnlyList<float[]> unitVectors, float minSim)
    {
        var n = unitVectors.Count;
        var sum = unitVectors.Select(v => (float[])v.Clone()).ToArray();
        var size = Enumerable.Repeat(1, n).ToArray();
        var parent = Enumerable.Range(0, n).ToArray();
        // Active groups still able to merge; a group whose closest is below minSim never will
        // (average linkage is reducible: merging others can't bring them closer to it).
        var active = new HashSet<int>(Enumerable.Range(0, n));
        float Sim(int a, int b) => Dot(sum[a], sum[b]) / (size[a] * (float)size[b]);
        var chain = new List<int>();
        while (active.Count > 1)
        {
            if (chain.Count == 0) chain.Add(active.Min());
            var top = chain[^1];
            var prev = chain.Count > 1 ? chain[^2] : -1;
            int nearest = -1;
            var bestSim = float.NegativeInfinity;
            foreach (var g in active)
            {
                if (g == top) continue;
                var s = Sim(top, g);
                // Ties go to the one before it on the chain, else the lower number: no cycles.
                if (s > bestSim || (s == bestSim && (g == prev || (nearest != prev && g < nearest))))
                {
                    bestSim = s;
                    nearest = g;
                }
            }
            if (nearest == prev)
            {
                chain.RemoveAt(chain.Count - 1);
                chain.RemoveAt(chain.Count - 1);
                if (bestSim < minSim)
                {
                    // The closest pair left for both of them is too far apart: both are done.
                    active.Remove(top);
                    active.Remove(nearest);
                    continue;
                }
                for (var k = 0; k < sum[top].Length; k++) sum[nearest][k] += sum[top][k];
                size[nearest] += size[top];
                parent[top] = nearest;
                active.Remove(top);
            }
            else chain.Add(nearest);
        }
        int Root(int i) { while (parent[i] != i) i = parent[i]; return i; }
        var numbers = new Dictionary<int, int>();
        return Enumerable.Range(0, n).Select(i => numbers.TryGetValue(Root(i), out var g) ? g : numbers[Root(i)] = numbers.Count).ToArray();
    }

    private static float Dot(float[] a, float[] b)
    {
        float d = 0;
        for (var k = 0; k < a.Length; k++) d += a[k] * b[k];
        return d;
    }

    private static float[] Unit(float[] v)
    {
        var norm = MathF.Sqrt(Dot(v, v));
        return norm <= 0 ? (float[])v.Clone() : v.Select(x => x / norm).ToArray();
    }
}

public static class MeetingRedo
{
    public sealed record Piece(double Start, double End, string Text, string Lang, int? Voice);

    /// Per line id, what it becomes: one piece (maybe a new voice) or several (it held two people).
    public sealed record Plan(Dictionary<string, List<Piece>> Lines, int Relabelled, int Split);

    private const int Rate = Recognizer.SampleRate;

    public static Plan Run(float[] audio, IReadOnlyList<TranscriptLine> lines, Func<float[], float[]?> embed,
        Func<float[], Recognized> recognize) =>
        Run((from, to) =>
        {
            var a = Math.Clamp((int)(from * Rate), 0, audio.Length);
            var b = Math.Clamp((int)(to * Rate), a, audio.Length);
            return audio[a..b];
        }, lines, embed, recognize);

    /// `slice`: the recording between two times (seconds), read when needed, so a meeting of many
    /// hours is never held in memory whole. `step`: called before each line of each pass with how far
    /// it is (done, total); it may block, to make way for a meeting being heard.
    public static Plan Run(Func<double, double, float[]> slice, IReadOnlyList<TranscriptLine> lines, Func<float[], float[]?> embed,
        Func<float[], Recognized> recognize, Action<int, int>? step = null)
    {
        float[] Slice(double from, double to) => slice(from, to);
        var theirs = lines.Where(l => l.Speaker == "them" && l.End is { } e && e > l.T).ToList();
        var total = theirs.Count * 2;
        var done = 0;

        // 1. The meeting's voices, from all its lines at once (`MeetingVoices`).
        var prints = new Dictionary<string, float[]?>();
        foreach (var l in theirs)
        {
            step?.Invoke(done++, total);
            prints[l.Id] = embed(Slice(l.T, l.End!.Value));
        }
        var voices = MeetingVoices.Group(theirs.Select(l => (prints[l.Id], l.End!.Value - l.T)).ToList());
        int? Who(float[]? p) => p == null ? null : voices.Closest(p);
        var finder = new TurnFinder(embed, (v, sim, margin) => voices.Known >= 2 ? voices.Nearest(v, sim, margin) : null);

        // 2. Each line again: cut where the voice changes, then whose each part is (cluster ids).
        var parts = new Dictionary<string, List<(double Start, double End, string Text, string Lang, int? Cluster)>>();
        foreach (var l in theirs)
        {
            step?.Invoke(done++, total);
            var start = l.T;
            var end = l.End!.Value;
            var clip = Slice(start, end);
            IReadOnlyList<int> cuts = [];
            if (clip.Length >= 2 * TurnFinder.MinSentence * Rate && voices.Known >= 2)
                cuts = finder.Points(clip, recognize(clip).SentenceEnds());
            if (cuts.Count == 0)
            {
                parts[l.Id] = [(start, end, l.Src, l.SrcLang, Who(prints[l.Id]))];
                continue;
            }
            var edges = new List<int> { 0 };
            edges.AddRange(cuts);
            edges.Add(clip.Length);
            var list = new List<(double, double, string, string, int?)>();
            for (var k = 0; k + 1 < edges.Count; k++)
            {
                var piece = clip[edges[k]..edges[k + 1]];
                var r = recognize(piece);
                var text = HeardText.Clean(r.Text);
                if (!text.Any(char.IsLetter)) continue;
                list.Add((start + edges[k] / (double)Rate, start + edges[k + 1] / (double)Rate, text, r.Lang, Who(embed(piece))));
            }
            parts[l.Id] = list.Count > 0 ? list : [(start, end, l.Src, l.SrcLang, Who(prints[l.Id]))];
        }

        // 3. Clusters onto the live voice numbers, so names given during the meeting stay put.
        var agree = new Dictionary<(int Cluster, int Live), double>();
        foreach (var l in theirs)
            if (l.Voice is { } live && parts[l.Id] is [var only] && only.Cluster is { } c)
                agree[(c, live)] = agree.GetValueOrDefault((c, live)) + (only.End - only.Start);
        var map = new Dictionary<int, int>();
        var used = new HashSet<int>();
        foreach (var ((c, live), _) in agree.OrderByDescending(kv => kv.Value))
        {
            if (map.ContainsKey(c) || used.Contains(live)) continue;
            map[c] = live;
            used.Add(live);
        }
        var next = lines.Select(l => l.Voice ?? -1).Append(-1).Max() + 1;

        var plan = new Dictionary<string, List<Piece>>();
        int relabelled = 0, split = 0;
        foreach (var l in theirs)
        {
            var ps = parts[l.Id].Select(p =>
            {
                int? v = l.Voice;
                if (p.Cluster is { } c)
                {
                    if (!map.TryGetValue(c, out var mapped)) map[c] = mapped = next++;
                    v = mapped;
                }
                return new Piece(p.Start, p.End, p.Text, p.Lang, v);
            }).ToList();
            if (ps.Count > 1) split++;
            else if (ps[0].Voice != l.Voice) relabelled++;
            if (ps.Count > 1 || ps[0].Voice != l.Voice) plan[l.Id] = ps;
        }
        return new Plan(plan, relabelled, split);
    }
}
