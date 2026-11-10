using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class WorkshopLanesOptions
{
    [Param("便道间距 m", 20, 120, 5,
        Hint = "作坊片内撒点的间距。作坊院子约 20–30 m 宽，40 m 时大的作坊片里有两三条便道；调小后更密")]
    public float Spacing { get; set; } = 40;
    [Param("种子", 0, 99999,
        Hint = "只影响片内撒点位置的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;
}

/// <summary>
/// Dirt tracks to the trades (<see cref="RoadKind.Track"/>). Each patch of one trade (wet trades, fire trades, a
/// mill) gets a track from its middle out to the nearest road it can reach (a road across the river is out of reach),
/// and on through the patch (or along its edge) from the middle to the cell farthest from that road, so plots can face
/// it; then tracks woven inside the patch (<see cref="StreetWeave"/>, no loops) from the roads that touch it.
/// </summary>
public sealed class WorkshopLanes : CityNode<WorkshopLanesOptions>
{
    public override string Group => "路网";
    public override string Title => "作坊便道";
    public override string Description =>
        "每片作坊（湿作坊、火作坊、水磨）从片中间修一条便道到走得到的最近道路，片内再从中间通到离那条路最远的一头；片大时再在片内按间距撒点连几条。都是土路。";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones, Keys.LandUse];
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, WorkshopLanesOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var land = c.Get(Keys.LandUse);
        var grid = zones.Grid;
        var links = c.Get(Keys.RoadLinks).Copy();
        var rng = new Random(o.Seed);
        bool Open(int k) => zones.Allowed(k, RoadZone.Outside);
        int failed = 0;
        foreach (var patch in land.Patches(LandUses.Trades))
        {
            c.Cancellation.ThrowIfCancellationRequested();
            var inPatch = patch.ToHashSet();
            var open = patch.Where(Open).ToList();
            if (open.Count == 0)
            {
                failed++;
                continue;
            }

            bool Touches(int k)
            {
                int x = k % grid.Nx, z = k / grid.Nx;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (grid.InGrid(x + dx, z + dz) && inPatch.Contains(grid.Index(x + dx, z + dz)))
                            return true;
                    }

                return false;
            }

            // Out from the middle to the nearest reachable road, then on across the patch away from it.
            var center = open.Aggregate(Godot.Vector2.Zero, (sum, k) => sum + grid.Center(k)) / open.Count;
            int middle = open.MinBy(k => (grid.Center(k).DistanceSquaredTo(center), k));
            var roadCells = links.Cells.ToHashSet();
            var road = RoadSearch.FindNearest(zones, links, middle, roadCells.Contains, RoadZone.Outside, RoadKind.Track,
                c.Cancellation);
            if (road == null)
            {
                failed++;
                continue;
            }

            links.Mark(road, RoadKind.Track);
            var entry = grid.Center(road[^1]);
            int far = open.MinBy(k => (-grid.Center(k).DistanceSquaredTo(entry), k));
            // A mill's patch is a thin strip of steep bank; the track may run along its land-side edge.
            var across = RoadSearch.Find(zones, links, far, middle, RoadZone.Outside, RoadKind.Track, c.Cancellation,
                Touches);
            if (across != null)
                links.Mark(across, RoadKind.Track);

            roadCells = links.Cells.ToHashSet();
            failed += StreetWeave.Weave(zones, links, RoadZone.Outside, k => Open(k) && Touches(k), inPatch.Contains,
                k => inPatch.Contains(k) || roadCells.Contains(k), o.Spacing, 0, rng, c.Cancellation, RoadKind.Track);
        }

        if (failed > 0)
            c.Notice($"{failed} 条作坊便道无合法通路，已跳过");
        c.Set(Keys.RoadLinks, links);
    }
}
