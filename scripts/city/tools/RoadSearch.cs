using System.Collections.Generic;
using System.Threading;

namespace PcgCitySandbox.City;

/// <summary>
/// A* over the site grid (8 neighbors) inside one road zone. Steeper and forested cells cost more; reusing an
/// existing link is cheaper, so new roads merge into old ones. Diagonals may not cut a corner of impassable
/// terrain or cross an existing link.
/// </summary>
public static class RoadSearch
{
    public static List<int>? Find(RoadZones zones, RoadLinks links, int start, int goal, RoadZone zone, RoadKind kind,
        CancellationToken cancel)
    {
        var grid = zones.Grid;
        var slope = zones.Slope.Values;
        if (start < 0 || goal < 0 || !zones.Allowed(start, zone) || !zones.Allowed(goal, zone))
            return null;

        int nx = grid.Nx;
        var cost = new Dictionary<int, float> { [start] = 0 };
        var from = new Dictionary<int, int>();
        var closed = new HashSet<int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(start, 0);
        int expanded = 0;
        while (open.TryDequeue(out int k, out _))
        {
            if (!closed.Add(k))
                continue;
            if (k == goal)
            {
                var path = new List<int> { k };
                while (k != start)
                    path.Add(k = from[k]);
                path.Reverse();
                return path;
            }

            if (++expanded % 4096 == 0)
                cancel.ThrowIfCancellationRequested();

            int x = k % nx, z = k / nx;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0 || !grid.InGrid(x + dx, z + dz))
                        continue;
                    int j = (z + dz) * nx + x + dx;
                    if (closed.Contains(j) || !zones.Allowed(j, zone))
                        continue;
                    if (dx != 0 && dz != 0)
                    {
                        int side1 = z * nx + x + dx, side2 = (z + dz) * nx + x;
                        if (!zones.Passable(side1) || !zones.Passable(side2) || links.TryGet(side1, side2, out _))
                            continue;
                    }

                    float step = (dx != 0 && dz != 0 ? 1.4142f : 1) * grid.CellSize;
                    float factor = 1 + slope[j] / 12 + (zones.Buildable.Block(j) is SiteBlock.Forest ? 0.5f : 0);
                    if (links.TryGet(k, j, out var existing))
                        factor *= kind == RoadKind.Street || existing == kind ? 0.5f : 0.8f;
                    float next = cost[k] + step * factor;
                    if (cost.TryGetValue(j, out float old) && old <= next)
                        continue;
                    cost[j] = next;
                    from[j] = k;
                    open.Enqueue(j, next + grid.Center(j).DistanceTo(grid.Center(goal)) * 0.5f);
                }
        }

        return null;
    }
}
