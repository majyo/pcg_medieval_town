using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PcgCitySandbox.City;

public sealed class LandformPositionOptions
{
    [Param("比较半径 m", 20, 200, 5,
        Hint = "和多大范围的周围比较（方窗半边长）。调小只看附近的小起伏；调大看出整片台地和河谷")]
    public float Radius { get; set; } = 60;
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
    public override string Description =>
        "比较每格与周围的平均高程：高出为岗地、台地边缘，低于为谷地。";
    public override IReadOnlyList<IKey> Reads => [Keys.Height];
    public override IReadOnlyList<IKey> Writes => [Keys.Relief];

    protected override void Run(NodeContext c, LandformPositionOptions o)
    {
        var height = c.Get(Keys.Height);
        var grid = height.Grid;
        var sums = new SummedArea(grid, height.Values);
        c.Cancellation.ThrowIfCancellationRequested();
        int r = Math.Max(1, (int)MathF.Round(o.Radius / grid.CellSize));
        var relief = new float[grid.Count];
        var options = new ParallelOptions { CancellationToken = c.Cancellation };
        Parallel.For(0, grid.Nz, options, z =>
        {
            for (int x = 0; x < grid.Nx; x++)
            {
                int k = grid.Index(x, z);
                relief[k] = (float)(height.Values[k] - sums.Mean(x, z, r));
            }
        });
        c.Set(Keys.Relief, new ScalarField(grid, relief, "地形位置", "m"));
    }
}
