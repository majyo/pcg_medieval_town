using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PcgCitySandbox.City;

public sealed class SlopeAspectOptions
{
    [Param("平地阈值 °", 0, 5, 0.1,
        Hint = "坡度低于它的格算作平地，不给坡向。调大后更多缓坡被视为平地，宜居度里的朝向因素也随之不起作用")]
    public float FlatSlope { get; set; } = 1;
}

/// <summary>
/// Slope and aspect from the sampled heights with Horn's 3×3 difference (edges reuse the nearest row or column).
/// Aspect is the compass bearing the ground faces, downhill, clockwise from north (-Z); flat cells have none.
/// </summary>
public sealed class SlopeAspect : CityNode<SlopeAspectOptions>
{
    /// <summary>Aspect value of cells flatter than the threshold.</summary>
    public const float NoAspect = -1;

    public override string Group => NodeGroups.Sampling;
    public override string Title => "坡度坡向";
    public override string Description =>
        "由高程算出每格的坡度和坡向（坡面朝哪个方向）。";
    public override IReadOnlyList<IKey> Reads => [Keys.Height];
    public override IReadOnlyList<IKey> Writes => [Keys.Slope, Keys.Aspect];

    protected override void Run(NodeContext c, SlopeAspectOptions o)
    {
        var height = c.Get(Keys.Height);
        var grid = height.Grid;
        var h = height.Values;
        var slope = new float[grid.Count];
        var aspect = new float[grid.Count];
        float H(int x, int z) => h[grid.Index(Math.Clamp(x, 0, grid.Nx - 1), Math.Clamp(z, 0, grid.Nz - 1))];
        var options = new ParallelOptions { CancellationToken = c.Cancellation };
        Parallel.For(0, grid.Nz, options, z =>
        {
            for (int x = 0; x < grid.Nx; x++)
            {
                float dx = (H(x + 1, z - 1) + 2 * H(x + 1, z) + H(x + 1, z + 1)
                    - H(x - 1, z - 1) - 2 * H(x - 1, z) - H(x - 1, z + 1)) / (8 * grid.CellSize);
                float dz = (H(x - 1, z + 1) + 2 * H(x, z + 1) + H(x + 1, z + 1)
                    - H(x - 1, z - 1) - 2 * H(x, z - 1) - H(x + 1, z - 1)) / (8 * grid.CellSize);
                int k = grid.Index(x, z);
                float degrees = MathF.Atan(MathF.Sqrt(dx * dx + dz * dz)) * 180 / MathF.PI;
                slope[k] = degrees;
                // Downhill is (-dx, -dz); north is -Z and east +X, so the bearing is atan2(east, north).
                float bearing = MathF.Atan2(-dx, dz) * 180 / MathF.PI;
                aspect[k] = degrees < o.FlatSlope ? NoAspect : (bearing + 360) % 360;
            }
        });
        c.Set(Keys.Slope, new ScalarField(grid, slope, "坡度", "°"));
        c.Set(Keys.Aspect, new ScalarField(grid, aspect, "坡向", "°", NoAspect, circular: true));
    }
}
