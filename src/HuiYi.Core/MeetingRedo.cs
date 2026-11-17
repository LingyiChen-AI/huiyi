using HuiYi.Speech;

namespace HuiYi;

/// After a meeting, who said what is checked again over the whole recording. Live, a line is
/// labelled with the voices heard so far: early in a meeting there are none yet, so quick exchanges
/// then are only split by comparing neighbouring windows (~74% of turns found, against ~91% once the
/// voices are known). Afterwards every line is looked at with the meeting's full set of voices:
/// lines still holding two people are cut (and recognized again), and each line gets the voice it
/// sounds most like. (A standard offline diarization, pyannote + global clustering, was tried first:
/// on a two-person Bilibili interview it put both people in one voice, so it isn't used.)
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

        // 1. The meeting's voices, from every line in order (the live rule, now with all of it).
        var voices = new VoiceClusterer();
        var prints = new Dictionary<string, float[]?>();
        foreach (var l in theirs)
        {
            step?.Invoke(done++, total);
            var p = embed(Slice(l.T, l.End!.Value));
            prints[l.Id] = p;
            if (p != null) voices.Label(p, l.End!.Value - l.T);
            voices.FoldStrays();
        }
        int? Who(float[]? p) => p == null || voices.Known == 0 ? null : voices.Nearest(p, 0.4f, 0.0f);
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
