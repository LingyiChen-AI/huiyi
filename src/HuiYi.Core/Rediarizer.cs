using SherpaOnnx;

namespace HuiYi;

/// After a meeting: who said what, worked out again over the whole recording at once. Live, each
/// line is labelled as it comes, from the voices heard so far; here pyannote's segmentation finds
/// every turn in the whole meeting (overlaps and quick replies included) and the CAM++ voiceprints
/// are clustered globally, so the record ends up more accurate than the live view.
public static class Rediarizer
{
    /// Line id → voice, for lines of the other side ("them") that overlap a found turn. Voices keep
    /// the live numbering where they match it (so names given during the meeting stay on the right
    /// person); a voice live never had gets a new number.
    public sealed record Result(Dictionary<string, int> Voices, int Speakers, int Changed);

    public static Result? Run(float[] audio, IReadOnlyList<TranscriptLine> lines, string segmentationModel,
        string embeddingModel, int? speakers)
    {
        var cfg = new OfflineSpeakerDiarizationConfig();
        cfg.Segmentation.Pyannote.Model = segmentationModel;
        cfg.Segmentation.NumThreads = 4;
        cfg.Embedding.Model = embeddingModel;
        cfg.Embedding.NumThreads = 4;
        // The live count is a good guess for how many people there were; without one, a threshold.
        if (speakers is >= 2) cfg.Clustering.NumClusters = speakers.Value;
        else cfg.Clustering.Threshold = 0.6f;
        cfg.MinDurationOn = 0.3f;
        cfg.MinDurationOff = 0.5f;
        using var diarization = new OfflineSpeakerDiarization(cfg);
        var turns = diarization.Process(audio);
        if (turns.Length == 0) return null;
        return Map(turns.Select(t => (t.Start, t.End, t.Speaker)).ToList(), lines);
    }

    /// Lines onto turns, then the found speakers onto the live voices. Pure, for tests.
    public static Result Map(IReadOnlyList<(float Start, float End, int Speaker)> turns, IReadOnlyList<TranscriptLine> lines)
    {
        var theirs = lines.Where(l => l.Speaker == "them").ToList();
        var found = new Dictionary<string, int>();
        foreach (var l in theirs)
        {
            var end = l.End ?? l.T + 3;
            var overlap = new Dictionary<int, double>();
            foreach (var t in turns)
            {
                var o = Math.Min(end, t.End) - Math.Max(l.T, t.Start);
                if (o > 0) overlap[t.Speaker] = overlap.GetValueOrDefault(t.Speaker) + o;
            }
            if (overlap.Count > 0) found[l.Id] = overlap.MaxBy(kv => kv.Value).Key;
        }
        // Found speaker → live voice: biggest agreements first, each live voice used once.
        var agree = new Dictionary<(int Found, int Live), double>();
        foreach (var l in theirs)
        {
            if (l.Voice is not { } live || !found.TryGetValue(l.Id, out var f)) continue;
            agree[(f, live)] = agree.GetValueOrDefault((f, live)) + ((l.End ?? l.T + 3) - l.T);
        }
        var map = new Dictionary<int, int>();
        var used = new HashSet<int>();
        foreach (var ((f, live), _) in agree.OrderByDescending(kv => kv.Value))
        {
            if (map.ContainsKey(f) || used.Contains(live)) continue;
            map[f] = live;
            used.Add(live);
        }
        var next = Math.Max(lines.Max(l => l.Voice ?? -1), used.DefaultIfEmpty(-1).Max()) + 1;
        foreach (var f in found.Values.Distinct().OrderBy(x => x))
            if (!map.ContainsKey(f)) map[f] = next++;
        var voices = found.ToDictionary(kv => kv.Key, kv => map[kv.Value]);
        var changed = theirs.Count(l => voices.TryGetValue(l.Id, out var v) && v != l.Voice);
        return new Result(voices, found.Values.Distinct().Count(), changed);
    }
}
