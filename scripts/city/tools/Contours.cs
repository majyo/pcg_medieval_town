using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>One piece of an iso-line: both ends lie where the field equals <see cref="Level"/>.</summary>
public readonly record struct ContourSegment(Vector2 A, Vector2 B, float Level);

/// <summary>Iso-lines of a scalar field by marching squares over the lattice of cell centers.</summary>
public static class Contours
{
    /// <summary>
    /// Segments at every multiple of <paramref name="interval"/>. A corner counts as above a level when its value is
    /// at least the level; saddles are resolved by the mean of the four corners. Points are interpolated linearly
    /// along lattice edges, so bilinear sampling of the field at any point returns its level.
    /// </summary>
    public static List<ContourSegment> Extract(ScalarField field, float interval)
    {
        var grid = field.Grid;
        var v = field.Values;
        var rows = new List<ContourSegment>[Math.Max(0, grid.Nz - 1)];
        Parallel.For(0, rows.Length, z =>
        {
            var row = new List<ContourSegment>();
            // Corners counter-clockwise from (x, z); edge i runs from corner i to corner i + 1.
            Span<float> h = stackalloc float[4];
            Span<Vector2> p = stackalloc Vector2[4];
            for (int x = 0; x < grid.Nx - 1; x++)
            {
                h[0] = v[grid.Index(x, z)];
                h[1] = v[grid.Index(x + 1, z)];
                h[2] = v[grid.Index(x + 1, z + 1)];
                h[3] = v[grid.Index(x, z + 1)];
                p[0] = grid.Center(x, z);
                p[1] = grid.Center(x + 1, z);
                p[2] = grid.Center(x + 1, z + 1);
                p[3] = grid.Center(x, z + 1);
                float low = MathF.Min(MathF.Min(h[0], h[1]), MathF.Min(h[2], h[3]));
                float high = MathF.Max(MathF.Max(h[0], h[1]), MathF.Max(h[2], h[3]));
                for (int k = (int)MathF.Floor(low / interval) + 1; k * interval <= high; k++)
                    Cell(row, h, p, k * interval);
            }

            rows[z] = row;
        });
        return rows.SelectMany(r => r).ToList();
    }

    private static void Cell(List<ContourSegment> output, Span<float> h, Span<Vector2> p, float level)
    {
        Span<Vector2> cross = stackalloc Vector2[4];
        Span<bool> crosses = stackalloc bool[4];
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            if (h[i] >= level == h[j] >= level)
                continue;
            float t = (level - h[i]) / (h[j] - h[i]);
            cross[i] = p[i].Lerp(p[j], t);
            crosses[i] = true;
            count++;
        }

        if (count == 2)
        {
            int a = crosses.IndexOf(true);
            int b = crosses[(a + 1)..].IndexOf(true) + a + 1;
            output.Add(new ContourSegment(cross[a], cross[b], level));
        }
        else if (count == 4)
        {
            // Saddle: if the center sides with corner 1, corner 1 joins the center and corners 0 and 2 are cut off.
            bool centerAbove = (h[0] + h[1] + h[2] + h[3]) / 4 >= level;
            bool isolateEven = centerAbove == h[1] >= level;
            if (isolateEven)
            {
                output.Add(new ContourSegment(cross[3], cross[0], level));
                output.Add(new ContourSegment(cross[1], cross[2], level));
            }
            else
            {
                output.Add(new ContourSegment(cross[0], cross[1], level));
                output.Add(new ContourSegment(cross[2], cross[3], level));
            }
        }
    }
}
