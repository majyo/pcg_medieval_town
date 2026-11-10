using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// A way to every landmark that has no road beside it (the town hall stands in the market and needs none): from the
/// cell by its plot nearest the market (in town) or nearest a road (out in the country), the cheapest road to the
/// nearest road there is; a lane in town, a track outside.
/// </summary>
public sealed class LandmarkAccess : CityNode<NoOptions>
{
    /// <summary>A road this near a plot already serves it.</summary>
    public const float Reach = 8;

    public override string Group => NodeGroups.Roads;
    public override string Title => "地标通路";
    public override string Description =>
        "旁边没有路的地标（市政厅在市场里，不算），从用地边上朝着市场（城内）或最近道路（城外）的一格修路，接到最近的道路：城内是街巷，城外是便道。";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones, Keys.Landmarks, Keys.Plaza];
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, NoOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var plaza = c.Get(Keys.Plaza);
        var grid = zones.Grid;
        var links = c.Get(Keys.RoadLinks).Copy();
        int failed = 0;
        foreach (var landmark in c.Get(Keys.Landmarks).Landmarks.Where(l => l.Kind != LandmarkKind.TownHall))
        {
            var roads = links.Cells.ToHashSet();
            var near = grid.CellsIn(landmark.Plot.Offset(Reach));
            if (near.Any(roads.Contains))
                continue;

            var zone = landmark.InTown ? RoadZone.Inside : RoadZone.Outside;
            var kind = landmark.InTown ? RoadKind.Street : RoadKind.Track;
            var doors = near.Where(k => zones.Allowed(k, zone)).ToList();
            if (doors.Count == 0)
            {
                failed++;
                continue;
            }

            float Score(int k) => landmark.InTown
                ? grid.Center(k).DistanceSquaredTo(plaza.Center)
                : roads.Min(r => grid.Center(k).DistanceSquaredTo(grid.Center(r)));
            int door = doors.MinBy(k => (Score(k), k));
            var path = RoadSearch.FindNearest(zones, links, door, roads.Contains, zone, kind, c.Cancellation);
            if (path == null)
            {
                failed++;
                continue;
            }

            links.Mark(path, kind);
        }

        if (failed > 0)
            c.Notice($"{failed} 处地标修不出通路");
        c.Set(Keys.RoadLinks, links);
    }
}
