using System;

namespace PcgCitySandbox.City;

/// <summary>
/// Summed-area table of one value per grid cell: the mean over any square window of cells in constant time.
/// Windows are clipped to the grid.
/// </summary>
public sealed class SummedArea
{
    private readonly SiteGrid _grid;
    private readonly double[] _sum;

    public SummedArea(SiteGrid grid, float[] values)
    {
        _grid = grid;
        int nx = grid.Nx;
        // _sum[z * (nx + 1) + x] = total of cells [0, x) × [0, z).
        _sum = new double[(nx + 1) * (grid.Nz + 1)];
        for (int z = 0; z < grid.Nz; z++)
        {
            double row = 0;
            for (int x = 0; x < nx; x++)
            {
                row += values[grid.Index(x, z)];
                _sum[(z + 1) * (nx + 1) + x + 1] = _sum[z * (nx + 1) + x + 1] + row;
            }
        }
    }

    /// <summary>Mean of the cells at most <paramref name="r"/> cells away from (x, z) along each axis.</summary>
    public double Mean(int x, int z, int r)
    {
        int nx = _grid.Nx;
        int x0 = Math.Max(0, x - r), x1 = Math.Min(nx, x + r + 1);
        int z0 = Math.Max(0, z - r), z1 = Math.Min(_grid.Nz, z + r + 1);
        double total = _sum[z1 * (nx + 1) + x1] - _sum[z0 * (nx + 1) + x1] - _sum[z1 * (nx + 1) + x0] + _sum[z0 * (nx + 1) + x0];
        return total / ((x1 - x0) * (z1 - z0));
    }
}
