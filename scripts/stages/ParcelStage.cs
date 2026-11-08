using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.Core;

namespace PcgCitySandbox.Stages;

public sealed class ParcelOptions
{
    [Param("最小面宽 m", 4, 20, 0.5)] public float FrontageMin { get; set; } = 7;
    [Param("最大面宽 m", 5, 30, 0.5)] public float FrontageMax { get; set; } = 12;
    [Param("地块进深 m", 10, 60, 1)] public float Depth { get; set; } = 26;
    [Param("最小面积 m²", 30, 400, 5)] public float MinArea { get; set; } = 80;
    [Param("城墙内侧退让 m", 0, 30, 1)] public float WallSetback { get; set; } = 8;
    [Param("种子", 0, 99999)] public int Seed { get; set; } = 1;
}

/// <summary>A street-fronting plot. Front runs along the street edge; Inward points away from the street.</summary>
public sealed record Parcel(int Id, Vector2[] Ring, Vector2 FrontA, Vector2 FrontB, Vector2 Inward, RoadKind Street, float Area)
{
    public float Frontage => FrontA.DistanceTo(FrontB);
}

public sealed class ParcelSet : IStageResult
{
    public required Region Developable { get; init; }
    public required IReadOnlyList<Region> Blocks { get; init; }
    public required IReadOnlyList<Parcel> Parcels { get; init; }
    public List<string> Notices { get; } = new();

    public string Summary
    {
        get
        {
            double area = Parcels.Sum(p => (double)p.Area);
            return $"{Blocks.Count} 街区，{Parcels.Count} 地块，占可建区 {area / Math.Max(1, Developable.Area):P0}";
        }
    }

    IReadOnlyList<string> IStageResult.Notices => Notices;
}

public sealed class ParcelStage : Stage<ParcelOptions, ParcelSet>
{
    private const float Bucket = 40;

    public override string Title => "04 街区与地块";

    protected override ParcelSet Run(StageInputs inputs, ParcelOptions o)
    {
        if (o.FrontageMax < o.FrontageMin)
            throw new ArgumentException("最大面宽不能小于最小面宽");

        var site = inputs.Get<Site>();
        var layout = inputs.Get<TownLayout>();
        var roads = inputs.Get<RoadGraph>();
        var developable = layout.Interior.Offset(-o.WallSetback)
            .Subtract(roads.Footprint)
            .Subtract(Unbuildable(site, layout.Interior.Bounds));
        var blocks = developable.Parts().Where(b => b.Area >= o.MinArea).ToList();

        var parcels = new List<Parcel>();
        var claimed = new List<Region>();
        var index = new Dictionary<(int, int), List<int>>();
        var rng = new Random(o.Seed);
        var ordered = roads.Edges.Where(e => e.Kind != RoadKind.Outer)
            .OrderBy(e => e.Kind).ThenByDescending(e => e.Length).ThenBy(e => e.Id);
        foreach (var edge in ordered)
        {
            inputs.Cancellation.ThrowIfCancellationRequested();
            foreach (int side in new[] { 1, -1 })
            {
                for (int s = 1; s < edge.Points.Length; s++)
                    Frontage(edge, edge.Points[s - 1], edge.Points[s], side);
            }
        }

        return new ParcelSet { Developable = developable, Blocks = blocks, Parcels = parcels };

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
                Claim(edge, a, b, inward, front);
                t += width;
            }
        }

        void Claim(RoadEdge edge, Vector2 a, Vector2 b, Vector2 inward, float front)
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
            var frontA = a + inward * 0.5f;
            var frontB = b + inward * 0.5f;
            parcels.Add(new Parcel(id, part.Rings[0], frontA, frontB, inward, edge.Kind, (float)part.Area));
            claimed.Add(part);
            foreach (var cell in Buckets(part.Bounds))
            {
                if (!index.TryGetValue(cell, out var list))
                    index[cell] = list = new List<int>();
                list.Add(id);
            }
        }

        IEnumerable<int> Nearby(Rect2 bounds) =>
            Buckets(bounds).SelectMany(c => index.TryGetValue(c, out var list) ? list : Enumerable.Empty<int>()).Distinct();
    }

    private static IEnumerable<(int, int)> Buckets(Rect2 r)
    {
        for (int z = (int)MathF.Floor(r.Position.Y / Bucket); z <= (int)MathF.Floor(r.End.Y / Bucket); z++)
            for (int x = (int)MathF.Floor(r.Position.X / Bucket); x <= (int)MathF.Floor(r.End.X / Bucket); x++)
                yield return (x, z);
    }

    private static Region Unbuildable(Site site, Rect2 bounds)
    {
        var squares = new List<Vector2[]>();
        float c = site.CellSize;
        for (int k = 0; k < site.Count; k++)
        {
            var p = site.Center(k);
            if (site.Buildable(k) || !bounds.Grow(c).HasPoint(p))
                continue;
            squares.Add(new[]
            {
                p + new Vector2(-c, -c) / 2, p + new Vector2(c, -c) / 2,
                p + new Vector2(c, c) / 2, p + new Vector2(-c, c) / 2,
            });
        }

        return squares.Count == 0 ? Region.Empty : Region.Polygons(squares);
    }
}
