using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class MarketPlaceOptions
{
    [Param("市场面积 m²", 600, 6000, 100,
        Hint = "市场的目标面积；地形不允许时会缩小")]
    public float Area { get; set; } = 2000;
    [Param("纺锤长宽比", 1.5, 6, 0.1,
        Hint = "不在路口时，市场是道路加宽成的纺锤形，长度是最宽处的几倍")]
    public float Elongation { get; set; } = 3;
    [Param("搜索范围 m", 40, 400, 10,
        Hint = "不在路口时，在离路口（村落里没有路口时为起源点）多远以内的路上找位置")]
    public float Reach { get; set; } = 150;
}

/// <summary>
/// The market, opened where the village's roads meet: at a junction inside the village it is the triangle between
/// the roads that meet there; elsewhere the road widens into a spindle on level ground near the junction (or the
/// origin). Shrunk until it stands on buildable land.
/// </summary>
public sealed class MarketPlace : CityNode<MarketPlaceOptions>
{
    private const float MinScale = 0.5f, Shrink = 0.9f;

    public override string Group => TownGrowth.Group;
    public override string Title => "市场";
    public override string Description =>
        "在村落里最靠近道路交汇处的平地上开出市场：在路口是几条路之间的三角形，不在路口时是道路加宽成的纺锤形；地形不允许时缩小。";
    public override IReadOnlyList<IKey> Reads => [Keys.Origin, Keys.Routes, Keys.Growth, Keys.Buildable, Keys.Slope];
    public override IReadOnlyList<IKey> Writes => [Keys.Plaza];

    protected override void Run(NodeContext c, MarketPlaceOptions o)
    {
        var origin = c.Get(Keys.Origin);
        var network = c.Get(Keys.Routes);
        var growth = c.Get(Keys.Growth);
        var buildable = c.Get(Keys.Buildable);
        var slope = c.Get(Keys.Slope);
        var grid = buildable.Grid;

        // A junction counts as in the village when a built cell lies within two cells of it.
        bool NearVillage(int k) => Enumerable.Range(-2, 5).Any(dz => Enumerable.Range(-2, 5).Any(dx =>
        {
            int x = k % grid.Nx + dx, z = k / grid.Nx + dz;
            return grid.InGrid(x, z) && growth.Built(grid.Index(x, z));
        }));
        int junction = network.Junctions.Where(NearVillage).OrderBy(k => grid.Center(k).DistanceTo(origin.Position))
            .DefaultIfEmpty(-1).First();
        var target = junction >= 0 ? grid.Center(junction) : origin.Position;

        (Vector2[] Ring, float Scale)? Fit(Vector2[] ring, Vector2 center)
        {
            for (float s = 1; s >= MinScale; s *= Shrink)
            {
                var scaled = ring.Select(p => center + (p - center) * s).ToArray();
                var cells = Growth.CellsIn(grid, Region.Polygon(scaled));
                if (cells.Count > 0 && cells.All(buildable.Buildable))
                    return (scaled, s);
            }

            return null;
        }

        if (junction >= 0 && Triangle(network, target, o.Area) is { } triangle)
        {
            var centroid = triangle.Aggregate(Vector2.Zero, (a, p) => a + p) / triangle.Length;
            if (Fit(triangle, centroid) is { Scale: >= 0.7f } fitted)
            {
                Set(c, fitted.Ring, centroid, o.Area);
                return;
            }
        }

        // Spindles centered on village road cells near the target: close, level and full-sized is best.
        var routeOf = new Dictionary<int, Route>();
        foreach (var route in network.Routes)
        {
            foreach (int k in route.Cells)
                routeOf.TryAdd(k, route);
        }

        float width = MathF.Sqrt(MathF.PI * o.Area / (2 * o.Elongation));
        (Vector2[] Ring, Vector2 Center)? best = null;
        float bestScore = float.PositiveInfinity;
        foreach (int k in network.Cells.Where(k => growth.Phase[k] == 1 && buildable.Buildable(k)).OrderBy(k => k))
        {
            float distance = grid.Center(k).DistanceTo(target);
            if (distance > o.Reach || Spindle(routeOf[k].Points, grid.Center(k), width * o.Elongation, width) is not { } spindle)
                continue;
            if (Fit(spindle.Ring, spindle.Center) is not { } fitted)
                continue;
            var cells = Growth.CellsIn(grid, Region.Polygon(fitted.Ring));
            float score = distance + 4 * cells.Average(j => slope.Values[j]) + 100 * (1 - fitted.Scale);
            if (score < bestScore)
            {
                bestScore = score;
                best = (fitted.Ring, spindle.Center);
            }
        }

        if (best is { } chosen)
        {
            Set(c, chosen.Ring, chosen.Center, o.Area);
            return;
        }

        // Nothing fits: one cell at the origin, which is buildable.
        c.Notice("村落里没有能放下市场的平地，市场只占起源点一格");
        float h = grid.CellSize / 2;
        var p0 = grid.Center(origin.Cell);
        Set(c, new[] { p0 + new Vector2(-h, -h), p0 + new Vector2(h, -h), p0 + new Vector2(h, h), p0 + new Vector2(-h, h) }, p0, o.Area);
    }

    private static void Set(NodeContext c, Vector2[] ring, Vector2 center, float target)
    {
        var area = Region.Polygon(ring);
        if (area.Area < target * 0.9f)
            c.Notice($"市场周围空地不足，缩小到 {area.Area:0} m²");
        c.Set(Keys.Plaza, new Plaza(area, center));
    }

    /// <summary>
    /// The market at a junction: the polygon through a point on each road leaving it, at the same distance along each
    /// road, sized for <paramref name="area"/>. Null when fewer than three distinct roads leave the junction.
    /// </summary>
    private static Vector2[]? Triangle(RouteNetwork network, Vector2 junction, float area)
    {
        var arms = new List<(Vector2[] Line, float At, int Way, Vector2 Direction)>();
        foreach (var route in network.Routes)
        {
            var (at, distance) = Project(route.Points, junction);
            if (distance > 2 * network.Grid.CellSize)
                continue;
            float total = Length(route.Points);
            foreach (int way in new[] { 1, -1 })
            {
                if ((way > 0 ? total - at : at) < 10)
                    continue;
                var direction = (PointAt(route.Points, at + way * 10) - junction).Normalized();
                if (arms.All(a => MathF.Abs(a.Direction.AngleTo(direction)) > 25 * MathF.PI / 180))
                    arms.Add((route.Points, at, way, direction));
            }
        }

        if (arms.Count < 3)
            return null;
        Vector2[] Ring(float reach) => arms.Select(a => PointAt(a.Line, a.At + a.Way * reach))
            .OrderBy(p => (p - junction).Angle()).ToArray();
        // The area grows with the square of the reach; measure it at 40 m and scale.
        float unit = (float)Region.Polygon(Ring(40)).Area;
        return unit < 1 ? null : Ring(40 * MathF.Sqrt(area / unit));
    }

    /// <summary>
    /// The road widened into a spindle: <paramref name="length"/> along the route centered on the point nearest
    /// <paramref name="near"/>, widest (<paramref name="width"/>) in the middle and road-wide at the ends. Null when the
    /// route is too short.
    /// </summary>
    private static (Vector2[] Ring, Vector2 Center)? Spindle(Vector2[] line, Vector2 near, float length, float width)
    {
        const int stations = 16;
        const float roadHalf = 3;
        float total = Length(line);
        if (total < length)
            return null;
        float middle = Math.Clamp(Project(line, near).At, length / 2, total - length / 2);
        var left = new List<Vector2>();
        var right = new List<Vector2>();
        for (int i = 0; i <= stations; i++)
        {
            float t = i / (float)stations;
            float s = middle - length / 2 + t * length;
            var p = PointAt(line, s);
            var tangent = (PointAt(line, s + 1) - PointAt(line, s - 1)).Normalized();
            float half = MathF.Max(roadHalf, width / 2 * MathF.Sin(MathF.PI * t));
            left.Add(p + tangent.Orthogonal() * half);
            right.Add(p - tangent.Orthogonal() * half);
        }

        right.Reverse();
        return (left.Concat(right).ToArray(), PointAt(line, middle));
    }

    private static float Length(Vector2[] line) => line.Zip(line.Skip(1), (a, b) => a.DistanceTo(b)).Sum();

    /// <summary>Distance along the polyline to the point nearest <paramref name="p"/>, and how far that point is.</summary>
    private static (float At, float Distance) Project(Vector2[] line, Vector2 p)
    {
        float along = 0, bestAt = 0, bestDistance = float.PositiveInfinity;
        for (int i = 1; i < line.Length; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            float length = a.DistanceTo(b);
            float t = length > 0 ? Math.Clamp((p - a).Dot(b - a) / (length * length), 0, 1) : 0;
            float distance = p.DistanceTo(a.Lerp(b, t));
            if (distance < bestDistance)
                (bestDistance, bestAt) = (distance, along + t * length);
            along += length;
        }

        return (bestAt, bestDistance);
    }

    /// <summary>The point <paramref name="at"/> meters along the polyline, clamped to its ends.</summary>
    private static Vector2 PointAt(Vector2[] line, float at)
    {
        if (at <= 0)
            return line[0];
        for (int i = 1; i < line.Length; i++)
        {
            float length = line[i - 1].DistanceTo(line[i]);
            if (at <= length)
                return line[i - 1].Lerp(line[i], length > 0 ? at / length : 0);
            at -= length;
        }

        return line[^1];
    }
}
