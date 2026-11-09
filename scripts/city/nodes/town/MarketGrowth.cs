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
    public override string Group => TownGrowth.Group;
    public override string Title => "二期 市镇";
    public override string Description =>
        "市场和教堂用地先算作建成，再以市场为起点沿道路扩展，到累计约 6 ha。";
    public override IReadOnlyList<IKey> Reads => [Keys.Plaza, Keys.Landmarks, Keys.Routes, Keys.Habitability, Keys.Buildable];
    public override IReadOnlyList<IKey> Edits => [Keys.Growth];

    protected override void Run(NodeContext c, MarketGrowthOptions o)
    {
        var history = c.Get(Keys.Growth);
        var plaza = c.Get(Keys.Plaza);
        var network = c.Get(Keys.Routes);
        var buildable = c.Get(Keys.Buildable);
        var grid = buildable.Grid;
        var built = Array.ConvertAll(history.Phase, p => p != 0);

        var plazaCells = Growth.CellsIn(grid, plaza.Area);
        var founded = plazaCells.Concat(c.Get(Keys.Landmarks).Landmarks.SelectMany(l => l.Cells(grid)))
            .Where(k => !built[k] && buildable.Buildable(k)).Distinct().ToList();
        foreach (int k in founded)
            built[k] = true;

        int target = o.Cells(grid) - built.Count(b => b);
        var cells = target <= 0 ? new List<int>()
            : Growth.Spread(network, buildable, c.Get(Keys.Habitability), built, Growth.RoadSeeds(network, plazaCells), target,
                o.Rules, null, c.Cancellation);
        if (cells.Count < target)
            c.Notice($"可建设地不足，市镇累计只有 {(built.Count(b => b) + cells.Count) * grid.CellSize * grid.CellSize / 10000:0.0} ha");
        c.Set(Keys.Growth, history.Add("市镇", founded.Concat(cells).ToList()));
    }
}
