using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class StreetBlocksOptions
{
    [Param("城墙内侧退让 m", 0, 30, 0.5,
        Hint = "城墙内侧留出多宽不建设。墙根巷修在离墙 5.5–10.5 m，退让 5.5 m 时街区一直接到墙根巷")]
    public float WallSetback { get; set; } = 5.5f;
    [Param("城墙外侧退让 m", 0, 60, 1,
        Hint = "城墙外侧留出多宽不建设（墙外防守用的空地），与四期的城外离墙距离一致")]
    public float OutsideSetback { get; set; } = 15;
    [Param("最小街区面积 m²", 30, 400, 5,
        Hint = "比它小的碎块不算街区")]
    public float MinArea { get; set; } = 80;
}

/// <summary>
/// Developable land = the built cells, kept clear of the wall on both sides, minus the paved area, landmark plots and
/// unbuildable cells, plus the trades' cells (far from the wall, and the wet trades stand on flood land and banks on
/// purpose) minus the paved area; blocks are its parts. Unbuilt land (gardens, steep ground) stays open.
/// </summary>
public sealed class StreetBlocks : CityNode<StreetBlocksOptions>
{
    // Blocks run along the road edges for kilometers; a hair of clearance keeps rounding slivers off the paving.
    private const float PavingClearance = 0.05f;
    /// <summary>The narrowest land that counts: a house is at least 4 m deep.</summary>
    private const float MinWidth = 4;
    /// <summary>How far from the wall the built land reaches out toward a wall lane.</summary>
    private const float NearWall = 16;

    public override string Group => NodeGroups.Roads;
    public override string Title => "街区";
    public override string Description =>
        "城内外已建成的地在城墙两侧各留出空地，去掉路面、地标用地和不可建设地，再加上作坊用地（去掉路面），连通部分就是街区；没建成的地（陡坡、菜园）不建房。城墙内侧的建成地多伸出一格接上墙根巷；窄于 4 m 的地去掉。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Buildable, Keys.Wall, Keys.Roads, Keys.Landmarks, Keys.Growth, Keys.LandUse];
    public override IReadOnlyList<IKey> Writes => [Keys.Blocks];

    protected override void Run(NodeContext c, StreetBlocksOptions o)
    {
        var buildable = c.Get(Keys.Buildable);
        var wall = c.Get(Keys.Wall);
        var roads = c.Get(Keys.Roads);
        var growth = c.Get(Keys.Growth);
        float cell = buildable.Grid.CellSize;
        var built = growth.Footprint.Rounded(cell);
        // Near the wall the built land reaches one cell farther, so the blocks meet a wall lane on the unbuilt strip.
        var nearWall = wall.Interior.Subtract(wall.Interior.Offset(-NearWall));
        var inside = built.Union(growth.Footprint.Offset(cell - PavingClearance).Intersect(nearWall));
        var developable = wall.Interior.Offset(-o.WallSetback).Intersect(inside)
            .Union(built.Subtract(wall.Interior.Offset(o.OutsideSetback)))
            .Subtract(roads.Footprint.Offset(PavingClearance))
            .Subtract(c.Get(Keys.Landmarks).Plots)
            .Subtract(Unbuildable(buildable, growth))
            .Union(c.Get(Keys.LandUse).Area(LandUses.Trades).Rounded(buildable.Grid.CellSize)
                .Subtract(roads.Footprint.Offset(PavingClearance)));
        // Strips narrower than the shallowest house go: the land between the wall setback and a wall lane running a
        // little farther in, and thin necks.
        developable = developable.Offset(-MinWidth / 2).Offset(MinWidth / 2).Intersect(developable);
        var blocks = developable.Parts().Where(b => b.Area >= o.MinArea).ToList();
        c.Set(Keys.Blocks, new BlockSet(developable, blocks));
    }

    /// <summary>
    /// Unbuildable cells next to a built cell: the only ones the rounded built area can reach. The evaluation
    /// shares the growth grid, so cell indices agree.
    /// </summary>
    private static Region Unbuildable(CategoryField buildable, GrowthHistory growth)
    {
        var grid = buildable.Grid;
        return grid.Squares(Enumerable.Range(0, grid.Count)
            .Where(k => !buildable.Buildable(k) && grid.AnyNear(k, 1, growth.Built)));
    }
}
