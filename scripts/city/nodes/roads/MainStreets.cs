using System;
using System.Collections.Generic;

namespace PcgCitySandbox.City;

/// <summary>Starts the road links: the cheapest path inside the walls from every gate to the plaza.</summary>
public sealed class MainStreets : CityNode<NoOptions>
{
    public override string Group => "路网";
    public override string Title => "主街";
    public override string Description =>
        "修主街：从每座城门到广场，在城内找代价最小的路。";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones, Keys.Core, Keys.Gates];
    public override IReadOnlyList<IKey> Writes => [Keys.RoadLinks];

    protected override void Run(NodeContext c, NoOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var site = zones.Site;
        var links = new RoadLinks { CoreCell = site.CellAt(c.Get(Keys.Core).Position) };
        links.Anchor(links.CoreCell, "core");
        foreach (var gate in c.Get(Keys.Gates).Gates)
        {
            int cell = site.CellAt(gate.Position);
            links.Anchor(cell, "gate");
            var path = RoadSearch.Find(zones, links, cell, links.CoreCell, RoadZone.Inside, RoadKind.Main, c.Cancellation)
                ?? throw new InvalidOperationException($"{gate.Id} 无法通往广场");
            links.Mark(path, RoadKind.Main);
        }

        c.Set(Keys.RoadLinks, links);
    }
}
