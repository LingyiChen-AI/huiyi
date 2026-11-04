using HuiYi;
using HuiYi.Audio;
using HuiYi.Speech;
using NAudio.Wave;
using SherpaOnnx;

/// huiyi-cli speakers <audio> <minutes> <segmentation.onnx> <speakers> <embedding.onnx>...
/// Compares the live way of telling voices apart (VAD lines → voiceprint → online clustering) with
/// an offline diarization of the same audio (pyannote segmentation + clustering into a known number
/// of speakers), per voiceprint model and threshold. The offline result stands in for the truth.
static class SpeakerEval
{
    public static void Run(string[] args, ModelStore models)
    {
        var audio = args[1];
        var minutes = double.Parse(args[2]);
        var segModel = args[3];
        var speakers = int.Parse(args[4]);
        var embModels = args.Skip(5).ToArray();
        var samples = Load(audio, minutes);
        Console.WriteLine($"{samples.Length / 16000.0:F0} s of audio");
        if (Environment.GetEnvironmentVariable("HUIYI_STITCH") == "1")
        {
            Stitch(samples, models);
            return;
        }

        // Reference: offline diarization with the first (Chinese) voiceprint model.
        var dcfg = new OfflineSpeakerDiarizationConfig();
        dcfg.Segmentation.Pyannote.Model = segModel;
        dcfg.Segmentation.NumThreads = 4;
        dcfg.Embedding.Model = embModels[0];
        dcfg.Embedding.NumThreads = 4;
        dcfg.Clustering.NumClusters = speakers;
        dcfg.MinDurationOn = 0.3f;
        dcfg.MinDurationOff = 0.5f;
        using var diar = new OfflineSpeakerDiarization(dcfg);
        var reference = diar.Process(samples);
        Console.WriteLine($"reference: {reference.Length} turns, speakers {string.Join(",", reference.GroupBy(r => r.Speaker).Select(g => $"{g.Key}:{g.Sum(r => r.End - r.Start):F0}s"))}");

        // The live lines: the same VAD settings as ListenEngine.
        var lines = VadLines(samples, models);
        var truth = lines.Select(l => RefSpeaker(reference, l.Start, l.End)).ToArray();
        Console.WriteLine($"{lines.Count} lines; reference speaker per line: {string.Join("", truth.Select(t => t < 0 ? "-" : t.ToString()))}");

        // With HUIYI_SPLIT=1: cutting long lines where the speaker changes (TurnSplitter), per threshold —
        // lines still holding two people, cuts at a real change vs cuts inside one person, and how the
        // clustering scores on the pieces.
        if (Environment.GetEnvironmentVariable("HUIYI_SPLIT") == "1")
        {
            using var ex = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = models.Speaker, NumThreads = 4 });
            var mixedBefore = lines.Count(l => l.End - l.Start >= 2 && RefSpeaker(reference, l.Start, l.End) < 0);
            var embBefore = lines.Select(l => Embed(ex, l.Samples)).ToArray();
            var cb = new Clusterer(VoiceClusterer.Join);
            var before = lines.Select((l, i) => embBefore[i] == null ? (int?)null : cb.Label(embBefore[i]!, l.Samples.Length / 16000.0)).ToArray();
            Console.WriteLine($"no split: {lines.Count} lines, {mixedBefore} hold two people; clustering {Score(before, truth)}");
            foreach (var th in new[] { 0.25f, 0.3f, 0.35f, 0.4f, 0.45f, 0.5f })
            {
                var pieces = new List<Line>();
                int good = 0, bad = 0;
                foreach (var l in lines)
                {
                    var points = TurnSplitter.Points(l.Samples, s => Embed(ex, s), th);
                    var edges = points.Prepend(0).Append(l.Samples.Length).ToList();
                    for (var k = 0; k + 1 < edges.Count; k++)
                        pieces.Add(new Line(l.Start + edges[k] / 16000.0, l.Start + edges[k + 1] / 16000.0, l.Samples[edges[k]..edges[k + 1]]));
                    foreach (var p in points)
                    {
                        var at = l.Start + p / 16000.0;
                        int left = RefSpeaker(reference, at - 1.2, at), right = RefSpeaker(reference, at, at + 1.2);
                        if (left >= 0 && right >= 0) { if (left != right) good++; else bad++; }
                    }
                }
                var ptruth = pieces.Select(p => RefSpeaker(reference, p.Start, p.End)).ToArray();
                var mixed = pieces.Count(p => p.End - p.Start >= 2 && RefSpeaker(reference, p.Start, p.End) < 0);
                var c = new Clusterer(VoiceClusterer.Join);
                var labels = pieces.Select(p => Embed(ex, p.Samples) is { } e ? c.Label(e, p.Samples.Length / 16000.0) : null).ToArray();
                Console.WriteLine($"split <{th:F2}: {pieces.Count} pieces, {mixed} hold two people, cuts at a change {good} / inside one voice {bad}; clustering {Score(labels, ptruth)}");
            }
            return;
        }

        // With HUIYI_TEXT=<threshold>: each line's words beside every model's label at that threshold,
        // to judge by content where the speaker really changes.
        if (Environment.GetEnvironmentVariable("HUIYI_TEXT") is { } th0 && float.TryParse(th0, out var showAt))
        {
            using var rec = new Recognizer(models);
            var cols = new List<int?[]>();
            foreach (var m in embModels.Prepend(models.Speaker))
            {
                using var ex = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = m, NumThreads = 4 });
                var c = new Clusterer(showAt);
                cols.Add(lines.Select(l => Embed(ex, l.Samples) is { } e ? c.Label(e, l.Samples.Length / 16000.0) : null).ToArray());
            }
            for (var i = 0; i < lines.Count; i++)
            {
                var text = rec.Recognize(lines[i].Samples).Text;
                Console.WriteLine($"{i,3} {TranscriptExport.Clock(lines[i].Start)} [{string.Join(" ", cols.Select(c => c[i]?.ToString() ?? "-"))}] {(text.Length > 48 ? text[..48] : text)}");
            }
            return;
        }

        foreach (var m in embModels.Prepend(models.Speaker))
        {
            var cfg = new SpeakerEmbeddingExtractorConfig { Model = m, NumThreads = 4 };
            using var ex = new SpeakerEmbeddingExtractor(cfg);
            var embs = lines.Select(l => Embed(ex, l.Samples)).ToArray();
            // How far apart the two voices are for this model.
            var same = new List<float>();
            var diff = new List<float>();
            for (var i = 0; i < lines.Count; i++)
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (truth[i] < 0 || truth[j] < 0 || embs[i] == null || embs[j] == null) continue;
                    (truth[i] == truth[j] ? same : diff).Add(Dot(embs[i]!, embs[j]!));
                }
            Console.WriteLine($"\n== {Path.GetFileName(m)}  same-speaker cos {Mean(same):F2}±{Sd(same):F2}  different {Mean(diff):F2}±{Sd(diff):F2}");
            foreach (var th in new[] { 0.3f, 0.35f, 0.4f, 0.45f, 0.5f, 0.55f, 0.6f, 0.65f, 0.7f })
            {
                var c = new Clusterer(th);
                var labels = lines.Select((l, i) => embs[i] == null ? (int?)null : c.Label(embs[i]!, l.Samples.Length / 16000.0)).ToArray();
                Console.WriteLine($"  threshold {th:F2}: {Score(labels, truth)}  {string.Join("", labels.Select(x => x?.ToString() ?? "-"))}");
                if (Math.Abs(th - VoiceClusterer.Join) < 0.001f)
                {
                    // How alike the voices' centres are: candidates for merging.
                    var centres = c.Centres();
                    for (var a = 0; a < centres.Count; a++)
                        for (var b = a + 1; b < centres.Count; b++)
                            Console.WriteLine($"    voices {a}({labels.Count(x => x == a)} lines) ~ {b}({labels.Count(x => x == b)} lines): {Dot(centres[a], centres[b]):F2}");
                }
            }
        }
    }

    /// The online rule with a tunable threshold (join and start-new share it, as in VoiceClusterer).
    private sealed class Clusterer(float threshold)
    {
        private readonly List<float[]> sums = [];

        public List<float[]> Centres() => sums.Select(s =>
        {
            var n = MathF.Sqrt(s.Sum(x => x * x));
            return s.Select(x => x / n).ToArray();
        }).ToList();

        public int? Label(float[] v, double seconds)
        {
            int best = -1;
            float bestSim = -1;
            for (var i = 0; i < sums.Count; i++)
            {
                var s = sums[i];
                var n = MathF.Sqrt(s.Sum(x => x * x));
                var sim = Dot(v, s) / Math.Max(n, 1e-9f);
                if (sim > bestSim) { bestSim = sim; best = i; }
            }
            if (best >= 0 && bestSim >= threshold)
            {
                for (var k = 0; k < v.Length; k++) sums[best][k] += v[k];
                return best;
            }
            if (seconds >= 2 && (sums.Count == 0 || bestSim < threshold))
            {
                sums.Add((float[])v.Clone());
                return sums.Count - 1;
            }
            return null;
        }
    }

    /// Lines labelled right under the best mapping of clusters to reference speakers, and how many
    /// clusters there were.
    private static string Score(int?[] labels, int[] truth)
    {
        var pairs = labels.Zip(truth).Where(p => p.First != null && p.Second >= 0).Select(p => (C: p.First!.Value, T: p.Second)).ToList();
        if (pairs.Count == 0) return "no labels";
        // Each cluster votes for the reference speaker it overlaps most.
        var correct = pairs.GroupBy(p => p.C).Sum(g => g.GroupBy(p => p.T).Max(h => h.Count()));
        var clusters = pairs.Select(p => p.C).Distinct().Count();
        var unlabelled = labels.Count(l => l == null);
        return $"{100.0 * correct / pairs.Count,5:F1}% right, {clusters} voices, {unlabelled} unlabelled";
    }

    private record Line(double Start, double End, float[] Samples);

    /// Quick exchanges built from real lines: the live clustering finds the two main voices, then
    /// lines are joined end to end with no pause — A+B (a change at a known point) and A+A (none) —
    /// and TurnSplitter is scored on finding the joins between people and leaving the others alone.
    private static void Stitch(float[] samples, ModelStore models)
    {
        using var ex = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = models.Speaker, NumThreads = 4 });
        var lines = VadLines(samples, models).Where(l => l.End - l.Start >= 2.5).ToList();
        var c = new Clusterer(VoiceClusterer.Join);
        var voice = lines.Select(l => Embed(ex, l.Samples) is { } e ? c.Label(e, l.Samples.Length / 16000.0) : null).ToArray();
        var top = voice.Where(v => v != null).GroupBy(v => v!.Value).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key).ToArray();
        if (top.Length < 2) { Console.WriteLine("only one voice found"); return; }
        var a = lines.Where((l, i) => voice[i] == top[0]).ToList();
        var b = lines.Where((l, i) => voice[i] == top[1]).ToList();
        Console.WriteLine($"voice {top[0]}: {a.Count} lines, voice {top[1]}: {b.Count} lines");
        static float[] Clip(Line l, int max) => l.Samples.Length > max ? l.Samples[..max] : l.Samples;
        var rnd = new Random(7);
        var cross = new List<(float[] S, int Join)>();
        var same = new List<(float[] S, int Join)>();
        for (var n = 0; n < 60; n++)
        {
            // 2.5–7 s of each side, like a reply cutting in.
            var x = Clip(a[rnd.Next(a.Count)], rnd.Next(40000, 112000));
            var y = Clip(b[rnd.Next(b.Count)], rnd.Next(40000, 112000));
            cross.Add(n % 2 == 0 ? ([.. x, .. y], x.Length) : ([.. y, .. x], y.Length));
            var p = a[rnd.Next(a.Count)];
            var q = a[rnd.Next(a.Count)];
            if (p == q) continue;
            var px = Clip(p, rnd.Next(40000, 112000));
            same.Add(([.. px, .. Clip(q, rnd.Next(40000, 112000))], px.Length));
        }
        var cache = new Dictionary<(float[], int, int), float[]?>();
        var win = double.TryParse(Environment.GetEnvironmentVariable("HUIYI_WINDOW"), out var wv) ? wv : TurnSplitter.Window;
        Console.WriteLine($"window {win} s");
        foreach (var th in new[] { 0.25f, 0.3f, 0.35f, 0.4f, 0.45f, 0.5f })
        {
            int hit = 0, wrongPlace = 0, missed = 0, falseCuts = 0;
            foreach (var (s, join) in cross)
            {
                var pts = TurnSplitter.Points(s, w => Embed(ex, w), th, win);
                if (pts.Any(p => Math.Abs(p - join) <= 12000)) hit++; else if (pts.Count > 0) wrongPlace++; else missed++;
                falseCuts += pts.Count(p => Math.Abs(p - join) > 12000);
            }
            var sameCut = same.Count(t => TurnSplitter.Points(t.S, w => Embed(ex, w), th, win).Count > 0);
            Console.WriteLine($"<{th:F2}: A+B found at the join {hit}/{cross.Count} (cut elsewhere {wrongPlace}, missed {missed}, extra cuts {falseCuts}); A+A cut anyway {sameCut}/{same.Count}");
        }
    }

    private static List<Line> VadLines(float[] samples, ModelStore models)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = models.Vad;
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.5f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.MaxSpeechDuration = 12f;
        config.SileroVad.WindowSize = 512;
        config.SampleRate = 16000;
        using var vad = new VoiceActivityDetector(config, 60);
        var lines = new List<Line>();
        void Drain()
        {
            while (!vad.IsEmpty())
            {
                var s = vad.Front();
                lines.Add(new Line(s.Start / 16000.0, (s.Start + s.Samples.Length) / 16000.0, s.Samples));
                vad.Pop();
            }
        }
        for (var i = 0; i < samples.Length; i += 1600)
        {
            vad.AcceptWaveform(samples[i..Math.Min(samples.Length, i + 1600)]);
            Drain();
        }
        vad.Flush();
        Drain();
        return lines;
    }

    private static int RefSpeaker(OfflineSpeakerDiarizationSegment[] reference, double start, double end)
    {
        var overlap = new Dictionary<int, double>();
        foreach (var r in reference)
        {
            var o = Math.Min(end, r.End) - Math.Max(start, r.Start);
            if (o > 0) overlap[r.Speaker] = overlap.GetValueOrDefault(r.Speaker) + o;
        }
        if (overlap.Count == 0) return -1;
        var best = overlap.MaxBy(kv => kv.Value);
        // A line that is mostly two people counts as neither.
        return best.Value >= 0.7 * overlap.Values.Sum() ? best.Key : -1;
    }

    private static float[]? Embed(SpeakerEmbeddingExtractor ex, float[] s)
    {
        if (s.Length < 8000) return null;
        using var st = ex.CreateStream();
        st.AcceptWaveform(16000, s.Length > 320000 ? s[..320000] : s);
        st.InputFinished();
        if (!ex.IsReady(st)) return null;
        var v = ex.Compute(st);
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    private static float Dot(float[] a, float[] b)
    {
        float d = 0;
        for (var i = 0; i < a.Length; i++) d += a[i] * b[i];
        return d;
    }

    private static float Mean(List<float> x) => x.Count == 0 ? 0 : x.Average();
    private static float Sd(List<float> x) => x.Count < 2 ? 0 : MathF.Sqrt(x.Select(v => (v - Mean(x)) * (v - Mean(x))).Average());

    private static float[] Load(string path, double minutes)
    {
        using var reader = new MediaFoundationReader(path);
        var resampler = new Resampler(reader.WaveFormat.SampleRate);
        var all = new List<float>();
        var buf = new byte[reader.WaveFormat.AverageBytesPerSecond];
        int n;
        while (all.Count < minutes * 60 * 16000 && (n = reader.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(resampler.Process(Resampler.ToMonoFloat(buf, n, reader.WaveFormat)));
        return all.ToArray();
    }
}
