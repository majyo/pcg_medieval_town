using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class RoadProfilesOptions
{
    [Param("修整道路",
        Hint = "关掉后道路和广场照旧贴着原地形，地面不动")]
    public bool Enabled { get; set; } = true;
    [Param("主街坡度上限 %", 3, 20, 0.5,
        Hint = "主街纵向坡度的上限。调小后街更平，但挖填更多；挖填超过上限的路段保留原坡，记为陡坡段")]
    public float MainGrade { get; set; } = 8;
    [Param("次街坡度上限 %", 3, 25, 0.5,
        Hint = "次街纵向坡度的上限")]
    public float StreetGrade { get; set; } = 10;
    [Param("巷坡度上限 %", 3, 30, 0.5,
        Hint = "巷纵向坡度的上限；超过的陡坡段可以做成台阶")]
    public float LaneGrade { get; set; } = 15;
    [Param("城外道路坡度上限 %", 3, 25, 0.5,
        Hint = "城外道路纵向坡度的上限")]
    public float OuterGrade { get; set; } = 10;
    [Param("便道坡度上限 %", 3, 30, 0.5,
        Hint = "通往作坊和城外地标的土路纵向坡度的上限")]
    public float TrackGrade { get; set; } = 15;
    [Param("挖填上限 m", 0, 4, 0.1,
        Hint = "路中线最多比原地形挖下或填高多少。调大后更多路段能压到坡度上限以内，路堑和路堤也更深；0 等于不修整")]
    public float MaxEarthwork { get; set; } = 1.5f;
    [Param("市场坡度上限 %", 0, 8, 0.5,
        Hint = "市场整成一个平面，它最多倾斜多少（排水，也少挖填）。0 是完全水平")]
    public float MarketGrade { get; set; } = 3;
    [Param("平滑长度 m", 0, 60, 2,
        Hint = "路面上比它短的起伏被抹平")]
    public float Smoothing { get; set; } = 20;

    /// <summary>The grade limit of a road class, as rise over run.</summary>
    public float Grade(RoadKind kind) => 0.01f * kind switch
    {
        RoadKind.Main => MainGrade,
        RoadKind.Outer => OuterGrade,
        RoadKind.Lane => LaneGrade,
        RoadKind.Track => TrackGrade,
        _ => StreetGrade,
    };
}

/// <summary>
/// Grades the roads and the paved squares. The market becomes one plane, tilted no more than allowed. Every junction
/// gets a height near the terrain's, moved until the road between two junctions is no steeper than its class allows,
/// but never farther from the terrain than the earthwork limit. Along each road the surface is the terrain smoothed,
/// then held to the grade limit; where that would cut or fill deeper than the limit the road keeps its slope and is
/// marked steep. Across its width a road is level. The small squares take the plane of the road through them.
/// </summary>
public sealed class RoadProfiles : CityNode<RoadProfilesOptions>
{
    /// <summary>Distance between the points of a road's profile.</summary>
    private const float Station = Bridges.Station;
    private const int Rounds = 200;

    /// <summary>How far past the earthwork limit a grade-limited stretch may go before it is given up as steep.</summary>
    public const float Slack = 0.25f;

    public override string Group => NodeGroups.Grading;
    public override string Title => "道路纵断面";
    public override string Description =>
        "把道路和铺装的广场修平：市场整成一个略带坡的平面；路口定高度，路段纵向坡度不超过各等级的上限、横向取平；" +
        "压到上限以内要挖填太深的路段保留原坡，记为陡坡段。";
    public override IReadOnlyList<IKey> Reads => [Keys.Roads, Keys.Plaza, Keys.Squares, Keys.Shore, Keys.Bridges];
    public override IReadOnlyList<IKey> Writes => [Keys.Grading];

    protected override void Run(NodeContext c, RoadProfilesOptions o)
    {
        var roads = c.Get(Keys.Roads);
        var plaza = c.Get(Keys.Plaza);
        var shore = c.Get(Keys.Shore);
        var terrain = c.Terrain;
        if (!o.Enabled || o.MaxEarthwork <= 0)
        {
            c.Set(Keys.Grading, GradePlan.None(terrain));
            return;
        }

        bool Water(Vector2 p) => shore.Nearest(p) < 0;
        var bridges = c.Get(Keys.Bridges).Bridges;
        var market = FitPlane(plaza.Area, terrain.HeightAt, o.MarketGrade * 0.01f);

        // Junction heights: the terrain's, the market's inside the market; then relaxed along the roads.
        var ground = roads.Nodes.Select(n => terrain.HeightAt(n.Position)).ToArray();
        var pinned = roads.Nodes.Select(n => plaza.Area.Contains(n.Position)).ToArray();
        var height = roads.Nodes.Select((n, i) => pinned[i] ? market.HeightAt(n.Position) : ground[i]).ToArray();
        foreach (var bridge in bridges)
        {
            // A junction on a deck (a road's end at the bridgehead) stands at the deck's height.
            foreach (var node in roads.Nodes.Where(n => bridge.Deck.Any(d => d.DistanceSquaredTo(n.Position) < 0.25f)))
                (pinned[node.Id], height[node.Id]) = (true, bridge.Height);
        }
        for (int round = 0; round < Rounds; round++)
        {
            float moved = 0;
            foreach (var edge in roads.Edges)
            {
                int a = edge.From, b = edge.To;
                float allowed = o.Grade(edge.Kind) * edge.Length;
                float excess = MathF.Abs(height[b] - height[a]) - allowed;
                if (a == b || excess <= 0 || pinned[a] && pinned[b])
                    continue;
                float sign = MathF.Sign(height[b] - height[a]);
                float shareA = pinned[a] ? 0 : pinned[b] ? 1 : 0.5f;
                moved = MathF.Max(moved, Move(a, sign * excess * shareA));
                moved = MathF.Max(moved, Move(b, -sign * excess * (1 - shareA)));
            }

            if (moved < 0.01f)
                break;
        }

        float Move(int node, float by)
        {
            float before = height[node];
            height[node] = Math.Clamp(before + by, ground[node] - o.MaxEarthwork, ground[node] + o.MaxEarthwork);
            return MathF.Abs(height[node] - before);
        }

        var corridors = new List<GradeCorridor>();
        foreach (var edge in roads.Edges)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            corridors.AddRange(Profile(edge, height[edge.From], height[edge.To]));
        }

        // The small squares take the plane of the graded road through them, so road and square are one surface.
        var platforms = new List<GradePlatform> { new(PlatformKind.Market, "market", plaza.Area, market) };
        foreach (var square in c.Get(Keys.Squares).Squares)
        {
            var samples = new List<(Vector2 P, float H)>();
            var reach = square.Area.Offset(2);
            foreach (var corridor in corridors.Where(k => Box(k.Points).Grow(k.Width).Intersects(reach.Bounds)))
            {
                for (int i = 0; i < corridor.Points.Length; i++)
                {
                    if (reach.Contains(corridor.Points[i]))
                        samples.Add((corridor.Points[i], corridor.Heights[i]));
                }
            }

            if (samples.Count > 0)
                platforms.Add(new GradePlatform(PlatformKind.Square, square.Of, square.Area, FitPlane(samples, o.Grade(RoadKind.Main))));
        }

        var patch = GradeBake.Bake(terrain, corridors, platforms, Water, c.Cancellation);
        var plan = new GradePlan(terrain, corridors, platforms, [], patch, o.MaxEarthwork);
        float total = corridors.Sum(k => k.Length);
        if (total > 0 && plan.SteepLength > total * 0.1f)
            c.Notice($"陡坡段 {plan.SteepLength:0} m，占道路的 {plan.SteepLength / total:P0}");
        c.Set(Keys.Grading, plan);

        // One edge's profile, cut where it enters and leaves the market: each piece is a corridor of its own.
        IEnumerable<GradeCorridor> Profile(RoadEdge edge, float from, float to)
        {
            var points = Geometry.Stations(edge.Points, Station);
            int n = points.Length;
            var raw = new float[n];
            var wet = new bool[n];
            var fixedAt = new float?[n];
            // Points whose height is given: in the market's plane, or on a bridge's deck.
            var inMarket = new bool[n];
            // How far a point may leave the terrain: the earthwork limit, more on the approach to a bridge.
            var room = new float[n];
            Array.Fill(room, o.MaxEarthwork);
            for (int i = 0; i < n; i++)
            {
                raw[i] = terrain.HeightAt(points[i]);
                wet[i] = Water(points[i]);
                inMarket[i] = plaza.Area.Contains(points[i]);
                if (inMarket[i])
                    fixedAt[i] = market.HeightAt(points[i]);
            }

            foreach (var bridge in bridges.Where(b => b.Edge == edge.Id))
            {
                for (int i = bridge.First; i <= bridge.Last; i++)
                    (inMarket[i], fixedAt[i], wet[i]) = (true, bridge.Height, true);
                // The approach comes down from the deck at the grade limit; let it stand that far off the ground.
                foreach (var (end, step) in new[] { (bridge.First, -1), (bridge.Last, 1) })
                {
                    float lift = MathF.Abs(bridge.Height - raw[end]), run = 0;
                    for (int i = end + step; i >= 0 && i < n; i += step)
                    {
                        run += points[i].DistanceTo(points[i - step]);
                        float extra = lift - o.Grade(edge.Kind) * run * 0.8f;
                        if (extra <= 0)
                            break;
                        room[i] = MathF.Max(room[i], o.MaxEarthwork + extra);
                    }
                }
            }

            // The ends take the junctions' heights, which inside the market are the market's.
            fixedAt[0] ??= from;
            fixedAt[n - 1] ??= to;
            Bridge(points, raw, wet);

            var surface = Smooth(points, raw, o.Smoothing);
            for (int start = 0; start < n - 1;)
            {
                int end = start + 1;
                if (inMarket[start] && inMarket[end])
                {
                    // The road inside the market, or on a deck, is one piece at the given heights.
                    while (end < n - 1 && inMarket[end + 1])
                        end++;
                    for (int i = start; i <= end; i++)
                        surface[i] = fixedAt[i]!.Value;
                    yield return new GradeCorridor(edge.Id, edge.Kind, points[start..(end + 1)], surface[start..(end + 1)], edge.Width,
                        false, MathF.Max(o.Grade(edge.Kind), o.MarketGrade * 0.01f));
                    start = end;
                    continue;
                }

                while (fixedAt[end] == null)
                    end++;
                bool steep = Hold(points, raw, wet, surface, start, end, fixedAt[start]!.Value, fixedAt[end]!.Value,
                    o.Grade(edge.Kind), room);
                yield return new GradeCorridor(edge.Id, edge.Kind, points[start..(end + 1)], surface[start..(end + 1)], edge.Width,
                    steep, o.Grade(edge.Kind));
                start = end;
            }
        }
    }

    /// <summary>
    /// Fits the stretch of <paramref name="surface"/> between two fixed points: the smoothed terrain shifted to meet
    /// both ends and kept within the earthwork limit, then held to the grade limit from both ends. Returns true, and
    /// leaves the stretch at the shifted terrain, when the ends are too far apart in height for the grade, or when
    /// holding the grade takes the surface too far from the terrain.
    /// </summary>
    private static bool Hold(Vector2[] points, float[] raw, bool[] wet, float[] surface, int start, int end, float from, float to,
        float grade, float[] room)
    {
        int n = end - start + 1;
        var along = new float[n];
        for (int i = 1; i < n; i++)
            along[i] = along[i - 1] + points[start + i].DistanceTo(points[start + i - 1]);
        float length = along[n - 1];

        // Shift the smoothed terrain so it meets both ends, the correction fading linearly from each.
        var eased = new float[n];
        float d0 = from - surface[start], d1 = to - surface[end];
        for (int i = 0; i < n; i++)
        {
            float t = length > 0 ? along[i] / length : 0;
            float value = surface[start + i] + Mathf.Lerp(d0, d1, t);
            eased[i] = wet[start + i] ? value : Math.Clamp(value, raw[start + i] - room[start + i], raw[start + i] + room[start + i]);
        }

        eased[0] = from;
        eased[n - 1] = to;
        bool steep = MathF.Abs(to - from) > grade * length + 0.01f;
        var held = (float[])eased.Clone();
        if (!steep)
        {
            for (int i = 1; i < n; i++)
            {
                float step = grade * (along[i] - along[i - 1]);
                // Stay inside what both ends can still reach at the limit, then within a step of the point before.
                float low = MathF.Max(from - grade * along[i], to - grade * (length - along[i]));
                float high = MathF.Min(from + grade * along[i], to + grade * (length - along[i]));
                held[i] = Within(Within(held[i], low, high), held[i - 1] - step, held[i - 1] + step);
            }

            held[n - 1] = to;
            for (int i = n - 2; i >= 0; i--)
            {
                float step = grade * (along[i + 1] - along[i]);
                held[i] = Within(held[i], held[i + 1] - step, held[i + 1] + step);
            }

            // The two passes can leave the first step too steep, or the surface too far from the terrain.
            steep = MathF.Abs(held[0] - from) > 0.01f;
            held[0] = from;
            for (int i = 1; i < n - 1 && !steep; i++)
                steep = !wet[start + i] && MathF.Abs(held[i] - raw[start + i]) > room[start + i] + Slack;
        }

        Array.Copy(steep ? eased : held, 0, surface, start, n);
        return steep;
    }

    /// <summary>A clamp that tolerates bounds a rounding error apart the wrong way round.</summary>
    private static float Within(float value, float low, float high) => MathF.Max(low, MathF.Min(value, high));

    /// <summary>
    /// Over water the terrain is the riverbed; a road crosses on a deck. Replaces the heights of each wet run with the
    /// straight line between the dry points at its ends.
    /// </summary>
    private static void Bridge(Vector2[] points, float[] raw, bool[] wet)
    {
        for (int i = 0; i < raw.Length;)
        {
            if (!wet[i])
            {
                i++;
                continue;
            }

            int end = i;
            while (end < raw.Length && wet[end])
                end++;
            float before = i > 0 ? raw[i - 1] : end < raw.Length ? raw[end] : raw[i];
            float after = end < raw.Length ? raw[end] : before;
            for (int j = i; j < end; j++)
                raw[j] = Mathf.Lerp(before, after, (j - i + 1) / (float)(end - i + 1));
            i = end;
        }
    }

    /// <summary>The mean of the heights within half of <paramref name="window"/> meters along the line, at each point.</summary>
    private static float[] Smooth(Vector2[] points, float[] raw, float window)
    {
        int reach = (int)MathF.Round(window / 2 / Station);
        if (reach <= 0)
            return (float[])raw.Clone();
        var smooth = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            int a = Math.Max(0, i - reach), b = Math.Min(raw.Length - 1, i + reach);
            float sum = 0;
            for (int j = a; j <= b; j++)
                sum += raw[j];
            smooth[i] = sum / (b - a + 1);
        }

        return smooth;
    }

    /// <summary>The plane nearest the ground over <paramref name="area"/>, tilted no more than <paramref name="maxGrade"/>.</summary>
    private static GradePlane FitPlane(Region area, Func<Vector2, float> ground, float maxGrade)
    {
        var samples = new List<(Vector2, float)>();
        var bounds = area.Bounds;
        for (float z = bounds.Position.Y; z <= bounds.End.Y; z += 2)
        {
            for (float x = bounds.Position.X; x <= bounds.End.X; x += 2)
            {
                var p = new Vector2(x, z);
                if (area.Contains(p))
                    samples.Add((p, ground(p)));
            }
        }

        if (samples.Count == 0)
            samples.Add((bounds.GetCenter(), ground(bounds.GetCenter())));
        return FitPlane(samples, maxGrade);
    }

    /// <summary>
    /// The least-squares plane through the samples; a tilt past <paramref name="maxGrade"/> is cut back to it, keeping
    /// its direction, and the plane keeps the samples' mean height at their middle.
    /// </summary>
    private static GradePlane FitPlane(IReadOnlyList<(Vector2 P, float H)> samples, float maxGrade)
    {
        var middle = samples.Aggregate(Vector2.Zero, (sum, s) => sum + s.P) / samples.Count;
        float mean = samples.Average(s => s.H);
        double xx = 0, xz = 0, zz = 0, xh = 0, zh = 0;
        foreach (var (p, h) in samples)
        {
            double x = p.X - middle.X, z = p.Y - middle.Y, dh = h - mean;
            xx += x * x;
            xz += x * z;
            zz += z * z;
            xh += x * dh;
            zh += z * dh;
        }

        var slope = Vector2.Zero;
        double determinant = xx * zz - xz * xz;
        if (determinant > 1e-3 * Math.Max(1, xx * zz))
            slope = new Vector2((float)((xh * zz - zh * xz) / determinant), (float)((zh * xx - xh * xz) / determinant));
        else if (xx + zz > 1e-6)
        {
            // Samples along one line (a road through a square): tilt along that line only.
            var along = new Vector2((float)xx, (float)xz).Length() > new Vector2((float)xz, (float)zz).Length()
                ? new Vector2((float)xx, (float)xz).Normalized() : new Vector2((float)xz, (float)zz).Normalized();
            double run = 0, rise = 0;
            foreach (var (p, h) in samples)
            {
                double s = (p - middle).Dot(along);
                run += s * s;
                rise += s * (h - mean);
            }

            slope = along * (float)(run > 1e-6 ? rise / run : 0);
        }

        if (slope.Length() > maxGrade)
            slope = slope.Normalized() * maxGrade;
        return new GradePlane(middle, mean, slope);
    }

    private static Rect2 Box(Vector2[] points)
    {
        var box = new Rect2(points[0], Vector2.Zero);
        foreach (var p in points)
            box = box.Expand(p);
        return box;
    }
}
