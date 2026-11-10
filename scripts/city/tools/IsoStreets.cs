using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PcgCitySandbox.City;

/// <summary>
/// Streets along an iso-line of some value per cell (a distance to the wall, an offset from a guide road): the cells
/// near the target value form bands; each band longer than a minimum becomes one road from end to end, and each end
/// is joined to the nearest other road within reach (left as a dead end when there is none).
/// </summary>
public static class IsoStreets
{
    /// <param name="inBand">Whether a cell lies on the iso-line (the caller picks how wide the band is).</param>
    /// <param name="minLength">Bands whose ends lie closer than this get no road.</param>
    /// <param name="joinReach">How far from a road's end another road may be to join it.</param>
    /// <returns>The roads built, as cell paths (joins included), and how many bands found no legal path.</returns>
    public static (List<List<int>> Roads, int Failed) Build(RoadZones zones, RoadLinks links, RoadZone zone, RoadKind kind,
        Func<int, bool> inBand, float minLength, float joinReach, CancellationToken cancel)
    {
        var grid = zones.Grid;
        var band = new HashSet<int>();
        for (int k = 0; k < grid.Count; k++)
        {
            if (inBand(k) && zones.Allowed(k, zone))
                band.Add(k);
        }

        var roads = new List<List<int>>();
        int failed = 0;
        var seen = new HashSet<int>();
        foreach (int seed in band.OrderBy(k => k))
        {
            if (seen.Contains(seed))
                continue;
            var part = Steps(grid, seed, band);
            seen.UnionWith(part.Keys);
            int a = part.MaxBy(p => (p.Value, -p.Key)).Key;
            int b = Steps(grid, a, band).MaxBy(p => (p.Value, -p.Key)).Key;
            if (grid.Center(a).DistanceTo(grid.Center(b)) < minLength)
                continue;

            var existing = links.Cells.ToHashSet();
            // A band one cell wide can be closed to the search where it meets a diagonal road (a step may not cross an
            // existing link) or a corner of impassable ground; then allow one cell either side of it.
            var path = RoadSearch.Find(zones, links, a, b, zone, kind, cancel, part.ContainsKey)
                ?? RoadSearch.Find(zones, links, a, b, zone, kind, cancel, k => grid.AnyNear(k, 1, part.ContainsKey));
            if (path == null)
            {
                failed++;
                continue;
            }

            links.Mark(path, kind);
            var road = new List<int>(path);
            foreach (int end in new[] { a, b })
            {
                if (existing.Contains(end))
                    continue;
                var origin = grid.Center(end);
                var join = RoadSearch.FindNearest(zones, links, end, k => existing.Contains(k), zone, kind, cancel,
                    k => grid.Center(k).DistanceTo(origin) <= joinReach);
                if (join == null)
                    continue;
                links.Mark(join, kind);
                road.AddRange(join);
            }

            roads.Add(road);
        }

        return (roads, failed);
    }

    /// <summary>Steps (8 neighbors) from <paramref name="start"/> to every cell of the set it reaches.</summary>
    private static Dictionary<int, int> Steps(SiteGrid grid, int start, HashSet<int> cells)
    {
        var steps = new Dictionary<int, int> { [start] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out int k))
        {
            grid.ForAround(k, j =>
            {
                if (cells.Contains(j) && steps.TryAdd(j, steps[k] + 1))
                    queue.Enqueue(j);
            });
        }

        return steps;
    }
}
