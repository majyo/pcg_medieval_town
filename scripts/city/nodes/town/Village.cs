using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

/// <summary>Options shared by the growth phases: how far a phase reaches and how it spreads along the roads.</summary>
public class GrowthOptions : SpreadOptions
{
    [Param("累计面积 ha", 0.5, 30, 0.1,
        Hint = "这一期结束时城镇建成区的总面积（含之前各期）")]
    public float Area { get; set; } = 2;

}

/// <summary>How a growth phase spreads along the roads (<see cref="GrowthRules"/>), as node parameters.</summary>
public class SpreadOptions
{
    [Param("临路进深 m", 8, 100, 2,
        Hint = "离路这么远以内的地按普通代价收入；更远的每米代价陡增，所以小面积时是沿路的带状")]
    public float Depth { get; set; } = 30;
    [Param("离路代价", 1, 10, 0.5,
        Hint = "离路 1 m 相当于沿路走多少米。调大后更贴着道路，沿路拉得更长")]
    public float DepthCost { get; set; } = 3;
    [Param("宜居度权重 m", 0, 300, 10,
        Hint = "宜居度为 1 的格相当于沿路近多少米。调大后更偏向好地，形状更不规则")]
    public float HabitabilityWeight { get; set; } = 80;

    public GrowthRules Rules => new(Depth, DepthCost, HabitabilityWeight);
}

public sealed class VillageOptions : GrowthOptions
{
}

/// <summary>Phase one: a village strung along the through routes from the origin.</summary>
public sealed class Village : CityNode<VillageOptions>
{
    public override string Group => NodeGroups.Town;
    public override string Title => "一期 村落";
    public override string Description =>
        "从起源点沿过境道路长出约 2 ha 的村落：先占临路的地，沿路越远、离路越深越晚收入，宜居度高的地优先。";
    public override IReadOnlyList<IKey> Reads => [Keys.Origin, Keys.Routes, Keys.Habitability, Keys.Buildable];
    public override IReadOnlyList<IKey> Writes => [Keys.Growth];

    protected override void Run(NodeContext c, VillageOptions o)
    {
        var origin = c.Get(Keys.Origin);
        var network = c.Get(Keys.Routes);
        var buildable = c.Get(Keys.Buildable);
        var grid = buildable.Grid;
        var history = GrowthHistory.Empty(grid);
        int target = grid.CellCount(o.Area);
        var cells = Growth.Spread(network, buildable, c.Get(Keys.Habitability), new bool[grid.Count], new[] { origin.Cell },
            target, o.Rules, null, c.Cancellation);
        if (cells.Count < target)
            c.Notice($"可建设地不足，村落只有 {grid.Hectares(cells.Count):0.0} ha");
        c.Set(Keys.Growth, history.Add("村落", cells));
    }
}
