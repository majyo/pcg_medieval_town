using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

/// <summary>
/// Turns graded roads and platforms into one ground surface, solved over the whole town at once rather than shape by
/// shape. Every lattice point within <see cref="Reach"/> of a shape is an unknown: its offset from the terrain. A
/// point inside a shape is pulled firmly to the shape's height; a point between shapes only wants to be smooth. The
/// least-squares solution is a continuous surface: shapes keep their heights, and the ground between them is the
/// smoothest that meets them all, so where two shapes disagree the difference spreads over the ground between them
/// instead of leaving a cliff. Where shapes overlap the higher class wins (market, then roads and squares, then
/// landmarks and plots), and shapes of one class average. A road holds its height loosely near its ends, so where
/// roads of different grades meet, the junction becomes one surface between them. Beyond the reach, on water points (<c>frozen</c>) and on the
/// rim of the map the ground stays as the terrain has it.
/// </summary>
public static class GradeBake
{
    /// <summary>How far beside a shape the ground may be reshaped; beyond it the terrain is untouched.</summary>
    public const float Reach = 10;

    /// <summary>Within this distance of the map's edge the ground is left alone; shapes fade in over the same again.</summary>
    public const float Rim = 20;

    /// <summary>How firmly a point keeps to the shape it lies in, by class, against a smoothness of 1 to each neighbor.</summary>
    private static readonly float[] Weights = [0, 100, 100, 200, 1000];

    /// <summary>The weight of the terrain itself on a point between shapes: just enough to settle points far from any.</summary>
    private const float Free = 0.001f;

    /// <summary>Over this distance from either end a road's hold on its height eases from <see cref="EndHold"/> to full.</summary>
    private const float JunctionReach = 8;
    private const float EndHold = 0.05f;

    private const int MaxIterations = 1500;
    private const double Tolerance = 1e-6;
    private const int Chunk = 8192;

    private enum Class : byte { None, Plot, Landmark, Road, Market }

    private static Class ClassOf(PlatformKind kind) => kind switch
    {
        PlatformKind.Market => Class.Market,
        PlatformKind.Square => Class.Road,
        PlatformKind.Landmark => Class.Landmark,
        _ => Class.Plot,
    };

    public static HeightPatch Bake(ITerrain terrain, IReadOnlyList<GradeCorridor> corridors, IReadOnlyList<GradePlatform> platforms,
        Func<Vector2, bool> frozen, CancellationToken cancel)
    {
        if (corridors.Count == 0 && platforms.Count == 0)
            return HeightPatch.Empty;

        // The lattice: every whole-meter point of the map that any shape's reach can touch.
        var bounds = new Rect2();
        bool first = true;
        void Grow(Rect2 box)
        {
            bounds = first ? box : bounds.Merge(box);
            first = false;
        }

        foreach (var corridor in corridors)
            Grow(Box(corridor.Points).Grow(corridor.Width / 2));
        foreach (var platform in platforms)
            Grow(platform.Area.Bounds);
        bounds = bounds.Grow(Reach + 2);
        int limit = (int)MathF.Floor(terrain.Half);
        int x0 = Math.Max(-limit, (int)MathF.Floor(bounds.Position.X)), x1 = Math.Min(limit, (int)MathF.Ceiling(bounds.End.X));
        int z0 = Math.Max(-limit, (int)MathF.Floor(bounds.Position.Y)), z1 = Math.Min(limit, (int)MathF.Ceiling(bounds.End.Y));
        int nx = x1 - x0 + 1, nz = z1 - z0 + 1;
        if (nx <= 0 || nz <= 0)
            return HeightPatch.Empty;

        // What each point lies in: the highest class of shape over it, and that class's heights averaged by how firmly
        // each holds (with the largest hold kept for the point's weight).
        var cls = new Class[nx * nz];
        var held = new float[nx * nz];
        var sum = new float[nx * nz];
        var hold = new float[nx * nz];
        void Put(int k, Class c, float height, float firm = 1)
        {
            if (c > cls[k])
                (cls[k], held[k], sum[k], hold[k]) = (c, firm, firm * height, firm);
            else if (c == cls[k])
                (held[k], sum[k], hold[k]) = (held[k] + firm, sum[k] + firm * height, MathF.Max(hold[k], firm));
        }

        void Visit(Rect2 box, Action<int, Vector2> visit)
        {
            int bx0 = Math.Max(x0, (int)MathF.Floor(box.Position.X)), bx1 = Math.Min(x1, (int)MathF.Ceiling(box.End.X));
            int bz0 = Math.Max(z0, (int)MathF.Floor(box.Position.Y)), bz1 = Math.Min(z1, (int)MathF.Ceiling(box.End.Y));
            for (int z = bz0; z <= bz1; z++)
            {
                for (int x = bx0; x <= bx1; x++)
                    visit((z - z0) * nx + x - x0, new Vector2(x, z));
            }
        }

        foreach (var platform in platforms)
        {
            cancel.ThrowIfCancellationRequested();
            var c = ClassOf(platform.Kind);
            Visit(platform.Area.Bounds, (k, p) =>
            {
                if (platform.Area.Contains(p))
                    Put(k, c, platform.Plane.HeightAt(p));
            });
        }

        // A road is level across: a point takes the height of the nearest point of the road's center line, not an
        // average of every piece of the line whose width reaches it. The line steps along the site grid, and where it
        // climbs a slope the pieces around a step differ in height, so it is first rounded, with its heights. The
        // corners the rounding cuts off are left to the ground around.
        var nearest = new Dictionary<int, (float Distance, float Height, float Firm)>();
        foreach (var corridor in corridors)
        {
            cancel.ThrowIfCancellationRequested();
            float half = corridor.Width / 2;
            var (points, heights) = Round(corridor.Points, corridor.Heights);
            var along = new float[points.Length];
            for (int i = 1; i < along.Length; i++)
                along[i] = along[i - 1] + points[i].DistanceTo(points[i - 1]);
            float length = along[^1];
            nearest.Clear();
            for (int i = 1; i < points.Length; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                float ha = heights[i - 1], hb = heights[i];
                float sa = along[i - 1], sb = along[i];
                var ab = b - a;
                float length2 = ab.LengthSquared();
                if (length2 < 1e-6f)
                    continue;
                Visit(Box([a, b]).Grow(half), (k, p) =>
                {
                    float t = Math.Clamp((p - a).Dot(ab) / length2, 0, 1);
                    float d = p.DistanceTo(a + ab * t);
                    if (d > half || nearest.TryGetValue(k, out var best) && best.Distance <= d)
                        return;
                    float s = Mathf.Lerp(sa, sb, t);
                    float end = MathF.Min(s, length - s) / JunctionReach;
                    nearest[k] = (d, Mathf.Lerp(ha, hb, t), Mathf.Lerp(EndHold, 1, Math.Clamp(end, 0, 1)));
                });
            }

            foreach (var (k, point) in nearest)
                Put(k, Class.Road, point.Height, point.Firm);
        }

        // The unknowns: points within the reach of a shape (steps of the lattice, diagonals counted as one), off the
        // water and the rim.
        int band = (int)MathF.Ceiling(Reach);
        var steps = new byte[nx * nz];
        Array.Fill(steps, byte.MaxValue);
        var queue = new Queue<int>();
        for (int k = 0; k < cls.Length; k++)
        {
            if (cls[k] != Class.None)
            {
                steps[k] = 0;
                queue.Enqueue(k);
            }
        }

        while (queue.TryDequeue(out int k))
        {
            if (steps[k] >= band)
                continue;
            int x = k % nx, z = k / nx;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int jx = x + dx, jz = z + dz;
                    if (jx < 0 || jz < 0 || jx >= nx || jz >= nz)
                        continue;
                    int j = jz * nx + jx;
                    if (steps[j] > steps[k] + 1)
                    {
                        steps[j] = (byte)(steps[k] + 1);
                        queue.Enqueue(j);
                    }
                }
            }
        }

        float mapHalf = terrain.Half;
        var index = new int[nx * nz];
        Array.Fill(index, -1);
        var cells = new List<int>();
        for (int k = 0; k < index.Length; k++)
        {
            if (steps[k] == byte.MaxValue)
                continue;
            var p = new Vector2(x0 + k % nx, z0 + k / nx);
            if (mapHalf - MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)) < Rim || frozen(p))
                continue;
            index[k] = cells.Count;
            cells.Add(k);
        }

        int n = cells.Count;
        if (n == 0)
            return HeightPatch.Empty;

        // The system (W + L) d = W g: W the weights, L the lattice Laplacian over the unknowns (a neighbor that is not
        // an unknown keeps offset 0), g the offset each shape asks for.
        var diagonal = new double[n];
        var rhs = new double[n];
        var neighbors = new int[n * 4];
        var offsets = new double[n];
        Parallel.For(0, n, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            int k = cells[i], x = k % nx, z = k / nx;
            var p = new Vector2(x0 + x, z0 + z);
            float weight = Free, goal = 0;
            if (cls[k] != Class.None)
            {
                float rim = Math.Clamp((mapHalf - MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)) - Rim) / Rim, 0, 1);
                weight = Weights[(int)cls[k]] * hold[k];
                goal = (sum[k] / held[k] - terrain.HeightAt(p)) * rim;
            }

            diagonal[i] = weight + 4;
            rhs[i] = weight * goal;
            offsets[i] = goal;
            neighbors[i * 4] = x > 0 ? index[k - 1] : -1;
            neighbors[i * 4 + 1] = x < nx - 1 ? index[k + 1] : -1;
            neighbors[i * 4 + 2] = z > 0 ? index[k - nx] : -1;
            neighbors[i * 4 + 3] = z < nz - 1 ? index[k + nx] : -1;
        });

        Solve(diagonal, neighbors, rhs, offsets, cancel);

        // The patch spans the unknowns; points outside them keep offset 0.
        int px0 = int.MaxValue, pz0 = int.MaxValue, px1 = int.MinValue, pz1 = int.MinValue;
        foreach (int k in cells)
        {
            int x = k % nx, z = k / nx;
            (px0, px1, pz0, pz1) = (Math.Min(px0, x), Math.Max(px1, x), Math.Min(pz0, z), Math.Max(pz1, z));
        }

        int pnx = px1 - px0 + 1, pnz = pz1 - pz0 + 1;
        var patch = new float[pnx * pnz];
        for (int i = 0; i < n; i++)
        {
            int k = cells[i];
            patch[(k / nx - pz0) * pnx + k % nx - px0] = (float)offsets[i];
        }

        return new HeightPatch(x0 + px0, z0 + pz0, pnx, pnz, patch);
    }

    /// <summary>
    /// Conjugate gradients with a Jacobi preconditioner on the system whose matrix has <paramref name="diagonal"/> on
    /// its diagonal and −1 for each neighbor. Starts from <paramref name="x"/> and leaves the solution there. Sums are
    /// taken in fixed chunks, so the result does not depend on how the work is shared among threads.
    /// </summary>
    private static void Solve(double[] diagonal, int[] neighbors, double[] b, double[] x, CancellationToken cancel)
    {
        int n = x.Length;
        var r = new double[n];
        var z = new double[n];
        var p = new double[n];
        var ap = new double[n];
        Multiply(diagonal, neighbors, x, ap);
        for (int i = 0; i < n; i++)
        {
            r[i] = b[i] - ap[i];
            z[i] = r[i] / diagonal[i];
            p[i] = z[i];
        }

        double scale = Math.Max(1e-12, Dot(b, b));
        double rz = Dot(r, z);
        for (int iteration = 0; iteration < MaxIterations && Dot(r, r) > Tolerance * Tolerance * scale; iteration++)
        {
            if (iteration % 32 == 0)
                cancel.ThrowIfCancellationRequested();
            Multiply(diagonal, neighbors, p, ap);
            double alpha = rz / Dot(p, ap);
            Parallel.For(0, (n + Chunk - 1) / Chunk, c =>
            {
                for (int i = c * Chunk; i < Math.Min(n, (c + 1) * Chunk); i++)
                {
                    x[i] += alpha * p[i];
                    r[i] -= alpha * ap[i];
                    z[i] = r[i] / diagonal[i];
                }
            });
            double next = Dot(r, z);
            double beta = next / rz;
            rz = next;
            Parallel.For(0, (n + Chunk - 1) / Chunk, c =>
            {
                for (int i = c * Chunk; i < Math.Min(n, (c + 1) * Chunk); i++)
                    p[i] = z[i] + beta * p[i];
            });
        }
    }

    private static void Multiply(double[] diagonal, int[] neighbors, double[] v, double[] result)
    {
        int n = v.Length;
        Parallel.For(0, (n + Chunk - 1) / Chunk, c =>
        {
            for (int i = c * Chunk; i < Math.Min(n, (c + 1) * Chunk); i++)
            {
                double value = diagonal[i] * v[i];
                for (int s = 0; s < 4; s++)
                {
                    int j = neighbors[i * 4 + s];
                    if (j >= 0)
                        value -= v[j];
                }

                result[i] = value;
            }
        });
    }

    private static double Dot(double[] a, double[] b)
    {
        int chunks = (a.Length + Chunk - 1) / Chunk;
        var partial = new double[chunks];
        Parallel.For(0, chunks, c =>
        {
            double s = 0;
            for (int i = c * Chunk; i < Math.Min(a.Length, (c + 1) * Chunk); i++)
                s += a[i] * b[i];
            partial[c] = s;
        });
        double total = 0;
        foreach (double s in partial)
            total += s;
        return total;
    }

    /// <summary>Two rounds of corner cutting (Chaikin) on a line and its heights together; the ends stay.</summary>
    private static (Vector2[] Points, float[] Heights) Round(Vector2[] points, float[] heights)
    {
        for (int pass = 0; pass < 2 && points.Length > 2; pass++)
        {
            var p = new List<Vector2> { points[0] };
            var h = new List<float> { heights[0] };
            for (int i = 0; i + 1 < points.Length; i++)
            {
                p.Add(points[i].Lerp(points[i + 1], 0.25f));
                h.Add(Mathf.Lerp(heights[i], heights[i + 1], 0.25f));
                p.Add(points[i].Lerp(points[i + 1], 0.75f));
                h.Add(Mathf.Lerp(heights[i], heights[i + 1], 0.75f));
            }

            p.Add(points[^1]);
            h.Add(heights[^1]);
            (points, heights) = (p.ToArray(), h.ToArray());
        }

        return (points, heights);
    }

    private static Rect2 Box(IReadOnlyList<Vector2> points)
    {
        var box = new Rect2(points[0], Vector2.Zero);
        foreach (var p in points)
            box = box.Expand(p);
        return box;
    }
}
