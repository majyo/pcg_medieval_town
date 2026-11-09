using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PcgCitySandbox.City;

public sealed class LandformPositionOptions
{
    [Param("比较半径 m", 20, 200, 5)] public float Radius { get; set; } = 60;
}

/// <summary>
/// How much higher a cell is than its surroundings: its height minus the mean height of the square window of side
/// 2R around it (clipped at the map edge). Positive on rises and terrace edges, negative in hollows and valleys.
/// The window mean comes from a summed-area table, so the cost does not grow with the radius.
/// </summary>
public sealed class LandformPosition : CityNode<LandformPositionOptions>
{
    public override string Group => Sampling.Group;
    public override string Title => "地形位置";
    public override IReadOnlyList<IKey> Reads => [Keys.Height];
    public override IReadOnlyList<IKey> Writes => [Keys.Relief];

    protected override void Run(NodeContext c, LandformPositionOptions o)
    {
        var height = c.Get(Keys.Height);
        var grid = height.Grid;
        int nx = grid.Nx, nz = grid.Nz;
        // sum[(z) * (nx + 1) + x] = total height of cells [0, x) × [0, z).
        var sum = new double[(nx + 1) * (nz + 1)];
        for (int z = 0; z < nz; z++)
        {
            double row = 0;
            for (int x = 0; x < nx; x++)
            {
                row += height.Values[grid.Index(x, z)];
                sum[(z + 1) * (nx + 1) + x + 1] = sum[z * (nx + 1) + x + 1] + row;
            }
        }

        c.Cancellation.ThrowIfCancellationRequested();
        int r = Math.Max(1, (int)MathF.Round(o.Radius / grid.CellSize));
        var relief = new float[grid.Count];
        var options = new ParallelOptions { CancellationToken = c.Cancellation };
        Parallel.For(0, nz, options, z =>
        {
            int z0 = Math.Max(0, z - r), z1 = Math.Min(nz, z + r + 1);
            for (int x = 0; x < nx; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(nx, x + r + 1);
                double total = sum[z1 * (nx + 1) + x1] - sum[z0 * (nx + 1) + x1] - sum[z1 * (nx + 1) + x0] + sum[z0 * (nx + 1) + x0];
                double mean = total / ((x1 - x0) * (z1 - z0));
                int k = grid.Index(x, z);
                relief[k] = (float)(height.Values[k] - mean);
            }
        });
        c.Set(Keys.Relief, new ScalarField(grid, relief, "地形位置", "m"));
    }
}
