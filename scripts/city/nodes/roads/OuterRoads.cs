using System.Collections.Generic;

namespace PcgCitySandbox.City;

public sealed class OuterRoadsOptions
{
    [Param("启用")] public bool Enabled { get; set; } = true;
}

/// <summary>Adds a road outside the walls from every gate to its map-edge entry, where the terrain allows one.</summary>
public sealed class OuterRoads : CityNode<OuterRoadsOptions>
{
    public override string Group => "路网";
    public override string Title => "城外道路";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones, Keys.Gates];
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, OuterRoadsOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var site = zones.Site;
        var links = c.Get(Keys.RoadLinks).Copy();
        foreach (var gate in o.Enabled ? c.Get(Keys.Gates).Gates : new List<Gate>())
        {
            int from = site.CellAt(gate.Position);
            int entry = site.CellAt(gate.Entry);
            var path = RoadSearch.Find(zones, links, from, entry, RoadZone.Outside, RoadKind.Outer, c.Cancellation);
            if (path == null)
            {
                c.Notice($"{gate.Id} 城外道路无法到达地图边缘（可能被河流阻隔）");
                continue;
            }

            links.Mark(path, RoadKind.Outer);
            links.Anchor(entry, "entry");
        }

        c.Set(Keys.RoadLinks, links);
    }
}
