using System.Collections.Generic;
using System.Threading.Tasks;

namespace PcgCitySandbox.City;

/// <summary>
/// The basic water facts a town needs, per cell: how far it is from the water's edge (negative in the water) and
/// how high its sampled ground sits above the water level of the nearest river reach. Both come straight from the
/// terrain's nearest-river query; there is no runoff or drainage modeling.
/// </summary>
public sealed class SurfaceWater : CityNode<NoOptions>
{
    public override string Group => NodeGroups.Sampling;
    public override string Title => "地表水";
    public override string Description =>
        "量出每格离河道边多远、比最近河段的水位高多少。不模拟径流和汇水。";
    public override IReadOnlyList<IKey> Reads => [Keys.Height];
    public override IReadOnlyList<IKey> Writes => [Keys.Shore, Keys.AboveWater];

    protected override void Run(NodeContext c, NoOptions o)
    {
        var height = c.Get(Keys.Height);
        var grid = height.Grid;
        var terrain = c.Terrain;
        var shore = new float[grid.Count];
        var above = new float[grid.Count];
        // Without any river, every cell is as far from water as the map is wide, and levels count from zero.
        float farthest = grid.Size.Length();
        var options = new ParallelOptions { CancellationToken = c.Cancellation };
        Parallel.For(0, grid.Count, options, k =>
        {
            var river = terrain.NearestRiver(grid.Center(k));
            bool none = float.IsPositiveInfinity(river.Distance);
            shore[k] = none ? farthest : river.Distance - river.HalfWidth;
            above[k] = height.Values[k] - (none ? 0 : river.WaterHeight);
        });
        c.Set(Keys.Shore, new ScalarField(grid, shore, "离水距离", "m"));
        c.Set(Keys.AboveWater, new ScalarField(grid, above, "高出水面", "m"));
    }
}
