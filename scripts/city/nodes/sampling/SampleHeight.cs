using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace PcgCitySandbox.City;

public sealed class SampleHeightOptions
{
    [Param("每格子样本（边）", 1, 4, 1)] public int SubSamples { get; set; } = 3;
}

/// <summary>
/// Ground height of each cell: the mean of k×k evenly spaced samples inside it. Averaging filters the meter-scale
/// relief that a single point sample would alias onto the grid.
/// </summary>
public sealed class SampleHeight : CityNode<SampleHeightOptions>
{
    public override string Group => Sampling.Group;
    public override string Title => "高程";
    public override IReadOnlyList<IKey> Reads => [Keys.Grid];
    public override IReadOnlyList<IKey> Writes => [Keys.Height];

    protected override void Run(NodeContext c, SampleHeightOptions o)
    {
        var grid = c.Get(Keys.Grid);
        var terrain = c.Terrain;
        int k = o.SubSamples;
        float step = grid.CellSize / k;
        var values = new float[grid.Count];
        var options = new ParallelOptions { CancellationToken = c.Cancellation };
        Parallel.For(0, grid.Nz, options, z =>
        {
            for (int x = 0; x < grid.Nx; x++)
            {
                var corner = grid.Origin + new Vector2(x, z) * grid.CellSize;
                float sum = 0;
                for (int j = 0; j < k; j++)
                {
                    for (int i = 0; i < k; i++)
                        sum += terrain.HeightAt(corner + new Vector2(i + 0.5f, j + 0.5f) * step);
                }

                values[grid.Index(x, z)] = sum / (k * k);
            }
        });
        c.Set(Keys.Height, new ScalarField(grid, values, "高程", "m"));
    }
}
