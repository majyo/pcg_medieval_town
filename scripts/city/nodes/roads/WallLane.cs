using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class WallLaneOptions
{
    [Param("墙根巷",
        Hint = "沿城墙内侧修一圈巷（守城时调兵的路），关掉后墙根照旧空着")]
    public bool Enabled { get; set; } = true;
    [Param("离墙 m", 8, 16, 0.5,
        Hint = "巷中线离墙线的距离；巷修在离墙这个距离前后 2.5 m 的格上（离墙 5.4 m 以内的格不修路）")]
    public float Distance { get; set; } = 8;
    [Param("最短 m", 10, 120, 5,
        Hint = "两端相距不到这么远的一段不修（城门之间很短的墙段、被地标截下的零碎段）")]
    public float MinLength { get; set; } = 40;
}

/// <summary>
/// The lane along the inside of the wall: on the cells about a set distance from the wall line (<see cref="IsoStreets"/>),
/// cut at every gate, so each piece runs between two gates and joins the main street by each; landmarks against the
/// wall (castle, friary), steep ground and water cut it further, and those ends join the nearest road or stay dead
/// ends.
/// </summary>
public sealed class WallLane : CityNode<WallLaneOptions>
{
    /// <summary>Half the band of cells the lane may use, either side of its distance from the wall.</summary>
    private const float HalfBand = 2.5f;
    /// <summary>The band stops this far from a gate, so each piece ends by the main street through it.</summary>
    private const float GateGap = 8;
    /// <summary>How far an end may be from the road it joins.</summary>
    private const float JoinReach = 14;

    public override string Group => NodeGroups.Roads;
    public override string Title => "墙根巷";
    public override string Description =>
        "沿城墙内侧、离墙一段距离修巷，在每座城门处断开、接到穿过城门的主街；碰到贴墙的地标、陡坡和水再断开，断头接最近的路。";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones, Keys.Wall, Keys.Gates];
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, WallLaneOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var links = c.Get(Keys.RoadLinks).Copy();
        if (!o.Enabled)
        {
            c.Set(Keys.RoadLinks, links);
            return;
        }

        var grid = zones.Grid;
        var gates = c.Get(Keys.Gates).Gates.Select(g => g.Position).ToList();
        var toWall = Proximity.ToRing(grid, c.Get(Keys.Wall).Ring, o.Distance + HalfBand + grid.CellSize);
        bool InBand(int k) => zones.Inside(k) && MathF.Abs(toWall[k] - o.Distance) <= HalfBand
            && gates.All(g => g.DistanceTo(grid.Center(k)) > GateGap);
        var (_, failed) = IsoStreets.Build(zones, links, RoadZone.Inside, RoadKind.Lane, InBand, o.MinLength, JoinReach,
            c.Cancellation);
        if (failed > 0)
            c.Notice($"{failed} 段墙根巷无合法通路，已跳过");
        c.Set(Keys.RoadLinks, links);
    }
}
