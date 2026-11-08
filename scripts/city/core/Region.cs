using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// The single planar polygon type: an immutable set of rings (outer rings and holes, non-zero fill)
/// in meters, stored at centimeter precision. All boolean and offset operations go through here.
/// </summary>
public sealed class Region
{
    private const double Scale = 100;
    private readonly Paths64 _paths;
    private Rect2? _bounds;

    public static readonly Region Empty = new(new Paths64());

    private Region(Paths64 paths)
    {
        _paths = paths;
    }

    public static Region Polygon(IEnumerable<Vector2> ring) => new(new Paths64 { ToPath(ring) });

    public static Region Polygons(IEnumerable<IEnumerable<Vector2>> rings) =>
        new(Clipper.Union(new Paths64(rings.Select(ToPath)), FillRule.NonZero));

    /// <summary>Buffers open polylines: each one becomes a band of the given full width with round ends.</summary>
    public static Region Lines(IEnumerable<(IReadOnlyList<Vector2> Points, float Width)> lines)
    {
        var result = new Paths64();
        foreach (var group in lines.GroupBy(l => l.Width))
        {
            var paths = new Paths64(group.Select(l => ToPath(l.Points)));
            result.AddRange(Clipper.InflatePaths(paths, group.Key * Scale / 2, JoinType.Round, EndType.Round));
        }

        return new Region(Clipper.Union(result, FillRule.NonZero));
    }

    public bool IsEmpty => _paths.Count == 0;
    public double Area => Math.Abs(Clipper.Area(_paths)) / (Scale * Scale);

    public IReadOnlyList<Vector2[]> Rings => _paths.Select(ToRing).ToArray();

    public Rect2 Bounds => _bounds ??= ComputeBounds();

    public Region Union(Region other) => new(Clipper.Union(_paths, other._paths, FillRule.NonZero));
    public Region Intersect(Region other) => new(Clipper.Intersect(_paths, other._paths, FillRule.NonZero));
    public Region Subtract(Region other) => new(Clipper.Difference(_paths, other._paths, FillRule.NonZero));

    /// <summary>Positive grows, negative shrinks.</summary>
    public Region Offset(float meters) =>
        new(Clipper.InflatePaths(_paths, meters * Scale, JoinType.Miter, EndType.Polygon));

    public bool Contains(Vector2 point)
    {
        if (!Bounds.HasPoint(point))
            return false;

        var p = new Point64(point.X * Scale, point.Y * Scale);
        int winding = 0;
        foreach (var path in _paths)
        {
            if (Clipper.PointInPolygon(p, path) != PointInPolygonResult.IsOutside)
                winding += Clipper.IsPositive(path) ? 1 : -1;
        }

        return winding != 0;
    }

    /// <summary>Splits into connected parts, each an outer ring with its holes.</summary>
    public IReadOnlyList<Region> Parts()
    {
        var tree = new PolyTree64();
        var clipper = new Clipper64();
        clipper.AddSubject(_paths);
        clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
        var parts = new List<Region>();
        Collect(tree, parts);
        return parts;
    }

    /// <summary>Triangle list (three vertices per triangle) covering the region.</summary>
    public Vector2[] Triangulate()
    {
        if (IsEmpty || Clipper.Triangulate(_paths, out Paths64 triangles) != TriangulateResult.success)
            return Array.Empty<Vector2>();

        return triangles.SelectMany(ToRing).ToArray();
    }

    private static void Collect(PolyPath64 node, List<Region> parts)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var outer = (PolyPath64)node[i];
            var paths = new Paths64 { outer.Polygon! };
            for (int j = 0; j < outer.Count; j++)
            {
                var hole = (PolyPath64)outer[j];
                paths.Add(hole.Polygon!);
                Collect(hole, parts);
            }

            parts.Add(new Region(paths));
        }
    }

    private Rect2 ComputeBounds()
    {
        if (IsEmpty)
            return new Rect2();

        var rect = Clipper.GetBounds(_paths);
        return new Rect2(
            (float)(rect.left / Scale),
            (float)(rect.top / Scale),
            (float)((rect.right - rect.left) / Scale),
            (float)((rect.bottom - rect.top) / Scale));
    }

    private static Path64 ToPath(IEnumerable<Vector2> points) =>
        new(points.Select(p => new Point64(p.X * Scale, p.Y * Scale)));

    private static Vector2[] ToRing(Path64 path) =>
        path.Select(p => new Vector2((float)(p.X / Scale), (float)(p.Y / Scale))).ToArray();
}
