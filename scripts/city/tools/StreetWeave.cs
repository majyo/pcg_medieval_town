using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PcgCitySandbox.City;

/// <summary>
/// Weaves streets into a set of cells: spaced points (existing road cells count as one connected hub group), joined
/// shortest-first into a spanning tree plus a few loops that do not leave sharp angles. The street nodes decide only
/// where points may fall and where streets may run.
/// </summary>
public static class StreetWeave
{
    private const int MaxPoints = 160;
    private const int MaxSearches = 900;

    /// <param name="hub">Which existing road cells the new streets may start from.</param>
    /// <param name="scatter">Which cells may hold a point.</param>
    /// <param name="within">Which cells a street may run through (on top of the zone); null for the whole zone.</param>
    /// <returns>How many candidate links found no legal path.</returns>
    public static int Weave(RoadZones zones, RoadLinks links, RoadZone zone, Func<int, bool> hub, Func<int, bool> scatter,
        Func<int, bool>? within, float spacing, float loopChance, Random rng, CancellationToken cancel)
    {
        var grid = zones.Grid;
        float Distance(int a, int b) => grid.Center(a).DistanceTo(grid.Center(b));

        var roadCells = links.Cells.ToHashSet();
        var hubs = new List<int>();
        foreach (int k in roadCells.Where(hub).OrderBy(k => k))
        {
            if (hubs.All(h => Distance(h, k) >= spacing * 0.8f))
                hubs.Add(k);
        }

        var points = new List<int>(hubs);
        var candidates = Enumerable.Range(0, grid.Count).Where(scatter).OrderBy(_ => rng.Next()).ToList();
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
            if (Root(a) == Root(b) && (rng.NextSingle() > loopChance || Acute(grid, points, accepted, a, b)))
                continue;
            if (++attempts > MaxSearches)
                break;

            var path = RoadSearch.Find(zones, links, points[a], points[b], zone, RoadKind.Street, cancel, within);
            if (path == null)
            {
                failed++;
                continue;
            }

            links.Mark(path, RoadKind.Street);
            parent[Root(b)] = Root(a);
            accepted.Add((a, b));
        }

        return failed;
    }

    /// <summary>True if the new link would leave either end at a sharp angle to an accepted link.</summary>
    private static bool Acute(SiteGrid grid, List<int> points, List<(int A, int B)> accepted, int a, int b)
    {
        bool SharpAt(int pivot, int target)
        {
            var direction = grid.Center(points[target]) - grid.Center(points[pivot]);
            foreach (var (p, q) in accepted)
            {
                if (p != pivot && q != pivot)
                    continue;
                var other = grid.Center(points[p == pivot ? q : p]) - grid.Center(points[pivot]);
                if (MathF.Abs(other.AngleTo(direction)) < 0.6f)
                    return true;
            }

            return false;
        }

        return SharpAt(a, b) || SharpAt(b, a);
    }
}
