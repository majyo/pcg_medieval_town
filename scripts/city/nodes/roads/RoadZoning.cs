using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class RoadZoningOptions
{
    [Param("道路坡度上限 °", 3, 25, 0.5,
        Hint = "坡度超过它的格不能修路。调小后主街和城外道路更绕")]
    public float MaxSlope { get; set; } = 12;
}

/// <summary>
/// Marks which site cells roads may use: dry, not too steep, off the landmarks' plots (with room for a lane's
/// half-width and its simplification), and on one side of the wall or a gate.
/// </summary>
public sealed class RoadZoning : CityNode<RoadZoningOptions>
{
    // Half a secondary street's width (5 m) plus the most a simplified street strays from its cells.
    private const float PlotClearance = 4.1f;

    public override string Group => NodeGroups.Roads;
    public override string Title => "通行区";
    public override string Description =>
        "标出哪些格可以修路：不是水、坡不太陡、不在地标用地上（留出半条街的宽度），分城内和城外；只能在城门处穿过城墙。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Buildable, Keys.Slope, Keys.Wall, Keys.Gates, Keys.Landmarks, Keys.Routes];
    public override IReadOnlyList<IKey> Writes => [Keys.RoadZones];

    protected override void Run(NodeContext c, RoadZoningOptions o)
    {
        var buildable = c.Get(Keys.Buildable);
        var slope = c.Get(Keys.Slope);
        var grid = buildable.Grid;
        var wall = c.Get(Keys.Wall);
        var inside = wall.Inside(grid);
        var nearWall = new bool[grid.Count];
        var passable = new bool[grid.Count];
        var wallBounds = wall.Interior.Bounds.Grow(grid.CellSize * 2);
        var routes = c.Get(Keys.Routes).Cells;
        var plots = grid.CellsIn(c.Get(Keys.Landmarks).Plots.Offset(PlotClearance))
            .Where(k => !routes.Contains(k)).ToHashSet();
        for (int k = 0; k < grid.Count; k++)
        {
            var p = grid.Center(k);
            passable[k] = buildable.Block(k) != SiteBlock.Water && slope.Values[k] <= o.MaxSlope && !plots.Contains(k);
            if (wallBounds.HasPoint(p))
                nearWall[k] = Geometry.DistanceToRing(p, wall.Ring) < grid.CellSize * 0.5f;
        }

        c.Set(Keys.RoadZones, new RoadZones(buildable, slope, inside, nearWall, passable, c.Get(Keys.Gates).Cells(grid)));
    }
}
