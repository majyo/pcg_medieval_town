using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class SuburbLanesOptions
{
    [Param("巷道间距 m", 25, 120, 5,
        Hint = "撒点的间距。临街一排地块约 20–25 m 深，45 m 时后面正好再一排；调小后巷道更密")]
    public float Spacing { get; set; } = 45;
    [Param("成环概率", 0, 1, 0.05,
        Hint = "可以成环的连线被采用的概率。城外多是尽头巷和后巷，所以比城内低")]
    public float LoopChance { get; set; } = 0.2f;
    [Param("离墙 m", 0, 60, 1,
        Hint = "城外离城墙这么近的地不铺巷道，与街区的城墙外侧退让一致")]
    public float WallSetback { get; set; } = 15;
    [Param("种子", 0, 99999,
        Hint = "只影响撒点位置和成环的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;
}

/// <summary>
/// Lanes in the suburbs (<see cref="StreetWeave"/>): points and lanes only on the built land outside the wall, clear of
/// the wall, starting from the outer roads that run through it. A roadside ribbon one or two plots deep has no room
/// for a point, so only the suburbs that have grown thick get lanes.
/// </summary>
public sealed class SuburbLanes : CityNode<SuburbLanesOptions>
{
    public override string Group => NodeGroups.Roads;
    public override string Title => "城外巷道";
    public override string Description =>
        "在城外长厚了的街区里按间距撒点，从穿过它的城外道路岔出巷道，连成以尽头巷和后巷为主的小路；只沿路一两排深的地方不铺。";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones, Keys.Growth, Keys.Wall];
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, SuburbLanesOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var growth = c.Get(Keys.Growth);
        var wall = c.Get(Keys.Wall);
        var grid = zones.Grid;
        var links = c.Get(Keys.RoadLinks).Copy();

        // The suburbs: built cells outside the wall, clear of it.
        var nearWall = grid.Mask(grid.CellsIn(wall.Interior.Offset(o.WallSetback)));
        var area = new bool[grid.Count];
        for (int k = 0; k < grid.Count; k++)
            area[k] = growth.Built(k) && !zones.Inside(k) && !nearWall[k] && zones.Allowed(k, RoadZone.Outside);

        var roads = links.Cells.ToHashSet();
        int failed = StreetWeave.Weave(zones, links, RoadZone.Outside, k => !zones.Inside(k) && grid.AnyNear(k, 1, j => area[j]),
            k => area[k] && !zones.IsGate(k), k => area[k] || roads.Contains(k), o.Spacing, o.LoopChance,
            new Random(o.Seed), c.Cancellation);
        if (failed > 0)
            c.Notice($"{failed} 条候选巷道无合法通路，已跳过");
        c.Set(Keys.RoadLinks, links);
    }
}
