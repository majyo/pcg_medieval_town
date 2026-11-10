using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class MarketGrowthOptions : GrowthOptions
{
    public MarketGrowthOptions() => Area = 8;
}

/// <summary>Phase two: the market town. The plaza and the church become built, then the town spreads from the plaza.</summary>
public sealed class MarketGrowth : CityNode<MarketGrowthOptions>
{
    public override string Group => NodeGroups.Town;
    public override string Title => "二期 市镇";
    public override string Description =>
        "市场和教堂用地先算作建成，再以市场为起点沿道路扩展，到累计约 8 ha。";
    public override IReadOnlyList<IKey> Reads => [Keys.Plaza, Keys.Landmarks, Keys.Routes, Keys.Habitability, Keys.Buildable];
    public override IReadOnlyList<IKey> Edits => [Keys.Growth];

    protected override void Run(NodeContext c, MarketGrowthOptions o)
    {
        var history = c.Get(Keys.Growth);
        var plaza = c.Get(Keys.Plaza);
        var network = c.Get(Keys.Routes);
        var buildable = c.Get(Keys.Buildable);
        var grid = buildable.Grid;
        var built = history.BuiltMask();

        var founded = grid.CellsIn(plaza.Area).Concat(c.Get(Keys.Landmarks).Cells())
            .Where(k => !built[k] && buildable.Buildable(k)).Distinct().ToList();
        foreach (int k in founded)
            built[k] = true;

        int target = grid.CellCount(o.Area) - built.Count(b => b);
        var cells = target <= 0 ? new List<int>()
            : Growth.Spread(network, buildable, c.Get(Keys.Habitability), built, Growth.MarketSeeds(network, plaza), target,
                o.Rules, null, c.Cancellation);
        if (cells.Count < target)
            c.Notice($"可建设地不足，市镇累计只有 {grid.Hectares(built.Count(b => b) + cells.Count):0.0} ha");
        c.Set(Keys.Growth, history.Add("市镇", founded.Concat(cells).ToList()));
    }
}
