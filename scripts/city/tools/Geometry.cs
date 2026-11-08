using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>Small planar helpers shared by nodes and checks.</summary>
public static class Geometry
{
    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-8f ? 0 : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return p.DistanceTo(a + ab * t);
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
