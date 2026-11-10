using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class PlannedStreetsOptions
{
    [Param("规划期", 3, 5, 1,
        Hint = "从第几期起，成片的地按规划式修街：3 是三期城内填充和四期城外街区，4 只有四期，5 表示都不规划（全是有机街巷）")]
    public int FromPhase { get; set; } = 3;
    [Param("街距 m", 40, 120, 5,
        Hint = "平行街之间、第一条平行街与过境道路之间的距离：两排背靠背的地块加一条街。调小后街更密、地块更浅")]
    public float Spacing { get; set; } = 70;
    [Param("横街间距 m", 50, 250, 10,
        Hint = "沿过境道路每隔多远修一条横街，把平行街接回过境道路")]
    public float CrossSpacing { get; set; } = 100;
    [Param("最小片 ha", 0.1, 2, 0.05,
        Hint = "比它小的成片地不规划，留给有机的街巷网")]
    public float MinArea { get; set; } = 0.4f;
    [Param("最短 m", 10, 120, 5,
        Hint = "两端相距不到这么远的平行街或横街不修")]
    public float MinLength { get; set; } = 40;
}

/// <summary>
/// Planned streets on the land built late (the walled infill, the suburbs), laid out from the through route each
/// patch lies along: streets parallel to it every so many meters (two rows of plots back to back between them), and
/// cross streets at intervals from the farthest parallel street back to the route. Each street follows an iso-line
/// of the offset from, or the distance along, the route (<see cref="IsoStreets"/>), so it bends where the route does
/// and breaks where the ground is closed to roads. The older land keeps its organic streets; the organic street
/// nodes keep out of the planned areas.
/// </summary>
public sealed class PlannedStreets : CityNode<PlannedStreetsOptions>
{
    /// <summary>Half the band of cells a street may use, either side of its iso-line.</summary>
    public const float HalfBand = 2.5f;
    /// <summary>A patch takes its guide from the route with most of its cells this near.</summary>
    private const float GuideReach = 120;
    /// <summary>A cross street is not built where an existing road crosses the patch this near it.</summary>
    private const float CrossClearance = 20;

    public override string Group => NodeGroups.Roads;
    public override string Title => "规划街巷";
    public override string Description =>
        "三期、四期成片的地按规划式修街：沿最近的过境道路每隔一段距离修一条平行街，再隔一段修一条横街接回过境道路。老城（一、二期）仍是有机的街巷网，街巷网和城外巷道不进规划区。";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones, Keys.Growth, Keys.Routes];
    public override IReadOnlyList<IKey> Writes => [Keys.StreetPlan];
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, PlannedStreetsOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var growth = c.Get(Keys.Growth);
        var routes = c.Get(Keys.Routes).Routes;
        var grid = zones.Grid;
        var links = c.Get(Keys.RoadLinks).Copy();
        var before = links.Cells.ToHashSet();
        RoadZone ZoneOf(int k) => zones.Inside(k) ? RoadZone.Inside : RoadZone.Outside;
        bool Candidate(int k) => growth.Phase[k] >= o.FromPhase && zones.Allowed(k, ZoneOf(k)) && !zones.IsGate(k);

        var areas = new List<PlanArea>();
        int failed = 0, unguided = 0;
        var seen = new HashSet<int>();
        for (int seed = 0; seed < grid.Count; seed++)
        {
            if (seen.Contains(seed) || !Candidate(seed))
                continue;
            var patch = Patch(grid, seed, k => Candidate(k) && zones.Inside(k) == zones.Inside(seed));
            seen.UnionWith(patch);
            if (grid.Hectares(patch.Count) < o.MinArea)
                continue;

            var guide = routes.Select(r => r.Points)
                .Select(line => (line, near: patch.Count(k => Geometry.DistanceToLine(grid.Center(k), line) <= GuideReach)))
                .MaxBy(x => x.near);
            if (guide.line == null || guide.near == 0)
            {
                unguided++;
                continue;
            }

            var streets = Lay(patch, guide.line, ZoneOf(seed));
            if (streets.Count > 0)
                areas.Add(new PlanArea(patch.ToArray(), guide.line, streets));
        }

        if (failed > 0)
            c.Notice($"{failed} 条规划街巷无合法通路，已跳过");
        if (unguided > 0)
            c.Notice($"{unguided} 片规划期的地离过境道路太远，留给有机街巷");
        c.Set(Keys.RoadLinks, links);
        c.Set(Keys.StreetPlan, new StreetPlan(grid, areas));

        List<PlanStreet> Lay(List<int> patch, Vector2[] guide, RoadZone zone)
        {
            var inPatch = patch.ToHashSet();
            var frame = new Dictionary<int, (float Offset, float Along)>();
            (float Offset, float Along) At(int k)
            {
                if (!frame.TryGetValue(k, out var f))
                    frame[k] = f = PlanArea.Frame(guide, grid.Center(k));
                return f;
            }

            var streets = new List<PlanStreet>();
            float joinReach = o.Spacing;
            // Out in the suburbs a street keeps to the built land and the roads already there, as the suburb lanes do.
            Func<int, bool>? keep = zone == RoadZone.Outside ? k => growth.Built(k) || before.Contains(k) : null;
            foreach (int side in new[] { 1, -1 })
            {
                var mine = patch.Where(k => MathF.Sign(At(k).Offset) == side).ToList();
                if (mine.Count == 0)
                    continue;

                // Parallel streets, nearest the route first.
                float reach = mine.Max(k => MathF.Abs(At(k).Offset));
                float farthest = 0;
                for (float s = o.Spacing; s <= reach + HalfBand; s += o.Spacing)
                {
                    var band = mine.Where(k => MathF.Abs(MathF.Abs(At(k).Offset) - s) <= HalfBand);
                    var (built, missed) = IsoStreets.Build(zones, links, zone, RoadKind.Street, band, o.MinLength, joinReach,
                        c.Cancellation, keep);
                    failed += missed;
                    streets.AddRange(built.Select(cells => new PlanStreet(cells, false, side * s)));
                    if (built.Count > 0)
                        farthest = s;
                }

                if (farthest == 0)
                    continue;

                // Cross streets from the farthest parallel street back to the route, through the older land between.
                var roadsInPatch = before.Where(k => inPatch.Contains(k) || grid.AnyNear(k, 1, inPatch.Contains))
                    .Where(k => MathF.Abs(At(k).Offset) > CrossClearance).Select(k => At(k).Along).ToList();
                float first = mine.Min(k => At(k).Along), last = mine.Max(k => At(k).Along);
                for (float t = MathF.Ceiling(first / o.CrossSpacing) * o.CrossSpacing; t <= last; t += o.CrossSpacing)
                {
                    if (roadsInPatch.Any(along => MathF.Abs(along - t) < CrossClearance))
                        continue;
                    var band = Corridor(grid, mine, k => (inPatch.Contains(k) || growth.Built(k) || before.Contains(k))
                            && (keep == null || keep(k)),
                        k => MathF.Sign(At(k).Offset) == side && MathF.Abs(At(k).Offset) <= farthest + HalfBand
                            && MathF.Abs(At(k).Along - t) <= HalfBand);
                    var (built, missed) = IsoStreets.Build(zones, links, zone, RoadKind.Street, band, o.MinLength, joinReach,
                        c.Cancellation, keep);
                    failed += missed;
                    streets.AddRange(built.Select(cells => new PlanStreet(cells, true, t)));
                }
            }

            return streets;
        }
    }

    /// <summary>The cells of one patch: candidates joined through their eight neighbors.</summary>
    private static List<int> Patch(SiteGrid grid, int seed, Func<int, bool> candidate)
    {
        var patch = new List<int> { seed };
        var seen = new HashSet<int> { seed };
        for (int i = 0; i < patch.Count; i++)
        {
            grid.ForAround(patch[i], j =>
            {
                if (seen.Add(j) && candidate(j))
                    patch.Add(j);
            });
        }

        return patch;
    }

    /// <summary>
    /// The cells passing <paramref name="onLine"/> within the bounds of the patch grown by the farthest offset, that
    /// are land a street may cross (<paramref name="open"/>): the patch, older built land, existing roads.
    /// </summary>
    private static IEnumerable<int> Corridor(SiteGrid grid, List<int> patch, Func<int, bool> open, Func<int, bool> onLine)
    {
        int x0 = patch.Min(k => k % grid.Nx), x1 = patch.Max(k => k % grid.Nx);
        int z0 = patch.Min(k => k / grid.Nx), z1 = patch.Max(k => k / grid.Nx);
        int pad = (int)MathF.Ceiling(GuideReach / grid.CellSize) + 1;
        for (int z = Math.Max(0, z0 - pad); z <= Math.Min(grid.Nz - 1, z1 + pad); z++)
        {
            for (int x = Math.Max(0, x0 - pad); x <= Math.Min(grid.Nx - 1, x1 + pad); x++)
            {
                int k = z * grid.Nx + x;
                if (open(k) && onLine(k))
                    yield return k;
            }
        }
    }
}
