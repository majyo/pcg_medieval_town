using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PcgCitySandbox.City;

/// <summary>
/// Streets along an iso-line of some value per cell (a distance to the wall, an offset from a guide road): the cells
/// near the target value form bands; each band longer than a minimum becomes one road from end to end, and each end
/// is joined to the nearest other road within reach. A road that would touch no other road is joined from one end
/// to the nearest road at any distance, or left out, so the network stays connected.
/// </summary>
public static class IsoStreets
{
    /// <param name="band">The cells on the iso-line (the caller picks how wide the band is); cells roads may not use
    /// are dropped.</param>
    /// <param name="minLength">Bands whose ends lie closer than this get no road.</param>
    /// <param name="joinReach">How far from a road's end another road may be to join it.</param>
    /// <param name="within">Which cells a road or its joins may run through besides the band (on top of the zone); null
    /// for any.</param>
    /// <returns>The roads built, as cell paths from end to end (without their joins), and how many bands found no
    /// legal path or no way to the network.</returns>
    public static (List<int[]> Roads, int Failed) Build(RoadZones zones, RoadLinks links, RoadZone zone, RoadKind kind,
        IEnumerable<int> band, float minLength, float joinReach, CancellationToken cancel, Func<int, bool>? within = null)
    {
        var grid = zones.Grid;
        var cells = band.Where(k => zones.Allowed(k, zone)).ToHashSet();
        var roads = new List<int[]>();
        int failed = 0;
        var seen = new HashSet<int>();
        foreach (int seed in cells.OrderBy(k => k))
        {
            if (seen.Contains(seed))
                continue;
            var part = Steps(grid, seed, cells);
            seen.UnionWith(part.Keys);
            int a = part.MaxBy(p => (p.Value, -p.Key)).Key;
            int b = Steps(grid, a, cells).MaxBy(p => (p.Value, -p.Key)).Key;
            if (grid.Center(a).DistanceTo(grid.Center(b)) < minLength)
                continue;

            // A band one cell wide can be closed to the search where it meets a diagonal road (a step may not cross an
            // existing link) or a corner of impassable ground; then allow one cell either side of it.
            var path = RoadSearch.Find(zones, links, a, b, zone, kind, cancel, part.ContainsKey)
                ?? RoadSearch.Find(zones, links, a, b, zone, kind, cancel,
                    k => grid.AnyNear(k, 1, part.ContainsKey) && (part.ContainsKey(k) || within == null || within(k)));
            if (path == null)
            {
                failed++;
                continue;
            }

            var existing = links.Cells.ToHashSet();
            var joins = new List<List<int>>();
            foreach (int end in new[] { a, b }.Where(e => !existing.Contains(e)))
            {
                var origin = grid.Center(end);
                if (RoadSearch.FindNearest(zones, links, end, existing.Contains, zone, kind, cancel,
                    k => grid.Center(k).DistanceTo(origin) <= joinReach && (within == null || within(k))) is { } join)
                    joins.Add(join);
            }

            if (joins.Count == 0 && !path.Any(existing.Contains))
            {
                if (RoadSearch.FindNearest(zones, links, a, existing.Contains, zone, kind, cancel, within) is not { } far)
                {
                    failed++;
                    continue;
                }

                joins.Add(far);
            }

            links.Mark(path, kind);
            foreach (var join in joins)
                links.Mark(join, kind);
            roads.Add(path.ToArray());
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
