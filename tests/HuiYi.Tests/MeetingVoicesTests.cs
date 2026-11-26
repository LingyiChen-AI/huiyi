namespace HuiYi.Tests;

/// The after-meeting grouping of a whole meeting's voices (issue #5).
public class MeetingVoicesTests
{
    private static float[] Unit(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    /// A voice: `center` with a little noise, as a person's lines are.
    private static float[] Near(float[] center, Random rnd, float noise = 0.05f) =>
        Unit(center.Select(x => x + (float)(rnd.NextDouble() * 2 - 1) * noise).ToArray());

    private static float[] Random(Random rnd, int dim = 16) => Unit(Enumerable.Range(0, dim).Select(_ => (float)(rnd.NextDouble() * 2 - 1)).ToArray());

    /// The textbook way: merge the two most alike groups (mean pairwise similarity) while they are
    /// at least `minSim` alike. Groups as sets of indices.
    private static List<HashSet<int>> BruteForce(IReadOnlyList<float[]> v, float minSim)
    {
        var groups = Enumerable.Range(0, v.Count).Select(i => new HashSet<int> { i }).ToList();
        float Dot(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();
        while (groups.Count > 1)
        {
            var best = (A: -1, B: -1, Sim: float.NegativeInfinity);
            for (var a = 0; a < groups.Count; a++)
                for (var b = a + 1; b < groups.Count; b++)
                {
                    var s = groups[a].SelectMany(i => groups[b].Select(j => Dot(v[i], v[j]))).Average();
                    if (s > best.Sim) best = (a, b, s);
                }
            if (best.Sim < minSim) break;
            groups[best.A].UnionWith(groups[best.B]);
            groups.RemoveAt(best.B);
        }
        return groups;
    }

    [Fact]
    public void GroupsAsTheTextbookAverageLinkageDoes()
    {
        var rnd = new Random(7);
        for (var trial = 0; trial < 20; trial++)
        {
            var centers = Enumerable.Range(0, rnd.Next(2, 6)).Select(_ => Random(rnd)).ToList();
            var v = Enumerable.Range(0, rnd.Next(10, 40)).Select(_ => Near(centers[rnd.Next(centers.Count)], rnd, 0.6f)).ToList();
            foreach (var minSim in new[] { 0.3f, 0.5f, 0.7f })
            {
                var got = MeetingVoices.AverageLink(v, minSim);
                var want = BruteForce(v, minSim);
                var gotSets = got.Select((g, i) => (g, i)).GroupBy(x => x.g).Select(x => x.Select(y => y.i).OrderBy(i => i).ToArray())
                    .OrderBy(s => s[0]).ToList();
                var wantSets = want.Select(s => s.OrderBy(i => i).ToArray()).OrderBy(s => s[0]).ToList();
                Assert.Equal(wantSets.Count, gotSets.Count);
                for (var k = 0; k < wantSets.Count; k++) Assert.Equal(wantSets[k], gotSets[k]);
            }
        }
    }

    [Fact]
    public void AsManyPeopleAsThereAreVoices()
    {
        var rnd = new Random(1);
        var people = Enumerable.Range(0, 5).Select(_ => Random(rnd, 64)).ToList();
        var lines = Enumerable.Range(0, 200).Select(i => ((float[]?)Near(people[i % 5], rnd), 3.0)).ToList();
        var voices = MeetingVoices.Group(lines);
        Assert.Equal(5, voices.Known);
        // Each person's lines all go to one group, a different one for each.
        var of = Enumerable.Range(0, 5).Select(p => lines.Where((_, i) => i % 5 == p).Select(l => voices.Closest(l.Item1!)).Distinct().Single()).ToList();
        Assert.Equal(5, of.Distinct().Count());
    }

    [Fact]
    public void AStrayGoesToAPerson()
    {
        var rnd = new Random(2);
        var a = Random(rnd, 64);
        var b = Random(rnd, 64);
        var cough = Random(rnd, 64);
        var lines = new List<(float[]?, double)>();
        for (var i = 0; i < 30; i++) lines.Add((Near(i % 2 == 0 ? a : b, rnd), 4.0));
        // A sound unlike both, 3 s in all: not a person.
        lines.Add((Near(cough, rnd), 1.5));
        lines.Add((Near(cough, rnd), 1.5));
        var voices = MeetingVoices.Group(lines);
        Assert.Equal(2, voices.Known);
        Assert.NotNull(voices.Closest(lines[^1].Item1!));
    }

    [Fact]
    public void ShortLinesDoNotShapeTheGroups()
    {
        var rnd = new Random(3);
        var a = Random(rnd, 64);
        var lines = new List<(float[]?, double)>();
        for (var i = 0; i < 20; i++) lines.Add((Near(a, rnd), 3.0));
        // Many half-second scraps of another sound, 15 s in all: too short to start anyone.
        var other = Random(rnd, 64);
        for (var i = 0; i < 30; i++) lines.Add((Near(other, rnd), 0.5));
        lines.Add((null, 2.0));
        Assert.Equal(1, MeetingVoices.Group(lines).Known);
    }

    [Fact]
    public void NoVoiceprintsNoPeople()
    {
        Assert.Equal(0, MeetingVoices.Group([(null, 3.0), (null, 5.0)]).Known);
        Assert.Equal(0, MeetingVoices.Group([]).Known);
    }
}
