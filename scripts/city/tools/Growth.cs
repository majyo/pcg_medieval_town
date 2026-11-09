using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PcgCitySandbox.City;

/// <summary>
/// How a growth phase spreads from its roads. A cell's cost is the distance along the road from the seed to where it
/// leaves the road, plus <see cref="DepthCost"/> per meter away from the road; past <see cref="Depth"/> each meter
/// costs <see cref="DeepCost"/> more. Good land is cheaper by <see cref="HabitabilityWeight"/> meters times its
/// habitability. So small phases are bands along the roads, and only large ones fill in behind the frontage.
/// </summary>
public sealed record GrowthRules(float Depth, float DepthCost, float HabitabilityWeight, float DeepCost = 12);

/// <summary>
/// Grows a town along its roads (the through routes): the shared step of every growth phase. A phase node decides
/// where to grow from and how much; <see cref="Spread"/> decides which cells.
/// </summary>
public static class Growth
{
    /// <summary>
    /// Distance along the network's links from the nearest seed cell to every route cell (infinite where no link
    /// leads from a seed, and for cells off the network).
    /// </summary>
    public static float[] AlongRoad(RouteNetwork network, IEnumerable<int> seeds)
    {
        var grid = network.Grid;
        var neighbors = new Dictionary<int, List<int>>();
        foreach (var (a, b) in network.Links)
        {
            (neighbors.TryGetValue(a, out var la) ? la : neighbors[a] = new List<int>()).Add(b);
            (neighbors.TryGetValue(b, out var lb) ? lb : neighbors[b] = new List<int>()).Add(a);
        }

        var along = new float[grid.Count];
        Array.Fill(along, float.PositiveInfinity);
        var open = new PriorityQueue<int, float>();
        foreach (int s in seeds)
        {
            along[s] = 0;
            open.Enqueue(s, 0);
        }

        while (open.TryDequeue(out int k, out float d))
        {
            if (d > along[k] || !neighbors.TryGetValue(k, out var next))
                continue;
            foreach (int j in next)
            {
                float nd = d + grid.Center(k).DistanceTo(grid.Center(j));
                if (nd < along[j])
                {
                    along[j] = nd;
                    open.Enqueue(j, nd);
                }
            }
        }

        return along;
    }

    /// <summary>
    /// Picks <paramref name="count"/> unbuilt cells for a new phase, cheapest first under <paramref name="rules"/>,
    /// growing from the network cells reachable along the road from <paramref name="seeds"/> and from the cells
    /// already built (<paramref name="built"/>). Every picked cell is buildable, allowed, and joined by side neighbors
    /// to a route cell or a built cell, so the town never jumps across a gully. Returns fewer cells when the land runs
    /// out.
    /// </summary>
    public static List<int> Spread(RouteNetwork network, CategoryField buildable, ScalarField habitability, bool[] built,
        IEnumerable<int> seeds, int count, GrowthRules rules, Func<int, bool>? allowed, CancellationToken cancel)
    {
        var grid = network.Grid;
        bool Open(int k) => !built[k] && buildable.Buildable(k) && (allowed == null || allowed(k));
        var score = Score(network, buildable, habitability, Growth.AlongRoad(network, seeds), rules, allowed, cancel);

        // Grow from the road and the built area, cheapest frontier cell first.
        var frontier = new PriorityQueue<int, float>();
        var queued = new bool[grid.Count];
        void Push(int k)
        {
            if (!queued[k] && Open(k) && float.IsFinite(score[k]))
            {
                queued[k] = true;
                frontier.Enqueue(k, score[k]);
            }
        }

        foreach (int k in network.Cells)
            Push(k);
        for (int k = 0; k < grid.Count; k++)
        {
            if (built[k])
                ForSides(grid, k, Push);
        }

        var picked = new List<int>(count);
        while (picked.Count < count && frontier.TryDequeue(out int k, out _))
        {
            picked.Add(k);
            ForSides(grid, k, Push);
        }

        return picked;
    }

    /// <summary>
    /// The cost of every cell reachable from a road cell through buildable, allowed land: the cheapest
    /// along-road distance plus <see cref="GrowthRules.DepthCost"/> per meter off the road (Dijkstra), then the
    /// penalty past the frontage depth and the habitability discount. Unreachable cells are infinite.
    /// </summary>
    private static float[] Score(RouteNetwork network, CategoryField buildable, ScalarField habitability, float[] along,
        GrowthRules rules, Func<int, bool>? allowed, CancellationToken cancel)
    {
        var grid = network.Grid;
        var cost = new float[grid.Count];
        var off = new float[grid.Count];
        Array.Fill(cost, float.PositiveInfinity);
        var open = new PriorityQueue<int, float>();
        foreach (int k in network.Cells)
        {
            if (float.IsFinite(along[k]))
            {
                cost[k] = along[k];
                open.Enqueue(k, along[k]);
            }
        }

        float diagonal = grid.CellSize * MathF.Sqrt(2);
        int popped = 0;
        while (open.TryDequeue(out int k, out float c))
        {
            if (c > cost[k])
                continue;
            if (++popped % 8192 == 0)
                cancel.ThrowIfCancellationRequested();
            int x = k % grid.Nx, z = k / grid.Nx;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int jx = x + dx, jz = z + dz;
                    if ((dx | dz) == 0 || !grid.InGrid(jx, jz))
                        continue;
                    int j = grid.Index(jx, jz);
                    if (!buildable.Buildable(j) || allowed != null && !allowed(j))
                        continue;
                    float step = dx != 0 && dz != 0 ? diagonal : grid.CellSize;
                    float nc = c + rules.DepthCost * step;
                    if (nc < cost[j])
                    {
                        cost[j] = nc;
                        off[j] = off[k] + step;
                        open.Enqueue(j, nc);
                    }
                }
            }
        }

        var score = new float[grid.Count];
        for (int k = 0; k < grid.Count; k++)
        {
            score[k] = cost[k] + rules.DeepCost * MathF.Max(0, off[k] - rules.Depth)
                - rules.HabitabilityWeight * habitability.Values[k];
        }

        return score;
    }

    /// <summary>Calls <paramref name="visit"/> on each side neighbor (up, down, left, right) of cell k.</summary>
    public static void ForSides(SiteGrid grid, int k, Action<int> visit)
    {
        int x = k % grid.Nx, z = k / grid.Nx;
        if (x > 0)
            visit(k - 1);
        if (x < grid.Nx - 1)
            visit(k + 1);
        if (z > 0)
            visit(k - grid.Nx);
        if (z < grid.Nz - 1)
            visit(k + grid.Nx);
    }

    /// <summary>The network cells nearest to <paramref name="cells"/>: those among them, or else the one closest to their middle.</summary>
    public static List<int> RoadSeeds(RouteNetwork network, IReadOnlyCollection<int> cells)
    {
        var seeds = cells.Where(network.Cells.Contains).ToList();
        if (seeds.Count > 0 || cells.Count == 0)
            return seeds;
        var grid = network.Grid;
        var middle = cells.Aggregate(Godot.Vector2.Zero, (sum, k) => sum + grid.Center(k)) / cells.Count;
        return new List<int> { network.Cells.MinBy(k => (grid.Center(k).DistanceSquaredTo(middle), k)) };
    }

    /// <summary>The cells whose centers lie in <paramref name="area"/>.</summary>
    public static List<int> CellsIn(SiteGrid grid, Region area)
    {
        var cells = new List<int>();
        var bounds = area.Bounds;
        int x0 = Math.Max(0, (int)MathF.Floor((bounds.Position.X - grid.Origin.X) / grid.CellSize));
        int z0 = Math.Max(0, (int)MathF.Floor((bounds.Position.Y - grid.Origin.Y) / grid.CellSize));
        int x1 = Math.Min(grid.Nx - 1, (int)MathF.Floor((bounds.End.X - grid.Origin.X) / grid.CellSize));
        int z1 = Math.Min(grid.Nz - 1, (int)MathF.Floor((bounds.End.Y - grid.Origin.Y) / grid.CellSize));
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (area.Contains(grid.Center(x, z)))
                    cells.Add(grid.Index(x, z));
            }
        }

        return cells;
    }
}
