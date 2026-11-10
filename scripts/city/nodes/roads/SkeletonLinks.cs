using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class SkeletonLinksOptions
{
    [Param("城外道路",
        Hint = "关掉后只保留过境道路在城内的部分（主街），城外道路不画、不铺装")]
    public bool Outside { get; set; } = true;
}

/// <summary>
/// Starts the road links from the through routes: every route link is copied, as a main street where both cells
/// lie inside the wall and as an outer road elsewhere, all the way to the map edge and across the crossings. The
/// market (its route cell nearest the center), the gates and the map entries are anchors.
/// </summary>
public sealed class SkeletonLinks : CityNode<SkeletonLinksOptions>
{
    public override string Group => NodeGroups.Roads;
    public override string Title => "骨架道路";
    public override string Description =>
        "把过境道路抄成道路连线：城内的段是主街，城外的段是城外道路，一直到图边，过河处走渡口；市场、城门、图边入口是锚点。";
    public override IReadOnlyList<IKey> Reads => [Keys.Routes, Keys.Wall, Keys.Gates, Keys.Plaza];
    public override IReadOnlyList<IKey> Writes => [Keys.RoadLinks];

    protected override void Run(NodeContext c, SkeletonLinksOptions o)
    {
        var network = c.Get(Keys.Routes);
        var wall = c.Get(Keys.Wall);
        var plaza = c.Get(Keys.Plaza);
        var grid = network.Grid;
        var inside = wall.Inside(grid);

        var links = new RoadLinks
        {
            CoreCell = network.Cells.MinBy(k => (plaza.Area.Contains(grid.Center(k)) ? 0 : 1, grid.Center(k).DistanceTo(plaza.Center), k)),
        };
        links.Anchor(links.CoreCell, "core");
        foreach (var (a, b) in network.Links.OrderBy(l => l))
        {
            bool main = inside[a] && inside[b];
            if (main || o.Outside)
                links.Mark(new[] { a, b }, main ? RoadKind.Main : RoadKind.Outer);
        }

        foreach (int gate in c.Get(Keys.Gates).Cells(grid))
            links.Anchor(gate, "gate");
        if (o.Outside)
        {
            foreach (var entry in network.Entries)
                links.Anchor(entry.Cell, "entry");
        }

        c.Set(Keys.RoadLinks, links);
    }
}
