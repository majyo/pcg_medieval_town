using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class FrontageParcelsOptions
{
    [Param("市场核心 最小面宽 m", 3, 20, 0.5,
        Hint = "市场核心的地块临街面宽的下限。市场边的地最值钱，地块窄而深")]
    public float CoreFrontageMin { get; set; } = 5;
    [Param("市场核心 最大面宽 m", 4, 30, 0.5,
        Hint = "市场核心的地块临街面宽的上限；每块在上下限之间随机")]
    public float CoreFrontageMax { get; set; } = 8;
    [Param("居住 最小面宽 m", 3, 20, 0.5,
        Hint = "城内居住区的地块临街面宽的下限")]
    public float HousingFrontageMin { get; set; } = 7;
    [Param("居住 最大面宽 m", 4, 30, 0.5,
        Hint = "城内居住区的地块临街面宽的上限；每块在上下限之间随机")]
    public float HousingFrontageMax { get; set; } = 12;
    [Param("城外街区 最小面宽 m", 3, 30, 0.5,
        Hint = "城外街区的地块临街面宽的下限。城外地便宜，地块更宽")]
    public float SuburbFrontageMin { get; set; } = 10;
    [Param("城外街区 最大面宽 m", 4, 40, 0.5,
        Hint = "城外街区的地块临街面宽的上限；每块在上下限之间随机")]
    public float SuburbFrontageMax { get; set; } = 16;
    [Param("作坊 最小面宽 m", 6, 40, 0.5,
        Hint = "作坊（湿作坊、火作坊、水磨）的地块临街面宽的下限。作坊要院子堆料、晾晒，地块最宽")]
    public float TradeFrontageMin { get; set; } = 18;
    [Param("作坊 最大面宽 m", 8, 60, 0.5,
        Hint = "作坊的地块临街面宽的上限；每块在上下限之间随机")]
    public float TradeFrontageMax { get; set; } = 30;
    [Param("起始进深 m", 10, 60, 1,
        Hint = "先沿街切出这么深的地块（先切的街占住街角），之后各地块一齐向街区深处延伸")]
    public float Depth { get; set; } = 16;
    [Param("最大进深 m", 10, 120, 1,
        Hint = "地块向后延伸的上限；两边街道的地块在街区中间相遇时停下。调小后街区深处留出空地")]
    public float MaxDepth { get; set; } = 50;
    [Param("最小面积 m²", 30, 400, 5,
        Hint = "裁剪后比它小的地块丢弃")]
    public float MinArea { get; set; } = 80;
    [Param("种子", 0, 99999,
        Hint = "只影响各地块的面宽的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;

    /// <summary>The frontage range of plots cut for <paramref name="use"/>.</summary>
    public (float Min, float Max) Frontage(LandUse use) => use switch
    {
        LandUse.Core => (CoreFrontageMin, CoreFrontageMax),
        LandUse.Suburb => (SuburbFrontageMin, SuburbFrontageMax),
        _ when LandUses.Trades.Contains(use) => (TradeFrontageMin, TradeFrontageMax),
        _ => (HousingFrontageMin, HousingFrontageMax),
    };
}

/// <summary>
/// Cuts plots of random frontage along both sides of every road with blocks beside it (main streets, then outer
/// roads, then lanes, then tracks; longer before shorter), to a starting depth. The frontage range follows the land
/// use where each plot starts (narrow in the market core, wide in the suburbs, widest for the trades), and each plot
/// records its use. Then all plots
/// grow back together in thin strips, so plots from opposite streets meet near the middle of the block, up to a
/// maximum depth. Gaps left in a block (at bends, corners and
/// street ends) join the plot they share the longest border with, so the block is covered. A trade's block that no
/// plot reached becomes one plot.
/// </summary>
public sealed class FrontageParcels : CityNode<FrontageParcelsOptions>
{
    private const float Step = 2;
    private const float MaxGap = 1500;

    /// <summary>The shortest terrace front given a plot of its own when it is narrower than its use's plots.</summary>
    private const float MinTerraceFront = 6;

    public override string Group => NodeGroups.Lots;
    public override string Title => "临街地块";
    public override string Description =>
        "沿每条街和城外道路两侧按随机面宽切出临街地块（先主街，再城外道路，然后街巷，最后便道），面宽按用途：市场核心窄、城外街区宽、作坊最宽；再让所有地块一齐向后延伸到街区中间，街角和弯处剩下的空隙并给相邻地块。";
    public override IReadOnlyList<IKey> Reads => [Keys.Blocks, Keys.Roads, Keys.LandUse, Keys.Wall, Keys.Terraces];
    public override IReadOnlyList<IKey> Writes => [Keys.Parcels];

    protected override void Run(NodeContext c, FrontageParcelsOptions o)
    {
        foreach (var use in LandUses.Plots)
        {
            var (min, max) = o.Frontage(use);
            if (max < min)
                throw new ArgumentException($"{use.Label()}的最大面宽不能小于最小面宽");
        }

        if (o.MaxDepth < o.Depth)
            throw new ArgumentException("最大进深不能小于起始进深");

        var blocks = c.Get(Keys.Blocks).Blocks;
        var roads = c.Get(Keys.Roads);
        var land = c.Get(Keys.LandUse);
        var interior = c.Get(Keys.Wall).Interior;
        LandUse UseAt(Vector2 p) => land.PlotUse(p, interior.Contains(p));
        bool InBlock(Vector2 p) => blocks.Any(b => b.Bounds.HasPoint(p) && b.Contains(p));
        var sweep = new FrontSweep(blocks, c.Cancellation);
        var parcels = new List<Parcel>();
        // Where there are terraces, a plot starts and ends on one terrace's front and grows only within its reach.
        var terraces = c.Get(Keys.Terraces).Terraces.Where(t => t.Kind == TerraceKind.Block).ToList();
        var breaks = terraces.SelectMany(t => t.Fronts).GroupBy(f => (f.Edge, f.Side))
            .ToDictionary(g => g.Key, g => g.SelectMany(f => new[] { f.From, f.To }).Distinct().OrderBy(x => x).ToArray());
        var within = new List<Region?>();
        // A plot opening onto no terrace keeps to the land no terrace reaches.
        var unreached = terraces.Count == 0 ? null
            : blocks.Aggregate(Region.Empty, (all, b) => all.Union(b))
                .Subtract(terraces.Aggregate(Region.Empty, (all, t) => all.Union(t.Reach)));
        Region? ReachAt(Vector2 p) => terraces.FirstOrDefault(t => t.Reach.Bounds.HasPoint(p) && t.Reach.Contains(p))?.Reach ?? unreached;
        var rng = new Random(o.Seed);
        var ordered = roads.Edges.OrderBy(e => e.Kind).ThenByDescending(e => e.Length).ThenBy(e => e.Id);
        foreach (var edge in ordered)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            foreach (int side in new[] { 1, -1 })
            {
                float at = 0;
                var cuts = breaks.GetValueOrDefault((edge.Id, side), []);
                for (int s = 1; s < edge.Points.Length; s++)
                {
                    var (p0, p1) = (edge.Points[s - 1], edge.Points[s]);
                    float length = p0.DistanceTo(p1);
                    var along = (p1 - p0) / MathF.Max(1e-4f, length);
                    float from = at;
                    foreach (float cut in cuts.Where(x => x > at + 0.01f && x < at + length - 0.01f).Append(at + length))
                    {
                        Frontage(edge, p0 + along * (from - at), p0 + along * (cut - at), side, cuts.Length > 0);
                        from = cut;
                    }

                    at += length;
                }
            }
        }

        sweep.GrowBack(o.Depth, o.MaxDepth, Step);
        sweep.FillGaps(MaxGap, (i, gap) => within[i] is not { } reach || reach.Intersect(gap).Area > 0.5);
        var settled = Settle();
        Leftovers(settled);
        c.Set(Keys.Parcels, new ParcelSet(settled));

        void Frontage(RoadEdge edge, Vector2 p0, Vector2 p1, int side, bool terraced)
        {
            float length = p0.DistanceTo(p1);
            var along = (p1 - p0) / length;
            var inward = along.Orthogonal() * side;
            float front = edge.Width / 2;
            for (float t = 0; ;)
            {
                // Road beside no block (fields, gardens, a landmark) gets no plots: the next one starts where a block
                // does, so small blocks such as a mill's are not straddled by a plot that starts out in the field.
                while (length - t >= 1 && !InBlock(p0 + along * (t + 1) + inward * (front + 1.5f)))
                    t += 1;
                var (min, max) = o.Frontage(UseAt(p0 + along * Math.Min(t + 2, length) + inward * (front + 1.5f)));
                // A terrace's front shorter than a plot of its use still gets one plot, unless it is a mere sliver.
                if (length - t < min && !(terraced && t == 0 && length >= MinTerraceFront))
                    break;
                float width = min + rng.NextSingle() * (max - min);
                if (length - t - width < min * 0.6f && length - t <= max * 1.4f)
                    width = length - t;
                width = Math.Min(width, length - t);
                var a = p0 + along * t + inward * (front - 0.5f);
                var b = a + along * width;
                Claim(edge, a, b, inward);
                t += width;
            }
        }

        void Claim(RoadEdge edge, Vector2 a, Vector2 b, Vector2 inward)
        {
            var reach = ReachAt((a + b) / 2 + inward * 2);
            int id = sweep.Claim(a, b, inward, o.Depth + 0.5f, o.MinArea, reach);
            if (id < 0)
                return;
            within.Add(reach);
            var (frontA, frontB, _) = sweep.Fronts[id];
            var parcel = new Parcel(id, sweep.Shapes[id].Rings[0], frontA, frontB, inward, edge.Kind, default,
                (float)sweep.Shapes[id].Area);
            parcels.Add(parcel with { Use = UseAt(parcel.Probe) });
        }

        // A trade's block left without a plot (a mill's strip of bank, whose track bends too often for a full
        // frontage) becomes one plot. It fronts the side of its smallest bounding rectangle nearest a road.
        void Leftovers(List<Parcel> result)
        {
            var probes = result.Select(p => p.Probe).ToList();
            for (int block = 0; block < blocks.Count; block++)
            {
                var shape = blocks[block];
                if (shape.Area < o.MinArea || shape.Rings.Count != 1
                    || probes.Any(p => shape.Bounds.HasPoint(p) && shape.Contains(p)))
                    continue;
                // A strip along a bend can have its centroid in the river: judge it by the cells it covers.
                if (!land.Grid.CellsIn(shape).Any(k => LandUses.Trades.Contains(land.Use(k))))
                    continue;
                // On terraces it keeps to the one it overlaps most.
                if (terraces.Where(t => t.Reach.Bounds.Intersects(shape.Bounds)).MaxBy(t => t.Reach.Intersect(shape).Area) is { } terrace
                    && terrace.Reach.Intersect(shape).Parts().MaxBy(r => r.Area) is { } part)
                {
                    if (part.Area < o.MinArea || part.Rings.Count != 1)
                        continue;
                    shape = part;
                }

                var ring = shape.Rings[0];
                var centroid = ring.Aggregate(Vector2.Zero, (sum, p) => sum + p) / ring.Length;

                var (edge, road) = roads.Edges.SelectMany(e => Geometry.Samples(e.Points, 2).Select(p => (e, p)))
                    .MinBy(x => x.p.DistanceSquaredTo(centroid));
                var (a, b, inward) = Facing(ring, road);
                var parcel = new Parcel(result.Count, ring, a, b, inward, edge.Kind, default, (float)shape.Area);
                result.Add(parcel with { Use = UseAt(parcel.Probe) });
            }
        }

        // Strips meeting a diagonal block edge can leave a ring that pinches back on itself; read as one polygon, the
        // pinched loop pokes a sliver into its neighbor. Trim each plot by the plots before it and keep its largest
        // part, so the rings handed on never overlap.
        List<Parcel> Settle()
        {
            var settled = new List<Region>();
            var result = new List<Parcel>();
            for (int i = 0; i < parcels.Count; i++)
            {
                var region = Region.Polygon(sweep.Shapes[i].Rings[0]);
                foreach (var other in settled)
                {
                    if (other.Bounds.Intersects(region.Bounds) && other.Intersect(region).Area > 0.01)
                        region = region.Subtract(other);
                }

                var part = region.Parts().MaxBy(r => r.Area);
                if (part == null || part.Area < o.MinArea)
                    continue;
                settled.Add(part);
                result.Add(parcels[i] with { Id = result.Count, Ring = part.Rings[0], Area = (float)part.Area });
            }

            return result;
        }
    }

    /// <summary>
    /// The side of the smallest rectangle around <paramref name="ring"/> whose middle is nearest <paramref name="road"/>:
    /// its ends, and the direction into the rectangle.
    /// </summary>
    private static (Vector2 A, Vector2 B, Vector2 Inward) Facing(Vector2[] ring, Vector2 road)
    {
        (Vector2 Along, float A0, float A1, float N0, float N1) best = default;
        float bestArea = float.MaxValue;
        for (int i = 0; i < ring.Length; i++)
        {
            var d = ring[(i + 1) % ring.Length] - ring[i];
            if (d.LengthSquared() < 1e-4f)
                continue;
            d = d.Normalized();
            var n = d.Orthogonal();
            float a0 = ring.Min(p => p.Dot(d)), a1 = ring.Max(p => p.Dot(d));
            float n0 = ring.Min(p => p.Dot(n)), n1 = ring.Max(p => p.Dot(n));
            float area = (a1 - a0) * (n1 - n0);
            if (area < bestArea)
            {
                bestArea = area;
                best = (d, a0, a1, n0, n1);
            }
        }

        var (along, from, to, near, far) = best;
        var normal = along.Orthogonal();
        Vector2 P(float a, float n) => along * a + normal * n;
        var sides = new[]
        {
            (A: P(from, near), B: P(to, near), Inward: normal),
            (A: P(to, far), B: P(from, far), Inward: -normal),
            (A: P(from, far), B: P(from, near), Inward: along),
            (A: P(to, near), B: P(to, far), Inward: -along),
        };
        return sides.MinBy(s => ((s.A + s.B) / 2).DistanceSquaredTo(road));
    }
}
