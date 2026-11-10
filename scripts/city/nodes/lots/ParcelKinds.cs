using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class ParcelKindsOptions
{
    [Param("大院范围 m", 20, 120, 5,
        Hint = "城门内外多远以内临主街或城外道路的地块可以并成客栈大院")]
    public float CourtReach { get; set; } = 60;
    [Param("大院最小面宽 m", 10, 30, 1,
        Hint = "并成的大院至少这么宽（够开车马门、留院子）")]
    public float CourtMin { get; set; } = 16;
    [Param("大院最大面宽 m", 14, 40, 1,
        Hint = "并成的大院至多这么宽；在范围内的组合里取最接近两者中间的一组")]
    public float CourtMax { get; set; } = 26;
    [Param("长条进深面宽比", 1, 4, 0.1,
        Hint = "临主街、城外道路、次街的地块，进深至少是面宽的这么多倍时算长条")]
    public float StripRatio { get; set; } = 2;
    [Param("街角最短侧边 m", 2, 12, 0.5,
        Hint = "地块贴着另一条路的一段至少这么长才算街角地块")]
    public float MinSide { get; set; } = 4;
}

/// <summary>
/// Sorts the plots once they are cut: the trades' plots; a court for an inn inside and outside each gate and by the
/// market, merged from two or three neighbors on the main street or outer road; corner plots, whose side runs along
/// another street; long strips behind a main street, outer road or secondary street; and the rest. Only the courts
/// change any plot's shape; the others gain a kind (and a corner its side).
/// </summary>
public sealed class ParcelKinds : CityNode<ParcelKindsOptions>
{
    /// <summary>A court by the market has its front this near the market's edge.</summary>
    public const float MarketReach = 10;
    /// <summary>The farthest a court may lie from its gate, and its frontage range (the ends of the sliders).</summary>
    public const float MaxCourtReach = 120, MinCourtFrontage = 10, MaxCourtFrontage = 40;
    /// <summary>A plot's side runs along a street when its points lie this near the paving, at this angle to the front.</summary>
    public const float SideReach = 1.2f, SideAngle = 35;
    /// <summary>Points this near the front line belong to the front, not to a side.</summary>
    private const float FrontBand = 2;

    public override string Group => NodeGroups.Lots;
    public override string Title => "地块类型";
    public override string Description =>
        "切好的地块分类：作坊地块；城门内外和市场边各把两三块临主街的地块并成一处客栈大院；一边贴着另一条路的街角地块；临主街、次街的长条地块；其余是一般地块。";
    public override IReadOnlyList<IKey> Reads => [Keys.Roads, Keys.Gates, Keys.Plaza, Keys.Wall, Keys.LandUse];
    public override IReadOnlyList<IKey> Edits => [Keys.Parcels];

    protected override void Run(NodeContext c, ParcelKindsOptions o)
    {
        if (o.CourtMax < o.CourtMin)
            throw new ArgumentException("大院最大面宽不能小于最小面宽");
        var roads = c.Get(Keys.Roads);
        var plaza = c.Get(Keys.Plaza);
        var interior = c.Get(Keys.Wall).Interior;
        var land = c.Get(Keys.LandUse);
        var plots = c.Get(Keys.Parcels).Parcels
            .Select(p => LandUses.Trades.Contains(p.Use) ? p with { Kind = ParcelKind.Trade } : p).ToList();

        // Courts: inside and outside each gate, and by the market.
        var sites = c.Get(Keys.Gates).Gates
            .SelectMany(g => new[] { true, false }.Select(inside => (Func<Parcel, bool>)(p =>
                p.Probe.DistanceTo(g.Position) <= o.CourtReach && interior.Contains(p.Probe) == inside)))
            .Append(p => plaza.Area.Offset(MarketReach).Contains(p.Probe));
        int missing = 0;
        foreach (var site in sites)
        {
            var candidates = Enumerable.Range(0, plots.Count)
                .Where(i => plots[i] != null && plots[i].Kind == ParcelKind.General
                    && plots[i].Street is RoadKind.Main or RoadKind.Outer && site(plots[i]))
                .ToList();
            if (Court(plots, candidates, o, land, interior) is not { } court)
            {
                if (candidates.Count > 0)
                    missing++;
                continue;
            }

            plots[court.Members[0]] = court.Plot;
            foreach (int i in court.Members.Skip(1))
                plots[i] = null!;
        }

        plots = plots.Where(p => p != null).ToList();

        // Corners, then strips.
        var edges = roads.Edges.Where(e => e.Kind != RoadKind.Track)
            .Select(e => (Edge: e, Bounds: Bounds(e.Points).Grow(e.Width / 2 + SideReach))).ToList();
        for (int i = 0; i < plots.Count; i++)
        {
            var p = plots[i];
            if (p.Kind != ParcelKind.General)
                continue;
            if (SideOf(p, edges, o.MinSide) is { } side)
                plots[i] = p with { Kind = ParcelKind.Corner, Side = side };
            else if (p.Street is RoadKind.Main or RoadKind.Outer or RoadKind.Street && p.Depth >= o.StripRatio * p.Frontage)
                plots[i] = p with { Kind = ParcelKind.Strip };
        }

        if (missing > 0)
            c.Notice($"{missing} 处城门或市场边找不到能并成大院的相邻地块");
        c.Set(Keys.Parcels, new ParcelSet(plots.Select((p, i) => p with { Id = i }).ToList()));
    }

    /// <summary>
    /// The best court among the candidates: two or three neighbors in a row on one street, together within the
    /// frontage range and still one ring; the one nearest the middle of the range.
    /// </summary>
    private static (int[] Members, Parcel Plot)? Court(List<Parcel> plots, List<int> candidates, ParcelKindsOptions o,
        CategoryField land, Region interior)
    {
        var regions = candidates.ToDictionary(i => i, i => Region.Polygon(plots[i].Ring));
        bool Adjacent(int a, int b)
        {
            var p = plots[a];
            var q = plots[b];
            if (p.Inward.Dot(q.Inward) < 0.95f || MathF.Abs((q.FrontA - p.FrontA).Dot(p.Inward)) > 1)
                return false;
            double shared = regions[a].Offset(0.1f).Intersect(regions[b]).Area / 0.1;
            return shared >= 0.7 * Math.Min(p.Depth, q.Depth);
        }

        var groups = new List<int[]>();
        foreach (int a in candidates)
        {
            foreach (int b in candidates.Where(b => b > a && Adjacent(a, b)))
            {
                groups.Add([a, b]);
                foreach (int x in candidates.Where(x => x != a && x != b && (Adjacent(b, x) || Adjacent(a, x))))
                    groups.Add([a, b, x]);
            }
        }

        float middle = (o.CourtMin + o.CourtMax) / 2;
        (int[] Members, Parcel Plot)? best = null;
        float bestScore = float.PositiveInfinity;
        foreach (var group in groups)
        {
            var first = plots[group[0]];
            var along = (first.FrontB - first.FrontA).Normalized();
            var ends = group.SelectMany(i => new[] { plots[i].FrontA, plots[i].FrontB }).ToList();
            var a = ends.MinBy(v => v.Dot(along));
            var b = ends.MaxBy(v => v.Dot(along));
            float frontage = a.DistanceTo(b);
            float score = MathF.Abs(frontage - middle);
            if (frontage < o.CourtMin || frontage > o.CourtMax || score >= bestScore)
                continue;
            var union = group.Aggregate(Region.Empty, (all, i) => all.Union(regions[i]));
            if (union.Parts().Count != 1 || union.Rings.Count != 1)
                continue;

            var plot = first with
            {
                Ring = union.Rings[0],
                FrontA = a,
                FrontB = b,
                Area = (float)union.Area,
                Street = group.Min(i => plots[i].Street),
                Kind = ParcelKind.Court,
            };
            plot = plot with { Use = land.PlotUse(plot.Probe, interior.Contains(plot.Probe)) };
            best = (group.OrderBy(i => i).ToArray(), plot);
            bestScore = score;
        }

        return best;
    }

    /// <summary>The longest run along the plot's edge that lies along another street, if long enough: its two ends.</summary>
    private static Vector2[]? SideOf(Parcel p, List<(RoadEdge Edge, Rect2 Bounds)> edges, float minSide)
    {
        var front = (p.FrontB - p.FrontA).Normalized();
        var box = Bounds(p.Ring).Grow(1);
        var near = edges.Where(e => e.Bounds.Intersects(box)).Select(e => e.Edge).ToList();
        if (near.Count == 0)
            return null;

        var samples = new List<(Vector2 Point, bool Side)>();
        for (int i = 0; i < p.Ring.Length; i++)
        {
            var a = p.Ring[i];
            var b = p.Ring[(i + 1) % p.Ring.Length];
            float length = a.DistanceTo(b);
            for (float t = 0; t < length; t += 1)
            {
                var point = a + (b - a) * (t / length);
                samples.Add((point, (point - p.FrontA).Dot(p.Inward) > FrontBand && near.Any(e => AlongSide(e, point, front))));
            }
        }

        // Longest run of side points around the ring (it may wrap past the start).
        int n = samples.Count, bestStart = -1, bestLength = 0;
        bool all = samples.All(x => x.Side);
        for (int start = 0; start < n; start++)
        {
            // Count each run once, from its first point.
            if (!samples[start].Side || !all && samples[(start + n - 1) % n].Side)
                continue;
            int length = 0;
            while (length < n && samples[(start + length) % n].Side)
                length++;
            if (length > bestLength)
                (bestStart, bestLength) = (start, length);
        }

        if (bestStart < 0)
            return null;
        var from = samples[bestStart].Point;
        var to = samples[(bestStart + bestLength - 1) % n].Point;
        return from.DistanceTo(to) >= minSide ? [from, to] : null;
    }

    /// <summary>Whether the point lies by this road's paving where the road runs at a clear angle to the front.</summary>
    public static bool AlongSide(RoadEdge edge, Vector2 point, Vector2 front, float reach = SideReach)
    {
        int best = -1;
        float distance = float.PositiveInfinity;
        for (int i = 1; i < edge.Points.Length; i++)
        {
            float d = Geometry.DistanceToSegment(point, edge.Points[i - 1], edge.Points[i]);
            if (d < distance)
                (distance, best) = (d, i);
        }

        if (best < 0 || distance > edge.Width / 2 + reach)
            return false;
        var direction = (edge.Points[best] - edge.Points[best - 1]).Normalized();
        return MathF.Acos(MathF.Min(1, MathF.Abs(direction.Dot(front)))) > Mathf.DegToRad(SideAngle);
    }

    private static Rect2 Bounds(IReadOnlyList<Vector2> points)
    {
        var rect = new Rect2(points[0], Vector2.Zero);
        foreach (var v in points)
            rect = rect.Expand(v);
        return rect;
    }
}
