using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class WalledInfillOptions : SpreadOptions
{
    [Param("填充比例", 0.2, 1, 0.05,
        Hint = "这一期结束时，城内可建设的地有多大比例建成。1 是填满（菜园在各家地块的后院）；调小后城内留出成片的菜园")]
    public float Fill { get; set; } = 1;
    [Param("离墙 m", 0, 30, 1,
        Hint = "离城墙这么近的地不建房（墙根空地），与街区的城墙退让一致")]
    public float WallSetback { get; set; } = 8;
}

/// <summary>
/// Phase three: the walled town fills in. It grows along the roads from the market and the gates, only inside the
/// wall and clear of it, until the given share of the buildable land inside is built (all of it by default; gardens
/// are then the backs of the plots).
/// </summary>
public sealed class WalledInfill : CityNode<WalledInfillOptions>
{
    /// <summary>The name of the phase this node adds.</summary>
    public const string PhaseName = "城内填充";

    public override string Group => NodeGroups.Town;
    public override string Title => "三期 城内填充";
    public override string Description =>
        "筑墙之后，城内从市场和各城门沿道路继续生长，直到城内可建设的地建成一定比例（默认填满）；离墙太近的地不建。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Wall, Keys.Gates, Keys.Plaza, Keys.Routes, Keys.Habitability, Keys.Buildable, Keys.Landmarks];
    public override IReadOnlyList<IKey> Edits => [Keys.Growth];

    protected override void Run(NodeContext c, WalledInfillOptions o)
    {
        var history = c.Get(Keys.Growth);
        var wall = c.Get(Keys.Wall);
        var network = c.Get(Keys.Routes);
        var buildable = c.Get(Keys.Buildable);
        var grid = buildable.Grid;

        // Not on the landmarks' plots (a castle, a friary): their land is set aside.
        var plots = c.Get(Keys.Landmarks).Cells();
        var allowed = new bool[grid.Count];
        foreach (int k in grid.CellsIn(wall.Interior))
            allowed[k] = Geometry.DistanceToRing(grid.Center(k), wall.Ring) >= o.WallSetback && !plots.Contains(k);
        int land = Enumerable.Range(0, grid.Count).Count(k => allowed[k] && buildable.Buildable(k));
        int already = Enumerable.Range(0, grid.Count).Count(k => allowed[k] && history.Built(k));
        int target = (int)MathF.Round(o.Fill * land) - already;

        var seeds = Growth.MarketSeeds(network, c.Get(Keys.Plaza))
            .Concat(c.Get(Keys.Gates).Cells(grid).Where(network.Cells.Contains));
        var built = history.BuiltMask();
        var cells = target <= 0 ? new List<int>()
            : Growth.Spread(network, buildable, c.Get(Keys.Habitability), built, seeds, target, o.Rules, k => allowed[k],
                c.Cancellation);
        if (target <= 0)
            c.Notice($"城内已建成 {already * 100 / Math.Max(1, land)}%，不少于填充比例，这一期没有新建");
        else if (cells.Count < target)
            c.Notice("城内能沿道路长到的地不足，填充比例没有达到");
        c.Set(Keys.Growth, history.Add(PhaseName, cells));
    }
}
