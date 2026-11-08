using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class RoadZoningOptions
{
    [Param("道路坡度上限 °", 3, 25, 0.5)] public float MaxSlope { get; set; } = 12;
}

/// <summary>Marks which site cells roads may use: dry, not too steep, and on one side of the wall or a gate.</summary>
public sealed class RoadZoning : CityNode<RoadZoningOptions>
{
    public override string Group => "路网";
    public override string Title => "通行区";
    public override IReadOnlyList<IKey> Reads => [Keys.Site, Keys.Wall, Keys.Gates];
    public override IReadOnlyList<IKey> Writes => [Keys.RoadZones];

    protected override void Run(NodeContext c, RoadZoningOptions o)
    {
        var site = c.Get(Keys.Site);
        var wall = c.Get(Keys.Wall);
        var gates = c.Get(Keys.Gates).Gates;
        var inside = new bool[site.Count];
        var nearWall = new bool[site.Count];
        var passable = new bool[site.Count];
        var wallBounds = wall.Interior.Bounds.Grow(site.CellSize * 2);
        for (int k = 0; k < site.Count; k++)
        {
            var p = site.Center(k);
            passable[k] = site.Block[k] != SiteBlock.Water && site.Slope[k] <= o.MaxSlope;
            if (!wallBounds.HasPoint(p))
                continue;
            inside[k] = wall.Interior.Contains(p);
            nearWall[k] = Geometry.DistanceToRing(p, wall.Ring) < site.CellSize * 0.5f;
        }

        c.Set(Keys.RoadZones, new RoadZones(site, inside, nearWall, passable, gates.Select(g => site.CellAt(g.Position))));
    }
}
