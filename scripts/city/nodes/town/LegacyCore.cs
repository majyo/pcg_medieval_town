using System.Collections.Generic;

namespace PcgCitySandbox.City;

public sealed class LegacyCoreOptions
{
    [Param("规划半径 m", 100, 450, 10,
        Hint = "过渡用：只有主街用到城址（以它为终点），半径已不起作用。改为骨架道路（规划第 4 步后半）后删除")]
    public float Radius { get; set; } = 230;
}

/// <summary>
/// Transitional: the market's center (with a planned radius no node uses any more), where the main streets end until
/// the skeleton roads replace them.
/// </summary>
public sealed class LegacyCore : CityNode<LegacyCoreOptions>
{
    public override string Group => TownGrowth.Group;
    public override string Title => "城址（过渡）";
    public override string Description =>
        "过渡用：以市场中心为城址，主街从各城门修到这里。改为骨架道路后删除。";
    public override IReadOnlyList<IKey> Reads => [Keys.Plaza];
    public override IReadOnlyList<IKey> Writes => [Keys.Core];

    protected override void Run(NodeContext c, LegacyCoreOptions o) =>
        c.Set(Keys.Core, new TownCore(c.Get(Keys.Plaza).Center, o.Radius));
}
