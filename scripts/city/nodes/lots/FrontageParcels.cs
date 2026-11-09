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
    [Param("地块进深 m", 10, 60, 1,
        Hint = "地块从街边向内的进深。调大后地块更长，街区深处的空地更少")]
    public float Depth { get; set; } = 26;
    [Param("最小面积 m²", 30, 400, 5,
        Hint = "裁剪后比它小的地块丢弃")]
    public float MinArea { get; set; } = 80;
    [Param("种子", 0, 99999,
        Hint = "只影响各地块的面宽的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;
}

/// <summary>
/// Cuts fixed-depth plots of random frontage along both sides of every town street, main streets first and
/// longer streets before shorter. A plot is clipped to the blocks and loses whatever earlier plots claimed.
/// </summary>
public sealed class FrontageParcels : CityNode<FrontageParcelsOptions>
{
    private const float Bucket = 40;

    public override string Group => "街区与地块";
    public override string Title => "临街地块";
    public override string Description =>
        "沿每条街两侧按随机面宽切出固定进深的临街地块：先主街后街巷，后切的让给先切的。";
    public override IReadOnlyList<IKey> Reads => [Keys.Blocks, Keys.Roads];
    public override IReadOnlyList<IKey> Writes => [Keys.Parcels];

    protected override void Run(NodeContext c, FrontageParcelsOptions o)
    {
        if (o.FrontageMax < o.FrontageMin)
            throw new ArgumentException("最大面宽不能小于最小面宽");

        var blocks = c.Get(Keys.Blocks).Blocks;
        var roads = c.Get(Keys.Roads);
        var parcels = new List<Parcel>();
        var claimed = new List<Region>();
        var index = new Dictionary<(int, int), List<int>>();
        var rng = new Random(o.Seed);
        var ordered = roads.Edges.Where(e => e.Kind != RoadKind.Outer)
            .OrderBy(e => e.Kind).ThenByDescending(e => e.Length).ThenBy(e => e.Id);
        foreach (var edge in ordered)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            foreach (int side in new[] { 1, -1 })
            {
                for (int s = 1; s < edge.Points.Length; s++)
                    Frontage(edge, edge.Points[s - 1], edge.Points[s], side);
            }
        }

        c.Set(Keys.Parcels, new ParcelSet(parcels));

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
            var quad = Region.Polygon(new[] { a, b, b + inward * (o.Depth + 0.5f), a + inward * (o.Depth + 0.5f) });
            var bounds = quad.Bounds;
            var piece = Region.Empty;
            foreach (var block in blocks)
            {
                if (block.Bounds.Intersects(bounds))
                    piece = piece.Union(quad.Intersect(block));
            }

            foreach (int other in Nearby(bounds))
            {
                if (claimed[other].Bounds.Intersects(bounds))
                    piece = piece.Subtract(claimed[other]);
            }

            var probe = (a + b) / 2 + inward * 2;
            var part = piece.Parts().FirstOrDefault(p => p.Contains(probe));
            if (part == null || part.Area < o.MinArea)
                return;

            int id = parcels.Count;
            parcels.Add(new Parcel(id, part.Rings[0], a + inward * 0.5f, b + inward * 0.5f, inward, edge.Kind, (float)part.Area));
            claimed.Add(part);
            foreach (var cell in Buckets(part.Bounds))
            {
                if (!index.TryGetValue(cell, out var list))
                    index[cell] = list = new List<int>();
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
