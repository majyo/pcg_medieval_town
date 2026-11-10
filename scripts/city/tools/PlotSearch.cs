using System;

namespace PcgCitySandbox.City;

/// <summary>
/// Finds the best place for a landmark's plot: a rectangle of whole grid cells whose every cell is allowed, with at
/// least one cell that is near what it must be near, scoring lowest. Ties go to the first in row order.
/// </summary>
public static class PlotSearch
{
    /// <param name="score">The score of a plot; lower is better, NaN rules it out.</param>
    /// <returns>The best plot, or null when nothing fits.</returns>
    public static CellRect? Best(SiteGrid grid, int nx, int nz, Func<int, bool> allowed, Func<int, bool> near,
        Func<CellRect, float> score)
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
        CellRect? best = null;
        float bestScore = float.PositiveInfinity;
        for (int z0 = 0; z0 + nz <= grid.Nz; z0++)
        {
            for (int x0 = 0; x0 + nx <= grid.Nx; x0++)
            {
                if (blockedSums.Total(x0, z0, x0 + nx, z0 + nz) > 0 || closeSums.Total(x0, z0, x0 + nx, z0 + nz) <= 0)
                    continue;
                var rect = new CellRect(x0, z0, nx, nz);
                float s = score(rect);
                if (s < bestScore)
                {
                    bestScore = s;
                    best = rect;
                }
            }
        }

        return best;
    }
}
