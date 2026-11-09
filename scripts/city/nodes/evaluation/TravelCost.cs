using System.Collections.Generic;
using System.Threading.Tasks;

namespace PcgCitySandbox.City;

public sealed class TravelCostOptions
{
    /// <summary>Marks cells that cannot be crossed (water, too steep for a road).</summary>
    public const float Impassable = -1;

    [Param("可通行坡度上限 °", 5, 35, 0.5,
        Hint = "坡度超过它的格不可通行（无值）。调小后谷坡可能变成屏障，过境道路只能绕行")]
    public float MaxSlope { get; set; } = 20;
    [Param("每度坡度加价", 0, 0.3, 0.01,
        Hint = "每 1° 坡度让代价增加多少。调大后道路更愿意绕开坡地")]
    public float PerDegree { get; set; } = 0.08f;
    [Param("林地加价", 0, 3, 0.1,
        Hint = "密林（覆盖 1）让代价增加多少，按覆盖比例折算。调大后道路更愿意绕开森林")]
    public float ForestCost { get; set; } = 0.8f;
}

/// <summary>
/// How hard each cell is to cross on foot or by cart: meters of level, open ground one meter here is worth (at least
/// 1). Steeper ground and forest cost more; water and cliffs cannot be crossed (no value). The cost does not depend
/// on direction: a valley side too steep to climb straight up can still be crossed, at a price, as a road would
/// cross it on a slant.
/// </summary>
public sealed class TravelCost : CityNode<TravelCostOptions>
{
    public override string Group => Evaluation.Group;
    public override string Title => "通行代价";
    public override string Description =>
        "算出走过每格有多费劲：走 1 m 相当于平地几米，坡陡、林密更费劲；水面和陡崖不可通行。";
    public override IReadOnlyList<IKey> Reads => [Keys.Slope, Keys.Shore, Keys.Forest];
    public override IReadOnlyList<IKey> Writes => [Keys.TravelCost];

    protected override void Run(NodeContext c, TravelCostOptions o)
    {
        var slope = c.Get(Keys.Slope);
        var shore = c.Get(Keys.Shore).Values;
        var forest = c.Get(Keys.Forest).Values;
        var grid = slope.Grid;
        var cost = new float[grid.Count];
        var options = new ParallelOptions { CancellationToken = c.Cancellation };
        Parallel.For(0, grid.Count, options, k =>
        {
            float s = slope.Values[k];
            cost[k] = shore[k] < 0 || s > o.MaxSlope ? TravelCostOptions.Impassable
                : 1 + o.PerDegree * s + o.ForestCost * forest[k];
        });
        c.Set(Keys.TravelCost, new ScalarField(grid, cost, "通行代价", "×", TravelCostOptions.Impassable));
    }
}
