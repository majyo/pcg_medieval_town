using System;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// Finds the best place for a landmark's plot: a rectangle of whole grid cells whose every cell is allowed, with at
/// least one cell that is near what it must be near, scoring lowest.
/// </summary>
public static class PlotSearch
{
    /// <param name="score">The score of a plot by its center; lower is better, NaN rules it out.</param>
    /// <returns>The plot's first cell column and row, or null when nothing fits.</returns>
    public static (int X0, int Z0)? Best(SiteGrid grid, int nx, int nz, Func<int, bool> allowed, Func<int, bool> near,
        Func<Vector2, float> score)
    {
        var blocked = new float[grid.Count];
        var close = new float[grid.Count];
        for (int k = 0; k < grid.Count; k++)
        {
            blocked[k] = allowed(k) ? 0 : 1;
            close[k] = near(k) ? 1 : 0;
        }

        var blockedSums = new SummedArea(grid, blocked);
        var closeSums = new SummedArea(grid, close);
        (int, int)? best = null;
        float bestScore = float.PositiveInfinity;
        for (int z0 = 0; z0 + nz <= grid.Nz; z0++)
        {
            for (int x0 = 0; x0 + nx <= grid.Nx; x0++)
            {
                if (blockedSums.Total(x0, z0, x0 + nx, z0 + nz) > 0 || closeSums.Total(x0, z0, x0 + nx, z0 + nz) <= 0)
                    continue;
                float s = score(Center(grid, x0, z0, nx, nz));
                if (s < bestScore)
                {
                    bestScore = s;
                    best = (x0, z0);
                }
            }
        }

        return best;
    }

    public static Vector2 Center(SiteGrid grid, int x0, int z0, int nx, int nz) =>
        grid.Origin + new Vector2(x0 + nx / 2f, z0 + nz / 2f) * grid.CellSize;

    /// <summary>The plot's rectangle.</summary>
    public static Region Plot(SiteGrid grid, int x0, int z0, int nx, int nz)
    {
        var corner = grid.Origin + new Vector2(x0, z0) * grid.CellSize;
        var size = new Vector2(nx, nz) * grid.CellSize;
        return Region.Polygon(new[] { corner, corner + new Vector2(size.X, 0), corner + size, corner + new Vector2(0, size.Y) });
    }
}
