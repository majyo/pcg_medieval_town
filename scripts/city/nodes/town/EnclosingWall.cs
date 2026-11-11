using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class EnclosingWallOptions
{
    [Param("余地比例", 0, 2, 0.05,
        Hint = "城内面积 = 筑墙前的建成面积 ×（1 + 余地比例）。调大后城内留出更多空地")]
    public float Margin { get; set; } = 0.25f;
    [Param("建成区优先 m", 0, 300, 10,
        Hint = "已建成的格相当于离市场近多少米。调大后城墙更愿意绕出去把沿路的房子包进来，形状更不规则")]
    public float BuiltPreference { get; set; } = 100;
    [Param("离河 m", 0, 60, 1,
        Hint = "城墙离水至少多远；靠河的一侧墙停在这条线上，河当护城河")]
    public float RiverSetback { get; set; } = 20;
    [Param("平滑半径 m", 0, 40, 2,
        Hint = "抹平墙线凹口的尺度：口宽不到它两倍的凹湾被填平，哪怕里面是不能建房的陡坡。调大后墙线更圆、更短，城内多出一些空坡")]
    public float Smoothing { get; set; } = 30;
    [Param("最少顶点间距 m", 2, 30, 1,
        Hint = "墙线上相邻转折点的最小距离，决定一段墙有多长")]
    public float MinSpacing { get; set; } = 8;
}

/// <summary>
/// The town wall: a compact piece of land around the market, as large as the town built so far plus a margin,
/// taking built cells first. It stops at a setback from the river and at the top of slopes too steep to build on (the
/// same slopes the buildability rules out, so the wall takes in no land that would stay empty), takes in junctions
/// just outside it and short stretches where a road leaves and comes back, and is smoothed into a ring. The ends of
/// long arms may stay outside.
/// </summary>
public sealed class EnclosingWall : CityNode<EnclosingWallOptions>
{
    private const float JunctionReach = 40, JunctionRadius = 20, DetourLength = 120, GrazeLength = 40;
    private const int DetourWidth = 3, MaxPasses = 3;

    /// <summary>
    /// Land this near the market and the landmark plots can be enclosed however steep: smoothing cuts the corners of
    /// the outline, and would cut into a plot standing at the top of a slope.
    /// </summary>
    private const float PlotMargin = 8;

    public override string Group => NodeGroups.Town;
    public override string Title => "城墙";
    public override string Description =>
        "以市场为中心围出一块紧凑的城内范围（筑墙前的建成面积再加余地），优先围住已建成的格；靠河停在离河线上，不能建房的陡坡停在坡顶；" +
        "把墙外不远的路口和出墙又回来的路段包进来，再平滑成墙线。长臂的末端可以留在墙外。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Growth, Keys.Plaza, Keys.Landmarks, Keys.Routes, Keys.Shore, Keys.Buildable];
    public override IReadOnlyList<IKey> Writes => [Keys.Wall];

    protected override void Run(NodeContext c, EnclosingWallOptions o)
    {
        var growth = c.Get(Keys.Growth);
        var network = c.Get(Keys.Routes);
        var shore = c.Get(Keys.Shore).Values;
        var buildable = c.Get(Keys.Buildable);
        var grid = growth.Grid;

        var required = grid.CellsIn(c.Get(Keys.Plaza).Area).Concat(c.Get(Keys.Landmarks).Cells()).Distinct().ToList();
        var byPlots = Proximity.From(grid, required, PlotMargin);
        bool Enclosable(int k) => shore[k] >= o.RiverSetback
            && (buildable.Block(k) != SiteBlock.Slope || float.IsFinite(byPlots[k]));
        int built = growth.BuiltCells.Count();
        int target = (int)MathF.Round(built * (1 + o.Margin));

        var inside = Compact(grid, required, Enclosable, k => growth.Built(k) ? o.BuiltPreference : 0, target, c);
        bool Dry(int k) => shore[k] >= 0;
        TakeInRoads(grid, network, inside, k => inside[k], Dry);
        int r = grid.Span(o.Smoothing, 0);
        Vector2[] Shape()
        {
            if (r > 0)
            {
                var closed = Morphology.Close(grid, inside, r);
                for (int k = 0; k < grid.Count; k++)
                    inside[k] = inside[k] || closed[k] && shore[k] >= o.RiverSetback / 2;
            }

            Morphology.FillHoles(grid, inside);
            Morphology.KeepConnected(grid, inside, required);
            return Outline(grid, inside, o.MinSpacing);
        }

        // Smoothing moves the line, so check the routes against the finished ring and repeat while a route still
        // leaves and comes back, or grazes the town, within a short stretch.
        var ring = Shape();
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            var interior = Region.Polygon(ring);
            var cache = new Dictionary<int, bool>();
            bool In(int k) => cache.TryGetValue(k, out bool v) ? v : cache[k] = interior.Contains(grid.Center(k));
            if (!TakeInRoads(grid, network, inside, In, Dry))
                break;
            ring = Shape();
        }

        var wall = new WallRing(ring);
        int enclosed = growth.BuiltCells.Count(k => wall.Interior.Contains(grid.Center(k)));
        c.Set(Keys.Wall, new WallRing(ring, built > 0 ? enclosed / (float)built : null));
    }

    /// <summary>
    /// Cells taken from <paramref name="required"/> outward, cheapest first, each a side neighbor of one already
    /// taken, until <paramref name="target"/>: a cell costs its path length from the required cells over enclosable
    /// land, less its <paramref name="preference"/> in meters.
    /// </summary>
    private static bool[] Compact(SiteGrid grid, List<int> required, Func<int, bool> enclosable, Func<int, float> preference,
        int target, NodeContext c)
    {
        var distance = Proximity.From(grid, required, float.PositiveInfinity, enclosable);
        var inside = new bool[grid.Count];
        var frontier = new PriorityQueue<int, float>();
        var queued = new bool[grid.Count];
        void Push(int k)
        {
            if (!queued[k] && !inside[k] && float.IsFinite(distance[k]))
            {
                queued[k] = true;
                frontier.Enqueue(k, distance[k] - preference(k));
            }
        }

        foreach (int k in required)
            inside[k] = true;
        foreach (int k in required)
            grid.ForSides(k, Push);
        int count = required.Count;
        while (count < target && frontier.TryDequeue(out int k, out _))
        {
            inside[k] = true;
            count++;
            grid.ForSides(k, Push);
        }

        if (count < target)
            c.Notice($"可围的地不足，城内只有 {grid.Hectares(count):0.0} ha");
        return inside;
    }

    /// <summary>
    /// Takes in the land around junctions just outside the town, so their roads leave through one gate; the stretches
    /// where a road leaves and comes back within <see cref="DetourLength"/>, so it does not cross the wall three
    /// times; and the stretches where a road outside dips in for less than <see cref="GrazeLength"/>, so it does not
    /// get two gates a few meters apart. <paramref name="isInside"/> says which cells the town holds now. Returns
    /// whether any cell was added.
    /// </summary>
    private static bool TakeInRoads(SiteGrid grid, RouteNetwork network, bool[] inside, Func<int, bool> isInside,
        Func<int, bool> dry)
    {
        bool added = false;
        void Disc(int center, float radius)
        {
            int r = (int)MathF.Ceiling(radius / grid.CellSize);
            int cx = center % grid.Nx, cz = center / grid.Nx;
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (grid.InGrid(x, z) && (dx * dx + dz * dz) * grid.CellSize * grid.CellSize <= radius * radius &&
                        dry(grid.Index(x, z)) && !inside[grid.Index(x, z)])
                    {
                        inside[grid.Index(x, z)] = true;
                        added = true;
                    }
                }
            }
        }

        int reach = (int)MathF.Ceiling(JunctionReach / grid.CellSize);
        foreach (int j in network.Junctions.Where(j => !isInside(j) && grid.AnyNear(j, reach, k => inside[k])).ToList())
            Disc(j, JunctionRadius);

        foreach (var route in network.Routes)
        {
            var cells = route.Cells;
            for (int a = 0; a + 1 < cells.Length; a++)
            {
                // A run of cells on the other side from cells[a], ended by a cell back on cells[a]'s side.
                bool side = isInside(cells[a]);
                if (side == isInside(cells[a + 1]))
                    continue;
                float limit = side ? DetourLength : GrazeLength, length = 0;
                int b = a + 1;
                for (; b < cells.Length && isInside(cells[b]) != side && length <= limit; b++)
                    length += grid.Center(cells[b - 1]).DistanceTo(grid.Center(cells[b]));
                if (b >= cells.Length || isInside(cells[b]) != side || length > limit)
                    continue;
                for (int i = a + 1; i < b; i++)
                    Disc(cells[i], DetourWidth * grid.CellSize);
                a = b - 1;
            }
        }

        return added;
    }

    /// <summary>
    /// The outer boundary of the cells, along cell edges, cut three times at the corners (Chaikin), simplified and thinned so
    /// that neighboring points are at least <paramref name="spacing"/> apart. Cutting corners only moves the line
    /// inward, so it stays on the cells.
    /// </summary>
    private static Vector2[] Outline(SiteGrid grid, bool[] inside, float spacing)
    {
        var part = grid.Squares(Enumerable.Range(0, grid.Count).Where(k => inside[k])).Parts().MaxBy(p => p.Area)
            ?? throw new InvalidOperationException("城内范围为空");
        // A part's first ring is its outer one; the rest are holes.
        var ring = part.Rings[0];
        for (int pass = 0; pass < 3; pass++)
            ring = Chaikin(ring);
        // A tolerance of most of a cell removes the stair steps of the cell edges.
        var simplified = Geometry.Simplify(ring.Append(ring[0]).ToList(), grid.CellSize * 0.75f);
        var thinned = new List<Vector2>();
        foreach (var p in simplified.Take(simplified.Length - 1))
        {
            if (thinned.Count == 0 || thinned[^1].DistanceTo(p) >= spacing)
                thinned.Add(p);
        }

        while (thinned.Count > 3 && thinned[^1].DistanceTo(thinned[0]) < spacing)
            thinned.RemoveAt(thinned.Count - 1);
        return thinned.ToArray();
    }

    private static Vector2[] Chaikin(Vector2[] ring)
    {
        var result = new Vector2[ring.Length * 2];
        for (int i = 0; i < ring.Length; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Length];
            result[2 * i] = p.Lerp(q, 0.25f);
            result[2 * i + 1] = p.Lerp(q, 0.75f);
        }

        return result;
    }
}
