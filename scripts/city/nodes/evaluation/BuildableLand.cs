using System.Collections.Generic;
using System.Threading.Tasks;

namespace PcgCitySandbox.City;

public static class Evaluation
{
    public const string Group = "场地评价";
}

public sealed class BuildableLandOptions
{
    [Param("易淹高度 m", 0, 6, 0.1)] public float FloodHeight { get; set; } = 2.5f;
    [Param("河岸退让 m", 0, 80, 1)] public float RiverSetback { get; set; } = 14;
    [Param("建设坡度上限 °", 2, 30, 0.5)] public float MaxSlope { get; set; } = 9;
    [Param("森林覆盖上限", 0, 1, 0.05)] public float ForestLimit { get; set; } = 0.55f;
}

/// <summary>
/// Whether each cell can be built on, from the sampled fields; the first reason that rules a cell out wins: water,
/// ground that floods (too little height above the water), the bank setback, steep ground, dense forest.
/// </summary>
public sealed class BuildableLand : CityNode<BuildableLandOptions>
{
    public override string Group => Evaluation.Group;
    public override string Title => "可建设判定";
    public override IReadOnlyList<IKey> Reads => [Keys.Slope, Keys.Shore, Keys.AboveWater, Keys.Forest];
    public override IReadOnlyList<IKey> Writes => [Keys.Buildable];

    protected override void Run(NodeContext c, BuildableLandOptions o)
    {
        var slope = c.Get(Keys.Slope);
        var shore = c.Get(Keys.Shore).Values;
        var above = c.Get(Keys.AboveWater).Values;
        var forest = c.Get(Keys.Forest).Values;
        var grid = slope.Grid;
        var block = new byte[grid.Count];
        var options = new ParallelOptions { CancellationToken = c.Cancellation };
        Parallel.For(0, grid.Count, options, k =>
        {
            block[k] = (byte)(shore[k] < 0 ? SiteBlock.Water
                : above[k] < o.FloodHeight ? SiteBlock.Flood
                : shore[k] < o.RiverSetback ? SiteBlock.Bank
                : slope.Values[k] > o.MaxSlope ? SiteBlock.Slope
                : forest[k] > o.ForestLimit ? SiteBlock.Forest
                : SiteBlock.None);
        });
        c.Set(Keys.Buildable, new CategoryField(grid, block, "可建设判定", SiteBlocks.Names));
    }
}
