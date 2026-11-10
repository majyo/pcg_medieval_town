using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class FrontageParcelsOptions
{
    [Param("最小面宽 m", 4, 20, 0.5,
        Hint = "地块临街面宽的下限")]
    public float FrontageMin { get; set; } = 7;
    [Param("最大面宽 m", 5, 30, 0.5,
        Hint = "地块临街面宽的上限；每块在上下限之间随机")]
    public float FrontageMax { get; set; } = 12;
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
}

/// <summary>
/// Cuts plots of random frontage along both sides of every road with blocks beside it (main streets, then outer
/// roads, then lanes; longer before shorter), to a starting depth. Then all plots grow back together in thin strips, so plots from opposite
/// streets meet near the middle of the block, up to a maximum depth. Gaps left in a block (at bends, corners and
/// street ends) join the plot they share the longest border with, so the block is covered.
/// </summary>
public sealed class FrontageParcels : CityNode<FrontageParcelsOptions>
{
    private const float Bucket = 40;
    private const float Step = 2;
    private const float MaxGap = 1500;

    public override string Group => "街区与地块";
    public override string Title => "临街地块";
    public override string Description =>
        "沿每条街和城外道路两侧按随机面宽切出临街地块（先主街，再城外道路，后街巷），再让所有地块一齐向后延伸到街区中间，街角和弯处剩下的空隙并给相邻地块。";
    public override IReadOnlyList<IKey> Reads => [Keys.Blocks, Keys.Roads];
    public override IReadOnlyList<IKey> Writes => [Keys.Parcels];

    protected override void Run(NodeContext c, FrontageParcelsOptions o)
    {
        if (o.FrontageMax < o.FrontageMin)
            throw new ArgumentException("最大面宽不能小于最小面宽");
        if (o.MaxDepth < o.Depth)
            throw new ArgumentException("最大进深不能小于起始进深");

        var blocks = c.Get(Keys.Blocks).Blocks;
        var roads = c.Get(Keys.Roads);
        var parcels = new List<Parcel>();
        var shapes = new List<Region>();
        var blockOf = new List<int>();
        var index = new Dictionary<(int, int), List<int>>();
        var rng = new Random(o.Seed);
        var ordered = roads.Edges.OrderBy(e => e.Kind).ThenByDescending(e => e.Length).ThenBy(e => e.Id);
        foreach (var edge in ordered)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            foreach (int side in new[] { 1, -1 })
            {
                for (int s = 1; s < edge.Points.Length; s++)
                    Frontage(edge, edge.Points[s - 1], edge.Points[s], side);
            }
        }

        GrowBack();
        FillGaps();
        c.Set(Keys.Parcels, new ParcelSet(Settle()));

        void Frontage(RoadEdge edge, Vector2 p0, Vector2 p1, int side)
        {
            float length = p0.DistanceTo(p1);
            var along = (p1 - p0) / length;
            var inward = along.Orthogonal() * side;
            float front = edge.Width / 2;
            for (float t = 0; length - t >= o.FrontageMin;)
            {
                float width = o.FrontageMin + rng.NextSingle() * (o.FrontageMax - o.FrontageMin);
                if (length - t - width < o.FrontageMin * 0.6f && length - t <= o.FrontageMax * 1.4f)
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
            var probe = (a + b) / 2 + inward * 2;
            int block = blocks.Count;
            for (int i = 0; i < blocks.Count && block == blocks.Count; i++)
            {
                if (blocks[i].Contains(probe))
                    block = i;
            }

            if (block == blocks.Count)
                return;

            var quad = Region.Polygon(new[] { a, b, b + inward * (o.Depth + 0.5f), a + inward * (o.Depth + 0.5f) });
            var piece = Free(quad.Intersect(blocks[block]), quad.Bounds);
            var part = piece.Parts().FirstOrDefault(p => p.Contains(probe));
            // A part wrapped around an earlier plot would keep only its outer ring and overlap that plot.
            if (part == null || part.Area < o.MinArea || part.Rings.Count != 1)
                return;

            int id = parcels.Count;
            parcels.Add(new Parcel(id, part.Rings[0], a + inward * 0.5f, b + inward * 0.5f, inward, edge.Kind, (float)part.Area));
            shapes.Add(part);
            blockOf.Add(block);
            Register(id);
        }

        // Every plot takes the next strip behind it in turn, round after round, so plots facing each other across a
        // block meet within a strip of its middle. A plot stops once nothing behind it is free.
        void GrowBack()
        {
            var growing = Enumerable.Range(0, parcels.Count).ToList();
            for (float near = o.Depth; near < o.MaxDepth && growing.Count > 0; near += Step)
            {
                c.Cancellation.ThrowIfCancellationRequested();
                float far = Math.Min(near + Step, o.MaxDepth) + 0.5f;
                var still = new List<int>();
                foreach (int i in growing)
                {
                    var p = parcels[i];
                    var strip = Region.Polygon(new[]
                    {
                        p.FrontA + p.Inward * (near - 1), p.FrontB + p.Inward * (near - 1),
                        p.FrontB + p.Inward * far, p.FrontA + p.Inward * far,
                    });
                    var free = Free(strip.Intersect(blocks[blockOf[i]]), strip.Bounds);
                    if (Join(i, free))
                        still.Add(i);
                }

                growing = still;
            }
        }

        // What is left of each block after the plots joins the plot it borders most; large leftovers stay open.
        void FillGaps()
        {
            for (int block = 0; block < blocks.Count; block++)
            {
                var members = Enumerable.Range(0, parcels.Count).Where(i => blockOf[i] == block).ToList();
                if (members.Count == 0)
                    continue;

                var taken = Region.Polygons(members.Select(i => shapes[i].Rings[0]));
                foreach (var gap in blocks[block].Subtract(taken).Parts())
                {
                    if (gap.Area < 1 || gap.Area > MaxGap)
                        continue;

                    var around = gap.Offset(0.3f);
                    var (best, border) = members.Select(i => (i, around.Intersect(shapes[i]).Area)).MaxBy(x => x.Area);
                    if (border > 0.05)
                        Join(best, gap);
                }
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
                var region = Region.Polygon(shapes[i].Rings[0]);
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

        // Adds the parts of `extra` touching plot i to it, if the plot stays one ring; true when it grew.
        bool Join(int i, Region extra)
        {
            var touch = shapes[i].Offset(0.1f);
            var added = extra.Parts().Where(part => part.Intersect(touch).Area > 0.01).ToList();
            if (added.Count == 0)
                return false;

            var merged = added.Aggregate(shapes[i], (shape, part) => shape.Union(part));
            if (merged.Rings.Count != 1 || merged.Area < shapes[i].Area + 0.5)
                return false;

            shapes[i] = merged;
            Register(i);
            return true;
        }

        Region Free(Region piece, Rect2 bounds)
        {
            foreach (int other in Nearby(bounds))
            {
                if (shapes[other].Bounds.Intersects(bounds))
                    piece = piece.Subtract(shapes[other]);
            }

            return piece;
        }

        void Register(int id)
        {
            foreach (var cell in Buckets(shapes[id].Bounds))
            {
                if (!index.TryGetValue(cell, out var list))
                    index[cell] = list = new List<int>();
                if (!list.Contains(id))
                    list.Add(id);
            }
        }

        IEnumerable<int> Nearby(Rect2 bounds) =>
            Buckets(bounds).SelectMany(k => index.TryGetValue(k, out var list) ? list : Enumerable.Empty<int>()).Distinct();
    }

    private static IEnumerable<(int, int)> Buckets(Rect2 r)
    {
        for (int z = (int)MathF.Floor(r.Position.Y / Bucket); z <= (int)MathF.Floor(r.End.Y / Bucket); z++)
            for (int x = (int)MathF.Floor(r.Position.X / Bucket); x <= (int)MathF.Floor(r.End.X / Bucket); x++)
                yield return (x, z);
    }
}
