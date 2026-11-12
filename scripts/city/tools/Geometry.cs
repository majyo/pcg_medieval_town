using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>Small planar helpers shared by nodes and checks.</summary>
public static class Geometry
{
    /// <summary>The point of segment ab nearest <paramref name="p"/>.</summary>
    public static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-8f ? 0 : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return a + ab * t;
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b) => p.DistanceTo(ClosestOnSegment(p, a, b));

    /// <summary>Distance to an open polyline.</summary>
    public static float DistanceToLine(Vector2 p, IReadOnlyList<Vector2> line)
    {
        float best = float.PositiveInfinity;
        for (int i = 1; i < line.Count; i++)
            best = Math.Min(best, DistanceToSegment(p, line[i - 1], line[i]));
        return best;
    }

    public static float DistanceToRing(Vector2 p, Vector2[] ring)
    {
        float best = float.MaxValue;
        for (int i = 0; i < ring.Length; i++)
            best = Math.Min(best, DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Length]));
        return best;
    }

    /// <summary>Douglas-Peucker: keeps the end points and every point farther than the tolerance from the chord.</summary>
    public static Vector2[] Simplify(IReadOnlyList<Vector2> points, float tolerance)
    {
        if (points.Count < 3)
            return points.ToArray();

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float worst = 0;
            int index = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistanceToSegment(points[i], points[a], points[b]);
                if (d > worst)
                {
                    worst = d;
                    index = i;
                }
            }

            if (index >= 0 && worst > tolerance)
            {
                keep[index] = true;
                stack.Push((a, index));
                stack.Push((index, b));
            }
        }

        return points.Where((_, i) => keep[i]).ToArray();
    }

    /// <summary>Points along a polyline no farther apart than <paramref name="step"/>, ends included.</summary>
    public static IEnumerable<Vector2> Samples(Vector2[] points, float step)
    {
        for (int i = 1; i < points.Length; i++)
        {
            int count = Math.Max(1, (int)(points[i - 1].DistanceTo(points[i]) / step));
            for (int j = 0; j <= count; j++)
                yield return points[i - 1].Lerp(points[i], j / (float)count);
        }
    }

    /// <summary>
    /// The stations of a line: its two ends and points between them about <paramref name="step"/> apart, none closer
    /// than half a meter to the one before.
    /// </summary>
    public static Vector2[] Stations(Vector2[] line, float step)
    {
        var points = new List<Vector2> { line[0] };
        foreach (var p in Samples(line, step))
        {
            if (p.DistanceSquaredTo(points[^1]) > 0.25f)
                points.Add(p);
        }

        // Keep the line's own end, not a sample just short of it.
        if (points.Count == 1)
            points.Add(line[^1]);
        else if (points[^1].DistanceSquaredTo(line[^1]) <= 0.25f)
            points[^1] = line[^1];
        else
            points.Add(line[^1]);
        return points.ToArray();
    }

    /// <summary>Deterministic value in [0, 1] from a cell index and a seed.</summary>
    public static float Hash(int k, int seed)
    {
        unchecked
        {
            uint h = (uint)(k * 73856093 ^ seed * 19349663);
            h ^= h >> 16;
            h *= 0x7feb352d;
            h ^= h >> 15;
            return (h & 0xffff) / 65535f;
        }
    }
}
