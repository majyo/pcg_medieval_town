using System;
using System.Collections.Generic;

namespace PcgCitySandbox.City;

/// <summary>Shape operations on a set of grid cells (a mask with one flag per cell).</summary>
public static class Morphology
{
    /// <summary>
    /// Closing with a disc of <paramref name="radius"/> cells: grows the set, then shrinks it back, which fills notches
    /// and gaps narrower than the disc and keeps every cell already in the set.
    /// </summary>
    public static bool[] Close(SiteGrid grid, bool[] mask, int radius)
    {
        var (x0, z0, x1, z1) = Bounds(grid, mask, radius + 1);
        var disc = new List<(int, int)>();
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dz * dz <= radius * radius)
                    disc.Add((dx, dz));
            }
        }

        bool[] Apply(bool[] source, bool grow)
        {
            var result = new bool[grid.Count];
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    bool value = !grow;
                    foreach (var (dx, dz) in disc)
                    {
                        bool cell = grid.InGrid(x + dx, z + dz) && source[grid.Index(x + dx, z + dz)];
                        if (cell == grow)
                        {
                            value = grow;
                            break;
                        }
                    }

                    result[grid.Index(x, z)] = value;
                }
            }

            return result;
        }

        var closed = Apply(Apply(mask, true), false);
        for (int k = 0; k < grid.Count; k++)
            closed[k] |= mask[k];
        return closed;
    }

    /// <summary>Adds every cell not in the set that cannot reach the map edge through other cells not in the set.</summary>
    public static void FillHoles(SiteGrid grid, bool[] mask)
    {
        var (x0, z0, x1, z1) = Bounds(grid, mask, 1);
        var outside = new bool[grid.Count];
        var queue = new Queue<int>();
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int k = grid.Index(x, z);
                if ((x == x0 || x == x1 || z == z0 || z == z1) && !mask[k])
                {
                    outside[k] = true;
                    queue.Enqueue(k);
                }
            }
        }

        while (queue.TryDequeue(out int k))
        {
            grid.ForSides(k, j =>
            {
                int x = j % grid.Nx, z = j / grid.Nx;
                if (x >= x0 && x <= x1 && z >= z0 && z <= z1 && !mask[j] && !outside[j])
                {
                    outside[j] = true;
                    queue.Enqueue(j);
                }
            });
        }

        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int k = grid.Index(x, z);
                mask[k] |= !outside[k];
            }
        }
    }

    /// <summary>Removes the cells not joined by side neighbors to one of <paramref name="seeds"/>.</summary>
    public static void KeepConnected(SiteGrid grid, bool[] mask, IEnumerable<int> seeds)
    {
        var reached = new bool[grid.Count];
        var queue = new Queue<int>();
        foreach (int s in seeds)
        {
            if (mask[s] && !reached[s])
            {
                reached[s] = true;
                queue.Enqueue(s);
            }
        }

        while (queue.TryDequeue(out int k))
        {
            grid.ForSides(k, j =>
            {
                if (mask[j] && !reached[j])
                {
                    reached[j] = true;
                    queue.Enqueue(j);
                }
            });
        }

        Array.Copy(reached, mask, mask.Length);
    }

    /// <summary>The cell range [x0, x1] × [z0, z1] holding the set, grown by <paramref name="pad"/> and clipped to the grid.</summary>
    private static (int X0, int Z0, int X1, int Z1) Bounds(SiteGrid grid, bool[] mask, int pad)
    {
        int x0 = grid.Nx, z0 = grid.Nz, x1 = -1, z1 = -1;
        for (int k = 0; k < grid.Count; k++)
        {
            if (!mask[k])
                continue;
            int x = k % grid.Nx, z = k / grid.Nx;
            (x0, x1, z0, z1) = (Math.Min(x0, x), Math.Max(x1, x), Math.Min(z0, z), Math.Max(z1, z));
        }

        if (x1 < 0)
            return (0, 0, -1, -1);
        return (Math.Max(0, x0 - pad), Math.Max(0, z0 - pad), Math.Min(grid.Nx - 1, x1 + pad), Math.Min(grid.Nz - 1, z1 + pad));
    }
}
