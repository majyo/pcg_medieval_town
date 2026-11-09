using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>Distances on the site grid: how far each cell lies from a set of cells or from a ring.</summary>
public static class Proximity
{
    /// <summary>
    /// Distance from each cell to the nearest of <paramref name="sources"/> (8-neighbor steps, so within about 8% of
    /// the straight line), computed out to <paramref name="radius"/>; farther cells are infinite.
    /// </summary>
    public static float[] From(SiteGrid grid, IEnumerable<int> sources, float radius)
    {
        var distance = new float[grid.Count];
        Array.Fill(distance, float.PositiveInfinity);
        var open = new PriorityQueue<int, float>();
        foreach (int k in sources)
        {
            distance[k] = 0;
            open.Enqueue(k, 0);
        }

        float diagonal = grid.CellSize * MathF.Sqrt(2);
        while (open.TryDequeue(out int k, out float d))
        {
            if (d > distance[k])
                continue;
            int x = k % grid.Nx, z = k / grid.Nx;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dz) == 0 || !grid.InGrid(x + dx, z + dz))
                        continue;
                    int j = grid.Index(x + dx, z + dz);
                    float nd = d + (dx != 0 && dz != 0 ? diagonal : grid.CellSize);
                    if (nd <= radius && nd < distance[j])
                    {
                        distance[j] = nd;
                        open.Enqueue(j, nd);
                    }
                }
            }
        }

        return distance;
    }

    /// <summary>
    /// Distance from each cell center to <paramref name="ring"/>, out to <paramref name="reach"/>; farther cells are
    /// infinite. Cells the ring passes near get the exact distance; from them each cell inherits its neighbor's nearest
    /// ring point, nearest first, so the distance stays exact to within a fraction of a cell at any range.
    /// </summary>
    public static float[] ToRing(SiteGrid grid, IReadOnlyList<Vector2> ring, float reach)
    {
        var distance = new float[grid.Count];
        Array.Fill(distance, float.PositiveInfinity);
        var nearest = new Vector2[grid.Count];
        var open = new PriorityQueue<int, float>();
        void Seed(int k, Vector2 point)
        {
            float d = grid.Center(k).DistanceTo(point);
            if (d < distance[k] && d <= reach)
            {
                distance[k] = d;
                nearest[k] = point;
                open.Enqueue(k, d);
            }
        }

        // Every cell within two cells of a segment, with its exact nearest point on that segment.
        float step = grid.CellSize / 2;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            int pieces = Math.Max(1, (int)MathF.Ceiling(a.DistanceTo(b) / step));
            for (int s = 0; s <= pieces; s++)
            {
                int c = grid.CellAt(a.Lerp(b, s / (float)pieces));
                if (c < 0)
                    continue;
                int cx = c % grid.Nx, cz = c / grid.Nx;
                for (int dz = -2; dz <= 2; dz++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (grid.InGrid(cx + dx, cz + dz))
                        {
                            int k = grid.Index(cx + dx, cz + dz);
                            Seed(k, Closest(grid.Center(k), a, b));
                        }
                    }
                }
            }
        }

        while (open.TryDequeue(out int k, out float d))
        {
            if (d > distance[k])
                continue;
            int x = k % grid.Nx, z = k / grid.Nx;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dz) != 0 && grid.InGrid(x + dx, z + dz))
                        Seed(grid.Index(x + dx, z + dz), nearest[k]);
                }
            }
        }

        return distance;
    }

    private static Vector2 Closest(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() > 0 ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1) : 0;
        return a + ab * t;
    }
}
