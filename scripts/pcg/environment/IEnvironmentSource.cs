using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PcgCitySandbox.Pcg;

public enum LayerStatus { Unavailable, Provided, KnownEmpty }
public readonly record struct EnvironmentLayers(LayerStatus Height, LayerStatus Slope, LayerStatus Water,
    LayerStatus Vegetation, LayerStatus NoBuild, LayerStatus Obstacles);
public readonly record struct EnvironmentPoint(float? Height, float? Slope, bool? Water, float? Vegetation);

public interface IEnvironmentSource
{
    string SourceId { get; }
    long Revision { get; }
    Rect2 Bounds { get; }
    float? NativeSpacing { get; }
    EnvironmentLayers Layers { get; }
    IReadOnlyList<ConstraintPolygon> NoBuildZones { get; }
    IReadOnlyList<ConstraintPolygon> Obstacles { get; }
    bool Contains(Vector2 worldXZ);
    EnvironmentPoint Query(Vector2 worldXZ);
}

// A simple, closed, non-self-intersecting ring. No holes; callers may provide multiple polygons.
public sealed class ConstraintPolygon
{
    public string Id { get; }
    public ReadOnlyCollection<Vector2> Points { get; }
    public Rect2 Bounds { get; }

    public ConstraintPolygon(string id, IEnumerable<Vector2> points)
    {
        var p = points.ToArray();
        if (string.IsNullOrWhiteSpace(id) || p.Length < 3 || p.Any(v => !v.IsFinite()))
            throw new ArgumentException("约束轮廓需要标识和至少三个有限顶点。");
        double area = 0;
        for (int i = 0; i < p.Length; i++)
        {
            Vector2 a = p[i], b = p[(i + 1) % p.Length];
            if (a == b) throw new ArgumentException("约束轮廓有重复相邻顶点。");
            area += (double)a.X * b.Y - (double)a.Y * b.X;
            for (int j = i + 1; j < p.Length; j++)
            {
                if (j == i + 1 || (i == 0 && j == p.Length - 1)) continue;
                if (IntersectsSegments(a, b, p[j], p[(j + 1) % p.Length]))
                    throw new ArgumentException("约束轮廓不能自相交。");
            }
        }
        if (Math.Abs(area) < 1e-8) throw new ArgumentException("约束轮廓面积为零。");
        Id = id; Points = Array.AsReadOnly(p);
        Vector2 min = p[0], max = p[0];
        foreach (var v in p) { min = min.Min(v); max = max.Max(v); }
        Bounds = new Rect2(min, max - min);
    }

    public bool Intersects(Rect2 rect)
    {
        if (Bounds.End.X < rect.Position.X || Bounds.Position.X > rect.End.X ||
            Bounds.End.Y < rect.Position.Y || Bounds.Position.Y > rect.End.Y) return false;
        Vector2 a = rect.Position, b = new(rect.End.X, rect.Position.Y), c = rect.End, d = new(rect.Position.X, rect.End.Y);
        if (Contains(a)) return true;
        for (int i = 0; i < Points.Count; i++)
        {
            Vector2 p = Points[i], q = Points[(i + 1) % Points.Count];
            if (Inside(rect, p) || IntersectsSegments(p, q, a, b) || IntersectsSegments(p, q, b, c) ||
                IntersectsSegments(p, q, c, d) || IntersectsSegments(p, q, d, a)) return true;
        }
        return false;
    }

    private bool Contains(Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var a = Points[j]; var b = Points[i];
            if (OnSegment(a, b, p)) return true;
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (double)(b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    internal static bool Inside(Rect2 r, Vector2 p) => p.IsFinite() && p.X >= r.Position.X && p.X <= r.End.X && p.Y >= r.Position.Y && p.Y <= r.End.Y;
    private static double Cross(Vector2 a, Vector2 b, Vector2 p) => ((double)b.X - a.X) * (p.Y - a.Y) - ((double)b.Y - a.Y) * (p.X - a.X);
    private static bool OnSegment(Vector2 a, Vector2 b, Vector2 p) => Math.Abs(Cross(a, b, p)) <= 1e-8 &&
        p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
    private static bool IntersectsSegments(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
        return (Math.Sign(abC) * Math.Sign(abD) < 0 && Math.Sign(cdA) * Math.Sign(cdB) < 0) ||
            OnSegment(a, b, c) || OnSegment(a, b, d) || OnSegment(c, d, a) || OnSegment(c, d, b);
    }
}
