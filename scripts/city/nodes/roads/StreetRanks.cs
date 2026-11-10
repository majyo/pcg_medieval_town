using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

/// <summary>
/// Ranks the streets: the dead-end trees (<see cref="RoadTrees.DeadEnds"/>) lead to one patch of land only, so the
/// street links in them become lanes; streets on a loop or between two roads stay secondary streets. Main and outer
/// roads end the pruning; tracks are pruned with the rest (a street that only leads to a track is a lane) but keep
/// their class. No switch: the roads contract expects every secondary street to lead somewhere.
/// </summary>
public sealed class StreetRanks : CityNode<NoOptions>
{
    public override string Group => NodeGroups.Roads;
    public override string Title => "街巷分级";
    public override string Description =>
        "反复剪掉只连着一条路的端点（城门、市场、图边入口除外）：被剪掉的街巷只通向一片地，降为巷；连成环或连接两条路的是次街。";
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, NoOptions o)
    {
        var links = c.Get(Keys.RoadLinks).Copy();
        var all = links.Links.OrderBy(l => l.Key).ToList();
        var dead = RoadTrees.DeadEnds(all.Select(l => (l.Key.Item1, l.Key.Item2, l.Value is not (RoadKind.Main or RoadKind.Outer)))
            .ToList(), links.Anchors.ContainsKey);
        foreach (int i in dead.Where(i => all[i].Value == RoadKind.Street))
            links.Rank(all[i].Key, RoadKind.Lane);
        c.Set(Keys.RoadLinks, links);
    }
}
