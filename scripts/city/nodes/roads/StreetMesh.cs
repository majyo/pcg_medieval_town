using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class StreetMeshOptions
{
    [Param("街巷间距 m", 25, 140, 5)] public float Spacing { get; set; } = 60;
    [Param("成环概率", 0, 1, 0.05)] public float LoopChance { get; set; } = 0.35f;
    [Param("种子", 0, 99999)] public int Seed { get; set; } = 1;
}

/// <summary>
/// Organic street mesh inside the walls: spaced points (existing road cells count as one connected hub group),
/// joined shortest-first into a spanning tree plus a few loops that do not leave sharp angles.
/// </summary>
public sealed class StreetMesh : CityNode<StreetMeshOptions>
{
    private const int MaxPoints = 160;
    private const int MaxSearches = 900;

    public override string Group => "路网";
    public override string Title => "街巷网";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones];
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, StreetMeshOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var site = zones.Site;
        var links = c.Get(Keys.RoadLinks).Copy();
        var rng = new Random(o.Seed);
        float spacing = o.Spacing;
        float Distance(int a, int b) => site.Center(a).DistanceTo(site.Center(b));

        var roadCells = links.Cells.ToHashSet();
        var hubs = new List<int>();
        foreach (int k in roadCells.Where(zones.Inside).OrderBy(k => k))
        {
            if (hubs.All(h => Distance(h, k) >= spacing * 0.8f))
                hubs.Add(k);
        }

        var points = new List<int>(hubs);
        var candidates = Enumerable.Range(0, site.Count).Where(k => zones.Allowed(k, RoadZone.Inside) && !zones.IsGate(k))
            .OrderBy(_ => rng.Next()).ToList();
        foreach (int k in candidates)
        {
            if (points.Count >= hubs.Count + MaxPoints)
                break;
            if (points.All(s => Distance(s, k) >= spacing) && roadCells.All(r => Distance(r, k) >= spacing * 0.5f))
                points.Add(k);
        }

        var parent = Enumerable.Range(0, points.Count).Select(i => i < hubs.Count ? 0 : i).ToArray();
        int Root(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        var pairs = new List<(int A, int B, float Length)>();
        for (int i = 0; i < points.Count; i++)
            for (int j = Math.Max(i + 1, hubs.Count); j < points.Count; j++)
            {
                float length = Distance(points[i], points[j]);
                if (length < spacing * 1.9f)
                    pairs.Add((i, j, length));
            }

        var accepted = new List<(int A, int B)>();
        int failed = 0, attempts = 0;
        foreach (var (a, b, _) in pairs.OrderBy(p => p.Length))
        {
            if (Root(a) == Root(b) && (rng.NextSingle() > o.LoopChance || Acute(site, points, accepted, a, b)))
                continue;
            if (++attempts > MaxSearches)
                break;

            var path = RoadSearch.Find(zones, links, points[a], points[b], RoadZone.Inside, RoadKind.Street, c.Cancellation);
            if (path == null)
            {
                failed++;
                continue;
            }

            links.Mark(path, RoadKind.Street);
            parent[Root(b)] = Root(a);
            accepted.Add((a, b));
        }

        if (failed > 0)
            c.Notice($"{failed} 条候选街巷无合法通路，已跳过");
        c.Set(Keys.RoadLinks, links);
    }

    /// <summary>True if the new link would leave either end at a sharp angle to an accepted link.</summary>
    private static bool Acute(Site site, List<int> points, List<(int A, int B)> accepted, int a, int b)
    {
        bool SharpAt(int pivot, int target)
        {
            var direction = site.Center(points[target]) - site.Center(points[pivot]);
            foreach (var (p, q) in accepted)
            {
                if (p != pivot && q != pivot)
                    continue;
                var other = site.Center(points[p == pivot ? q : p]) - site.Center(points[pivot]);
                if (MathF.Abs(other.AngleTo(direction)) < 0.6f)
                    return true;
            }

            return false;
        }

        return SharpAt(a, b) || SharpAt(b, a);
    }
}
