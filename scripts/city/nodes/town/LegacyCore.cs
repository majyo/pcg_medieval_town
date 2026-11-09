using System.Collections.Generic;

namespace PcgCitySandbox.City;

public sealed class LegacyCoreOptions
{
    [Param("规划半径 m", 100, 450, 10,
        Hint = "过渡用：星形城墙按这个半径围绕市场画。改为围住建成区筑墙（规划第 4 步）后删除")]
    public float Radius { get; set; } = 230;
}

/// <summary>
/// Transitional: the market's center with a fixed planned radius, for the star wall and the gates until the wall
/// encloses the grown town.
/// </summary>
public sealed class LegacyCore : CityNode<LegacyCoreOptions>
{
    public override string Group => TownGrowth.Group;
    public override string Title => "城址（过渡）";
    public override string Description =>
        "过渡用：以市场中心为城址，加固定的规划半径，供星形城墙、城门和主街使用。城墙改为围住建成区后删除。";
    public override IReadOnlyList<IKey> Reads => [Keys.Plaza];
    public override IReadOnlyList<IKey> Writes => [Keys.Core];

    protected override void Run(NodeContext c, LegacyCoreOptions o) =>
        c.Set(Keys.Core, new TownCore(c.Get(Keys.Plaza).Center, o.Radius));
}
