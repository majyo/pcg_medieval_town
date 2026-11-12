using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>A rule every value of <see cref="Key"/> must satisfy, whichever node wrote it.</summary>
public sealed record Invariant(IKey Key, Func<IKeyReader, IEnumerable<string>> Check);

/// <summary>
/// The contract of each key: its type plus these invariants. A new node writing a key is held to the same rules
/// as the one it replaces. Checks may read other keys the value relates to.
/// </summary>
public static class Contracts
{
    private const double Tolerance = 0.5;

    // The upper ends of node sliders, so a rule holds for any setting.
    /// <summary>The widest wall setback a growth phase can be given.</summary>
    private const float MaxWallSetback = 30;

    /// <summary>The widest wall strip the town quarters can be given.</summary>
    private const float MaxStrip = 60;

    /// <summary>The farthest the leper house and the gallows may be set from a route.</summary>
    private const float MaxLeperReach = 40, MaxCountryReach = 200;

    /// <summary>
    /// The longest a wall may be, as a multiple of the circumference of a circle of its area. Walls measured on eight
    /// seeds run 1.2 to 1.55; one wrapped around every road arm would pass 2.
    /// </summary>
    private const float MaxWinding = 1.7f;

    /// <summary>The most the graded ground may rise or fall between points a meter apart, away from walls and the water.</summary>
    private const float MaxGroundStep = 0.6f;

    /// <summary>Ground this near a platform's edge is too narrow to ease a step and is held up by its retaining wall.</summary>
    private const float WallGap = 2.5f;

    /// <summary>Ground this near the water may fall steeply to it: a bridge's approach and its wing walls.</summary>
    private const float BankReach = 8;

    /// <summary>The share of graded points whose ground may break where the plan leaves no room to join it.</summary>
    private const float MaxBreakShare = 0.0001f;

    /// <summary>Within this distance of a bridge's end a road is on its approach and may stand off the ground.</summary>
    private const float ApproachReach = 100;

    /// <summary>
    /// How much the graded ground may differ between a house's corners, and the share of houses that may exceed it
    /// (a house deeper than its plot's platform, or set back from the street, reaches onto the yard behind).
    /// </summary>
    private const float MaxFloorStep = 0.5f, MaxUnevenShare = 0.1f;

    /// <summary>
    /// Leveling a road across its width on a side slope cuts one edge and fills the other on top of the center line's
    /// own cut or fill; so does a tilted market at its edges. The most that may add, in meters.
    /// </summary>
    private const float MaxCrossFall = 2;

    /// <summary>The largest share of the roads' length that may stay steeper than its class allows.</summary>
    private const float MaxSteepShare = 0.25f;

    /// <summary>The steepest the market plane can be set, as rise over run.</summary>
    private const float MaxMarketGrade = 0.08f;

    public static readonly IReadOnlyList<Invariant> All = new Invariant[]
    {
        new(Keys.Grid, GridRules),
        new(Keys.Height, r => FieldRules(r, Keys.Height).Concat(HeightRules(r))),
        new(Keys.Slope, r => FieldRules(r, Keys.Slope).Concat(Range(r, Keys.Slope, 0, 45))),
        new(Keys.Aspect, r => FieldRules(r, Keys.Aspect).Concat(Range(r, Keys.Aspect, 0, 360, maxExclusive: true))),
        new(Keys.Shore, r => FieldRules(r, Keys.Shore).Concat(ShoreRules(r))),
        new(Keys.AboveWater, r => FieldRules(r, Keys.AboveWater).Concat(AboveWaterRules(r))),
        new(Keys.Relief, r => FieldRules(r, Keys.Relief).Concat(ReliefRules(r))),
        new(Keys.Forest, r => FieldRules(r, Keys.Forest).Concat(Range(r, Keys.Forest, 0, 1)).Concat(ForestRules(r))),
        new(Keys.Buildable, BuildableRules),
        new(Keys.Habitability, r => FieldRules(r, Keys.Habitability).Concat(Range(r, Keys.Habitability, 0, 1))
            .Concat(HabitabilityRules(r))),
        new(Keys.TravelCost, r => FieldRules(r, Keys.TravelCost).Concat(TravelCostRules(r))),
        new(Keys.Crossings, CrossingRules),
        new(Keys.Routes, RouteRules),
        new(Keys.Origin, OriginRules),
        new(Keys.Growth, GrowthRules),
        new(Keys.Plaza, PlazaRules),
        new(Keys.Landmarks, LandmarkRules),
        new(Keys.Wall, WallRules),
        new(Keys.Gates, GateRules),
        new(Keys.StreetPlan, PlanRules),
        new(Keys.Squares, SquareRules),
        new(Keys.Roads, RoadRules),
        new(Keys.Blocks, BlockRules),
        new(Keys.Parcels, ParcelRules),
        new(Keys.Bridges, BridgeRules),
        new(Keys.Grading, GradingRules),
        new(Keys.Buildings, BuildingRules),
        new(Keys.Yards, YardRules),
        new(Keys.Fixtures, FixtureRules),
        new(Keys.Reach, r => FieldRules(r, Keys.Reach).Concat(ReachRules(r))),
        new(Keys.LandUse, LandUseRules),
    };

    private static IEnumerable<string> GridRules(IKeyReader r)
    {
        var grid = r.Get(Keys.Grid)!;
        float half = r.Terrain.Half;
        var end = grid.Origin + grid.Size;
        if (grid.Origin.X > -half || grid.Origin.Y > -half || end.X < half || end.Y < half)
            yield return "采样网格没有覆盖整张地图";
        if (-grid.Origin.X - half >= grid.CellSize || -grid.Origin.Y - half >= grid.CellSize ||
            end.X - half >= grid.CellSize || end.Y - half >= grid.CellSize)
            yield return "采样网格超出地图一格以上";
        if (!(grid.Origin + grid.Size / 2).IsZeroApprox())
            yield return "采样网格不以原点为中心";
    }

    /// <summary>Rules every scalar field obeys: on the shared grid, one finite value per cell, statistics consistent.</summary>
    private static IEnumerable<string> FieldRules(IKeyReader r, Key<ScalarField> key)
    {
        var field = r.Get(key)!;
        if (!ReferenceEquals(field.Grid, r.Get(Keys.Grid)))
            yield return "没有使用共用的采样网格";
        if (field.Values.Length != field.Grid.Count)
            yield return $"数值个数 {field.Values.Length} 与格数 {field.Grid.Count} 不符";
        if (!field.Values.All(float.IsFinite))
            yield return "存在非有限值";
        var valid = field.Values.Where(v => !field.IsNoData(v)).DefaultIfEmpty(0).ToArray();
        if (valid.Min() != field.Min || valid.Max() != field.Max)
            yield return "统计的最小/最大值与数据不符";
    }

    /// <summary>
    /// Every value (no-data cells excepted) lies in [<paramref name="min"/>, <paramref name="max"/>], or in
    /// [<paramref name="min"/>, <paramref name="max"/>) for angles that wrap.
    /// </summary>
    private static IEnumerable<string> Range(IKeyReader r, Key<ScalarField> key, float min, float max, bool maxExclusive = false)
    {
        var field = r.Get(key)!;
        if (field.Values.Any(v => !field.IsNoData(v) && (v < min || v > max || maxExclusive && v == max)))
            yield return $"取值超出 [{min}, {max}{(maxExclusive ? ")" : "]")}：{field.Min}–{field.Max}";
    }

    /// <summary>
    /// The distance counts from the channel edge (the river's half-width). Everything inside it is the terrain's
    /// water; the real waterline lies a few meters further out, where the bank rises past the water level.
    /// </summary>
    private static IEnumerable<string> ShoreRules(IKeyReader r)
    {
        const float waterlineReach = 8;
        var shore = r.Get(Keys.Shore)!;
        int wetLand = 0, farWater = 0;
        for (int k = 0; k < shore.Grid.Count; k++)
        {
            bool water = r.Terrain.Sample(shore.Grid.Center(k)).IsWater;
            if (!water && shore.Values[k] < 0)
                wetLand++;
            else if (water && shore.Values[k] >= waterlineReach)
                farWater++;
        }

        if (wetLand > 0)
            yield return $"{wetLand} 个陆地格的离水距离为负";
        if (farWater > 0)
            yield return $"{farWater} 个水面格离河道边 {waterlineReach} m 以上";
    }

    /// <summary>Well inside the water the ground is below the water level; well away from it, above.</summary>
    private static IEnumerable<string> AboveWaterRules(IKeyReader r)
    {
        var shore = r.Get(Keys.Shore)!.Values;
        var above = r.Get(Keys.AboveWater)!.Values;
        int high = 0, low = 0;
        for (int k = 0; k < above.Length; k++)
        {
            if (shore[k] < -2 && above[k] > 0.5f)
                high++;
            else if (shore[k] > 30 && above[k] < 0)
                low++;
        }

        if (high > 0)
            yield return $"{high} 个水面格高出水位 0.5 m 以上";
        if (low > 0)
            yield return $"{low} 个离水 30 m 以外的格低于水位";
    }

    /// <summary>Relative to its surroundings, the map averages out near zero, and the river channel lies low.</summary>
    private static IEnumerable<string> ReliefRules(IKeyReader r)
    {
        var relief = r.Get(Keys.Relief)!.Values;
        var shore = r.Get(Keys.Shore)!.Values;
        double mean = relief.Average(v => (double)v);
        if (Math.Abs(mean) >= 0.5)
            yield return $"全图平均地形位置 {mean:0.00} m，应接近 0";
        int deep = 0, low = 0;
        for (int k = 0; k < relief.Length; k++)
        {
            if (shore[k] >= -10)
                continue;
            deep++;
            if (relief[k] < 0)
                low++;
        }

        if (deep > 0 && low < deep * 0.9)
            yield return $"河道深处只有 {low}/{deep} 格低于周围";
    }

    /// <summary>No forest stands in the water.</summary>
    private static IEnumerable<string> ForestRules(IKeyReader r)
    {
        var forest = r.Get(Keys.Forest)!.Values;
        var shore = r.Get(Keys.Shore)!.Values;
        int wet = 0;
        for (int k = 0; k < forest.Length; k++)
        {
            if (shore[k] < 0 && forest[k] > 0)
                wet++;
        }

        if (wet > 0)
            yield return $"{wet} 个水面格有林地";
    }

    /// <summary>Averaging inside a cell only filters meter-scale relief: each value stays near the ground at the cell center.</summary>
    private static IEnumerable<string> HeightRules(IKeyReader r)
    {
        var height = r.Get(Keys.Height)!;
        for (int k = 0; k < height.Grid.Count; k++)
        {
            var p = height.Grid.Center(k);
            float ground = r.Terrain.HeightAt(p);
            if (MathF.Abs(height.Values[k] - ground) >= 1)
            {
                yield return $"{p} 处高程 {height.Values[k]:0.00} m 与地面 {ground:0.00} m 相差 1 m 以上";
                yield break;
            }
        }
    }

    /// <summary>
    /// On the shared grid with the known reasons; a sensible share is buildable; each reason is a threshold applied in
    /// order. Water is exactly the cells in the water. A cell is ruled out as flooding only if it lies lower above the
    /// water than every land cell that got past that test, and as too steep only if it is steeper than every
    /// buildable cell. These hold for any thresholds and fail for anything that is not a threshold rule.
    /// </summary>
    private static IEnumerable<string> BuildableRules(IKeyReader r)
    {
        var buildable = r.Get(Keys.Buildable)!;
        if (!ReferenceEquals(buildable.Grid, r.Get(Keys.Grid)))
            yield return "没有使用共用的采样网格";
        if (buildable.Names.Count != SiteBlocks.Names.Count)
            yield return $"类别数 {buildable.Names.Count} 与 {SiteBlocks.Names.Count} 种原因不符";
        float open = buildable.Fraction((int)SiteBlock.None);
        if (open is <= 0.05f or >= 0.95f)
            yield return $"可建设比例异常 {open:P0}";

        var shore = r.Get(Keys.Shore)!.Values;
        var above = r.Get(Keys.AboveWater)!.Values;
        var slope = r.Get(Keys.Slope)!.Values;
        int wrongWater = 0;
        float floodTop = float.NegativeInfinity, dryBottom = float.PositiveInfinity;
        float flatTop = float.NegativeInfinity, steepBottom = float.PositiveInfinity;
        for (int k = 0; k < shore.Length; k++)
        {
            var block = buildable.Block(k);
            if (block == SiteBlock.Water != shore[k] < 0)
                wrongWater++;
            if (block == SiteBlock.Flood)
                floodTop = Math.Max(floodTop, above[k]);
            else if (block != SiteBlock.Water)
                dryBottom = Math.Min(dryBottom, above[k]);
            if (block == SiteBlock.Slope)
                steepBottom = Math.Min(steepBottom, slope[k]);
            else if (block == SiteBlock.None)
                flatTop = Math.Max(flatTop, slope[k]);
        }

        if (wrongWater > 0)
            yield return $"{wrongWater} 格的水域判定与离水距离的正负不一致";
        if (floodTop >= dryBottom)
            yield return $"易淹格最高高出水面 {floodTop:0.00} m，不低于其余陆地格的最低值 {dryBottom:0.00} m";
        if (flatTop >= steepBottom)
            yield return $"可建设格最大坡度 {flatTop:0.0}°，不小于坡度过大格的最小值 {steepBottom:0.0}°";
    }

    /// <summary>Only buildable land scores, and some of it scores well.</summary>
    private static IEnumerable<string> HabitabilityRules(IKeyReader r)
    {
        var buildable = r.Get(Keys.Buildable)!;
        var score = r.Get(Keys.Habitability)!;
        int scored = 0;
        for (int k = 0; k < score.Values.Length; k++)
        {
            if (!buildable.Buildable(k) && score.Values[k] != 0)
                scored++;
        }

        if (scored > 0)
            yield return $"{scored} 个不可建设格的宜居度不为 0";
        if (score.Max <= 0.5f)
            yield return $"最高宜居度只有 {score.Max:0.00}";
    }

    /// <summary>Crossing a cell never costs less than level open ground; the water cannot be crossed.</summary>
    private static IEnumerable<string> TravelCostRules(IKeyReader r)
    {
        var cost = r.Get(Keys.TravelCost)!;
        var shore = r.Get(Keys.Shore)!.Values;
        int cheap = 0, wet = 0;
        for (int k = 0; k < shore.Length; k++)
        {
            float v = cost.Values[k];
            if (!cost.IsNoData(v) && v < 1)
                cheap++;
            if (shore[k] < 0 && !cost.IsNoData(v))
                wet++;
        }

        if (cheap > 0)
            yield return $"{cheap} 格的通行代价小于 1";
        if (wet > 0)
            yield return $"{wet} 个水面格可以通行";
    }

    /// <summary>
    /// At least one crossing. Each runs from one bridgehead cell to the other through side-neighboring cells, its
    /// center is in the water, and both bridgeheads are dry, passable land.
    /// </summary>
    private static IEnumerable<string> CrossingRules(IKeyReader r)
    {
        var crossings = r.Get(Keys.Crossings)!.Crossings;
        var grid = r.Get(Keys.Grid)!;
        var shore = r.Get(Keys.Shore)!;
        var cost = r.Get(Keys.TravelCost)!;
        if (crossings.Count == 0)
            yield return "没有渡口";
        foreach (var x in crossings)
        {
            var cells = x.Cells;
            if (cells.Length < 2 || cells[0] != grid.CellAt(x.BankA) || cells[^1] != grid.CellAt(x.BankB))
                yield return $"{x.Id} 的格子不是从一个桥头到另一个桥头";
            else if (cells.Zip(cells.Skip(1)).Any(p => grid.Chebyshev(p.First, p.Second) != 1 || Diagonal(grid, p.First, p.Second)))
                yield return $"{x.Id} 的格子不是逐格相邻（只走上下左右）";
            if (shore.Nearest(x.Center) >= 0)
                yield return $"{x.Id} 的中点不在水面上";
            foreach (var head in new[] { x.BankA, x.BankB })
            {
                int k = grid.CellAt(head);
                if (k < 0 || shore.Values[k] < 0 || cost.IsNoData(cost.Values[k]))
                    yield return $"{x.Id} 的桥头 {head} 不是可通行的陆地";
            }
        }
    }

    /// <summary>
    /// Entries lie on the map edge; each route runs from an entry to another entry or onto an earlier route, through
    /// 8-neighboring cells that are passable or the water cells of a crossing it names; together the routes connect
    /// every entry.
    /// </summary>
    private static IEnumerable<string> RouteRules(IKeyReader r)
    {
        var network = r.Get(Keys.Routes)!;
        var grid = network.Grid;
        var cost = r.Get(Keys.TravelCost)!;
        var crossings = r.Get(Keys.Crossings)!.Crossings.ToDictionary(x => x.Id);
        float half = r.Terrain.Half;
        foreach (var entry in network.Entries)
        {
            var p = entry.Position;
            if (half - Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) > 10)
                yield return $"{entry.Id} 离图边超过 10 m";
        }

        var cells = network.Entries.ToDictionary(e => e.Id, e => e.Cell);
        var earlier = new Dictionary<string, Route>();
        foreach (var route in network.Routes)
        {
            var path = route.Cells;
            bool ends = path.Length >= 2 && cells.TryGetValue(route.From, out int from) && path[0] == from &&
                (cells.TryGetValue(route.To, out int to) ? path[^1] == to
                    : earlier.TryGetValue(route.To, out var joined) && joined.Cells.Contains(path[^1]));
            if (!ends)
                yield return $"{route.Id} 不是从入口 {route.From} 到入口或更早的道路 {route.To}";
            earlier[route.Id] = route;
            if (path.Zip(path.Skip(1)).Any(p => grid.Chebyshev(p.First, p.Second) != 1))
                yield return $"{route.Id} 的格子不相邻";
            var water = route.Crossings.Where(crossings.ContainsKey).SelectMany(id => crossings[id].Cells).ToHashSet();
            int blocked = path.Count(k => cost.IsNoData(cost.Values[k]) && !water.Contains(k));
            if (blocked > 0)
                yield return $"{route.Id} 有 {blocked} 格既不可通行也不属于它经过的渡口";
        }

        // Every entry and route is reachable from the first entry through the From/To links.
        var seen = new HashSet<string>();
        var queue = new Queue<string>(network.Entries.Take(1).Select(e => e.Id));
        while (queue.TryDequeue(out var id))
        {
            if (!seen.Add(id))
                continue;
            foreach (var route in network.Routes.Where(x => x.From == id || x.To == id || x.Id == id))
            {
                queue.Enqueue(route.Id);
                queue.Enqueue(route.From);
                queue.Enqueue(route.To);
            }
        }

        int linked = network.Entries.Count(e => seen.Contains(e.Id));
        if (linked != network.Entries.Count)
            yield return $"道路只连通了 {linked}/{network.Entries.Count} 个入口";
    }

    /// <summary>The origin is a buildable cell on a route.</summary>
    private static IEnumerable<string> OriginRules(IKeyReader r)
    {
        var origin = r.Get(Keys.Origin)!;
        var network = r.Get(Keys.Routes)!;
        if (!r.Get(Keys.Buildable)!.Buildable(origin.Cell))
            yield return "起源点不在可建设格上";
        if (!network.Cells.Contains(origin.Cell) || network.Grid.CellAt(origin.Position) != origin.Cell)
            yield return "起源点不在过境道路上";
    }

    /// <summary>No negative distances; the inside and the gates are 0; every built cell can be walked to.</summary>
    private static IEnumerable<string> ReachRules(IKeyReader r)
    {
        var reach = r.Get(Keys.Reach)!;
        var grid = reach.Grid;
        var wall = r.Get(Keys.Wall)!;
        var growth = r.Get(Keys.Growth)!;
        if (reach.Values.Any(v => !reach.IsNoData(v) && v < 0))
            yield return "有负的路程";
        if (grid.CellsIn(wall.Interior).Any(k => reach.Values[k] != 0))
            yield return "城内的路程不是 0";
        if (r.Get(Keys.Gates)!.Gates.Any(g => reach.Values[grid.CellAt(g.Position)] != 0))
            yield return "城门格的路程不是 0";
        int lost = growth.BuiltCells.Count(k => reach.IsNoData(reach.Values[k]));
        if (lost > 0)
            yield return $"有 {lost} 个建成格从城门走不到";
    }

    /// <summary>
    /// Every cell has a use; water, the built cells and the public cells get exactly theirs; each side of the wall
    /// only its own uses; and each workshop and farm use stands where its node's rule puts it (for any setting).
    /// </summary>
    private static IEnumerable<string> LandUseRules(IKeyReader r)
    {
        var land = r.Get(Keys.LandUse)!;
        var grid = land.Grid;
        if (!ReferenceEquals(grid, r.Get(Keys.Grid)))
            yield return "没有使用共用的采样网格";
        var shore = r.Get(Keys.Shore)!;
        var growth = r.Get(Keys.Growth)!;
        var wall = r.Get(Keys.Wall)!;
        var buildable = r.Get(Keys.Buildable)!;
        var slope = r.Get(Keys.Slope)!;
        var travel = r.Get(Keys.TravelCost)!;
        var inside = wall.Inside(grid);
        var toWall = Proximity.ToRing(grid, wall.Ring, 600 + grid.CellSize);
        var level = Workshops.WaterLevel(r.Get(Keys.Height)!, r.Get(Keys.AboveWater)!);
        float townLevel = Workshops.TownWaterLevel(shore, level, toWall);

        var publicCells = grid.CellsIn(r.Get(Keys.Plaza)!.Area).Concat(r.Get(Keys.Landmarks)!.Cells()).ToHashSet();
        var wrong = new Dictionary<string, int>();
        void Count(string rule) => wrong[rule] = wrong.GetValueOrDefault(rule) + 1;
        for (int k = 0; k < grid.Count; k++)
        {
            var use = land.Use(k);
            if (use == LandUse.Unassigned)
                Count("有格未分配");
            if (use == LandUse.Water != shore.Values[k] < 0)
                Count("水域与离水 < 0 的格不一致");
            if (growth.Built(k) && !LandUses.Built.Contains(use))
                Count("建成格不是城镇四类（公共、核心、居住、城外街区）之一");
            if (use is LandUse.Core or LandUse.Housing or LandUse.Suburb && !growth.Built(k))
                Count("市场核心、居住或城外街区不在建成格上");
            if (use == LandUse.Public != publicCells.Contains(k))
                Count("公共用地与市场、地标用地的格不一致");
            if (inside[k] && use is not (LandUse.Water or LandUse.WallStrip or LandUse.Public) && !LandUses.Inside.Contains(use))
                Count("城内有城外的用途");
            if (!inside[k] && LandUses.Inside.Contains(use))
                Count("城外有城内的用途");
            if (use == LandUse.WallStrip && !(toWall[k] <= MaxStrip + grid.CellSize))
                Count("城墙空地离墙太远");
            if (buildable.Block(k) == SiteBlock.Forest && use > LandUse.Mill && use != LandUse.Woodland)
                Count("城外的森林格不是林地");
            bool ok = use switch
            {
                LandUse.WetWorks => shore.Values[k] is >= 0 and <= 60 && level[k] < townLevel,
                LandUse.FireWorks => !grid.AnyNear(k, 1, growth.Built),
                LandUse.Mill => shore.Values[k] is >= 0 and <= 12,
                LandUse.Woodland => buildable.Block(k) == SiteBlock.Forest,
                LandUse.Meadow => Countryside.IsMeadow(buildable, shore, r.Get(Keys.AboveWater)!, k),
                LandUse.Garden => buildable.Buildable(k),
                LandUse.Vineyard => Countryside.IsVineSlope(slope, r.Get(Keys.Aspect)!, k),
                LandUse.Arable => slope.Values[k] <= 6,
                LandUse.Pasture => !travel.IsNoData(travel.Values[k]),
                LandUse.Waste => travel.IsNoData(travel.Values[k]),
                _ => true,
            };
            if (!ok)
                Count($"{use.Label()}不满足它的条件");
        }

        foreach (var (rule, n) in wrong)
            yield return $"{rule}（{n} 格）";
    }

    /// <summary>
    /// Phases are numbered from 1 with the cell counts they record; every built cell is buildable; the infill stays
    /// inside the wall and the suburbs off it (and outside only once the inside is full); and after each phase, every
    /// group of built cells joined by side neighbors reaches a route cell (or touches one).
    /// </summary>
    private static IEnumerable<string> GrowthRules(IKeyReader r)
    {
        var growth = r.Get(Keys.Growth)!;
        var grid = growth.Grid;
        var buildable = r.Get(Keys.Buildable)!;
        var network = r.Get(Keys.Routes)!;
        if (!ReferenceEquals(grid, r.Get(Keys.Grid)))
            yield return "没有使用共用的采样网格";
        if (growth.Latest == 0)
            yield return "还没有任何一期";
        var counts = new int[256];
        foreach (byte p in growth.Phase)
            counts[p]++;
        if (counts.Skip(growth.Latest + 1).Any(n => n > 0))
            yield return "有格的期数超出记录";
        for (int n = 1; n <= growth.Latest; n++)
        {
            if (counts[n] != growth.Phases[n - 1].Cells)
                yield return $"{growth.Label(n)} 的格数 {counts[n]} 与记录 {growth.Phases[n - 1].Cells} 不符";
        }

        int unbuildable = growth.BuiltCells.Count(k => !buildable.Buildable(k));
        if (unbuildable > 0)
            yield return $"有 {unbuildable} 个建成格不可建设";
        int infill = growth.Phases.ToList().FindIndex(p => p.Name == WalledInfill.PhaseName) + 1;
        if (infill > 0 && r.Get(Keys.Wall) is { } wall)
        {
            int outside = growth.CellsOf(infill).Count(k => !wall.Interior.Contains(grid.Center(k)));
            if (outside > 0)
                yield return $"{growth.Label(infill)} 有 {outside} 格在城外";
        }

        int suburbs = growth.Phases.ToList().FindIndex(p => p.Name == Suburbs.PhaseName) + 1;
        if (suburbs > 0 && r.Get(Keys.Wall) is { } town)
        {
            var cells = growth.CellsOf(suburbs).ToList();
            int onWall = cells.Count(k => Geometry.DistanceToRing(grid.Center(k), town.Ring) < grid.CellSize);
            if (onWall > 0)
                yield return $"{growth.Label(suburbs)} 有 {onWall} 格贴着城墙";

            // Outside cells only once the inside is full: past the widest setback, no free buildable cell inside
            // borders a built one (a landmark's plot is not free).
            if (cells.Any(k => !town.Interior.Contains(grid.Center(k))))
            {
                var plots = r.Get(Keys.Landmarks)!.Cells();
                int left = grid.CellsIn(town.Interior.Offset(-MaxWallSetback)).Count(k =>
                {
                    if (growth.Built(k) || !buildable.Buildable(k) || plots.Contains(k))
                        return false;
                    bool beside = false;
                    grid.ForSides(k, j => beside |= growth.Built(j));
                    return beside;
                });
                if (left > 0)
                    yield return $"{growth.Label(suburbs)} 长到了城外，城内却还有 {left} 格可建设地挨着建成区";
            }
        }

        for (int n = 1; n <= growth.Latest; n++)
        {
            int phase = n;
            bool Built(int k) => growth.Phase[k] is var p && p >= 1 && p <= phase;
            var reached = new bool[grid.Count];
            var queue = new Queue<int>();
            for (int k = 0; k < grid.Count; k++)
            {
                if (Built(k) && grid.AnyNear(k, 1, network.Cells.Contains))
                {
                    reached[k] = true;
                    queue.Enqueue(k);
                }
            }

            while (queue.TryDequeue(out int k))
            {
                grid.ForSides(k, j =>
                {
                    if (!reached[j] && Built(j))
                    {
                        reached[j] = true;
                        queue.Enqueue(j);
                    }
                });
            }

            int cut = Enumerable.Range(0, grid.Count).Count(k => Built(k) && !reached[k]);
            if (cut > 0)
                yield return $"{growth.Label(n)} 之后有 {cut} 个建成格与道路不连通";
        }
    }

    /// <summary>
    /// The market covers only buildable cells, lies at the village (a phase-one cell within two cells of its center)
    /// and inside the wall.
    /// </summary>
    private static IEnumerable<string> PlazaRules(IKeyReader r)
    {
        var plaza = r.Get(Keys.Plaza)!;
        var growth = r.Get(Keys.Growth)!;
        var buildable = r.Get(Keys.Buildable)!;
        var grid = growth.Grid;
        var cells = grid.CellsIn(plaza.Area);
        if (cells.Count == 0)
            yield return "市场没有覆盖任何格";
        if (!cells.All(buildable.Buildable))
            yield return "市场压在不可建设格上";
        int center = grid.CellAt(plaza.Center);
        if (center < 0 || !growth.CellsOf(1).Any(k => grid.Chebyshev(k, center) <= 2))
            yield return "市场不在一期村落里，也不紧邻它";
        if (r.Get(Keys.Wall) is { } wall && plaza.Area.Subtract(wall.Interior).Area >= 1)
            yield return "市场超出城墙";
    }

    /// <summary>Each plot lies on buildable cells that are built by the end of the growth, off the routes and the market.</summary>
    private static IEnumerable<string> LandmarkRules(IKeyReader r)
    {
        var growth = r.Get(Keys.Growth)!;
        var grid = growth.Grid;
        var buildable = r.Get(Keys.Buildable)!;
        var routes = r.Get(Keys.Routes)!;
        var plaza = r.Get(Keys.Plaza)!;
        foreach (var landmark in r.Get(Keys.Landmarks)!.Landmarks)
        {
            if (!ReferenceEquals(landmark.Grid, grid))
            {
                yield return $"{landmark.KindLabel}没有使用共用的采样网格";
                continue;
            }

            if (!landmark.Rect.InGrid(grid))
            {
                yield return $"{landmark.KindLabel}用地超出网格";
                continue;
            }

            var cells = landmark.Cells.ToList();
            if (!cells.All(buildable.Buildable))
                yield return $"{landmark.KindLabel}用地压在不可建设格上";
            if (cells.Any(routes.Cells.Contains))
                yield return $"{landmark.KindLabel}用地压在过境道路上";
            if (landmark.Kind == LandmarkKind.TownHall)
            {
                if (landmark.Plot.Intersect(plaza.Area).Area < landmark.Plot.Area * TownHall.MinInMarket - 1)
                    yield return "市政厅用地在市场里的部分不足";
            }
            else if (landmark.Plot.Intersect(plaza.Area).Area >= 1)
            {
                yield return $"{landmark.KindLabel}用地和市场重叠";
            }

            if (landmark.Kind == LandmarkKind.Friary && cells.Any(k => growth.Phase[k] != 0 && growth.Phase[k] < landmark.Phase))
                yield return "修道院用地压在筑墙前的建成格上";
            if (landmark.Kind == LandmarkKind.Church && growth.Latest >= landmark.Phase && !cells.All(growth.Built))
                yield return $"{landmark.KindLabel}用地在{GrowthHistory.Ordinal(landmark.Phase)}结束时没有全部建成";
        }

        var all = r.Get(Keys.Landmarks)!.Landmarks;
        foreach (var kind in all.GroupBy(l => l.Kind).Where(g => g.Count() > 1))
            yield return $"有 {kind.Count()} 处{kind.First().KindLabel}";
        if (all.Select(l => l.Id).Distinct().Count() != all.Count)
            yield return "地标编号重复";
        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                if (all[i].Plot.Intersect(all[j].Plot).Area >= 1)
                    yield return $"{all[i].KindLabel}用地和{all[j].KindLabel}用地重叠";
            }
        }

        if (r.Get(Keys.Wall) is { } wall && all.FirstOrDefault(l => l.Kind == LandmarkKind.Castle) is { } castle)
        {
            var corners = castle.Plot.Rings[0];
            if (corners.Min(p => Geometry.DistanceToRing(p, wall.Ring)) > grid.CellSize)
                yield return "城堡不在墙线上";
        }

        // Out in the country: outside the wall, on no built cell, beside a route but off it, the two apart.
        var country = all.Where(l => !l.InTown).ToList();
        if (country.Count > 0)
        {
            var toRoute = Proximity.From(grid, routes.Cells, MaxCountryReach + grid.CellSize);
            foreach (var landmark in country)
            {
                var cells = landmark.Cells.ToList();
                if (r.Get(Keys.Wall) is { } town && landmark.Plot.Intersect(town.Interior).Area >= 1)
                    yield return $"{landmark.KindLabel}在城内";
                if (cells.Any(growth.Built))
                    yield return $"{landmark.KindLabel}用地压在建成格上";
                if (cells.Any(k => toRoute[k] < LeperHouse.RouteClearance))
                    yield return $"{landmark.KindLabel}用地离过境道路太近";
                float reach = landmark.Kind == LandmarkKind.LeperHouse ? MaxLeperReach : MaxCountryReach;
                if (!cells.Any(k => toRoute[k] <= reach))
                    yield return $"{landmark.KindLabel}用地不在过境道路边";
            }

            if (country.FirstOrDefault(l => l.Kind == LandmarkKind.LeperHouse) is { } leper
                && country.FirstOrDefault(l => l.Kind == LandmarkKind.Gallows) is { } gallows
                && leper.Center.DistanceTo(gallows.Center) < 200)
                yield return "刑场离麻风院不足 200 m";
        }
    }

    private static bool Diagonal(SiteGrid grid, int a, int b) => a % grid.Nx != b % grid.Nx && a / grid.Nx != b / grid.Nx;

    /// <summary>
    /// The wall is a simple polygon that stays out of the water, holds the landmark plots (the market: see its rules),
    /// encloses at least half of the town built before it, and does not wind around arms and bays for much more than
    /// the circumference of a circle of its area.
    /// </summary>
    private static IEnumerable<string> WallRules(IKeyReader r)
    {
        var wall = r.Get(Keys.Wall)!;
        var ring = wall.Ring;
        var shore = r.Get(Keys.Shore)!;
        if (ring.Length < 3)
            yield return "城墙不足三个顶点";
        for (int i = 0; i < ring.Length; i++)
        {
            for (int j = i + 2; j < ring.Length; j++)
            {
                if (i == 0 && j == ring.Length - 1)
                    continue;
                if (Crosses(ring[i], ring[(i + 1) % ring.Length], ring[j], ring[(j + 1) % ring.Length]))
                {
                    yield return $"城墙自相交（第 {i} 段与第 {j} 段）";
                    i = ring.Length;
                    break;
                }
            }
        }

        if (Geometry.Samples(ring.Append(ring[0]).ToArray(), 2).Any(p => shore.Nearest(p) < 0))
            yield return "城墙进入水域";
        foreach (var landmark in r.Get(Keys.Landmarks)!.Landmarks.Where(l => l.InTown))
        {
            if (landmark.Plot.Subtract(wall.Interior).Area >= 1)
                yield return $"{landmark.KindLabel}用地超出城墙";
        }

        if (wall.BuiltShare is < 0.5f)
            yield return $"城墙只围住筑墙前建成区的 {wall.BuiltShare:P0}";
        if (wall.Winding > MaxWinding)
            yield return $"城墙长度是等面积圆周长的 {wall.Winding:0.00} 倍，超过 {MaxWinding}";
    }

    /// <summary>True if segments ab and cd cross at a point inside both.</summary>
    private static bool Crosses(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float d1 = (b - a).Cross(c - a), d2 = (b - a).Cross(d - a), d3 = (d - c).Cross(a - c), d4 = (d - c).Cross(b - c);
        return (d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0) && d1 != 0 && d2 != 0 && d3 != 0 && d4 != 0;
    }

    /// <summary>
    /// At least one gate, all on the wall and on route cells, each leading to a map-edge entry; and a gate within a
    /// cell and a half of every place a route crosses the wall.
    /// </summary>
    private static IEnumerable<string> GateRules(IKeyReader r)
    {
        var gates = r.Get(Keys.Gates)!.Gates;
        var wall = r.Get(Keys.Wall)!;
        var network = r.Get(Keys.Routes)!;
        var grid = network.Grid;
        if (gates.Count == 0)
            yield return "没有城门";
        if (!gates.All(g => wall.Ring.Contains(g.Position)))
            yield return "城门不在城墙上";
        if (!gates.All(g => network.Cells.Contains(grid.CellAt(g.Position))))
            yield return "城门不在过境道路上";
        if (!gates.All(g => network.Entries.Any(e => e.Position == g.Entry)))
            yield return "城门的去向不是图边入口";
        foreach (var route in network.Routes)
        {
            for (int i = 1; i < route.Cells.Length; i++)
            {
                var a = grid.Center(route.Cells[i - 1]);
                var b = grid.Center(route.Cells[i]);
                if (wall.Interior.Contains(a) != wall.Interior.Contains(b) &&
                    !gates.Any(g => g.Position.DistanceTo((a + b) / 2) <= grid.CellSize * 1.5f))
                    yield return $"{route.Id} 在 {(a + b) / 2} 穿过城墙处没有城门";
            }
        }
    }

    private static IEnumerable<string> RoadRules(IKeyReader r)
    {
        var buildable = r.Get(Keys.Buildable)!;
        var grid = buildable.Grid;
        var interior = r.Get(Keys.Wall)!.Interior;
        var gates = r.Get(Keys.Gates)!.Gates;
        var roads = r.Get(Keys.Roads)!;
        var bridges = r.Get(Keys.Crossings)!.UsedBy(r.Get(Keys.Routes)!).ToList();
        if (!roads.Edges.All(e => e.From < roads.Nodes.Count && e.To < roads.Nodes.Count))
            yield return "路段引用了不存在的节点";
        if (!roads.Edges.All(e => e.Length > 0.01f))
            yield return "存在零长度路段";
        if (roads.Nodes.Count(n => n.Kind == "gate") != gates.Count)
            yield return "城门未全部接入路网";
        if (Reachable(roads).Count != roads.Nodes.Count)
            yield return "路网不连通";

        var growth = r.Get(Keys.Growth)!;
        foreach (var error in TrackRules(roads, r.Get(Keys.LandUse)!, r.Get(Keys.Landmarks)!))
            yield return error;
        var ring = r.Get(Keys.Wall)!.Ring;
        var body = Region.Lines(new[] { ((IReadOnlyList<Vector2>)ring.Append(ring[0]).ToArray(), WallThickness) });
        var throughGates = Region.Polygons(gates.Select(g =>
            Enumerable.Range(0, 16).Select(i => g.Position + Vector2.Right.Rotated(i * MathF.Tau / 16) * GateClearance)));
        // The through routes came before the wall and may hug it; the streets, lanes and tracks the town built may not.
        var built = Region.Lines(roads.Edges.Where(e => e.Kind is not (RoadKind.Main or RoadKind.Outer))
            .Select(e => ((IReadOnlyList<Vector2>)e.Points, e.Width)));
        var onWall = built.Intersect(body.Subtract(throughGates));
        if (onWall.Area >= Tolerance)
            yield return $"街巷铺装压在城墙上（城门以外）{onWall.Area:0.0} m²，在 {onWall.Bounds.GetCenter()} 一带";
        var dead = RoadTrees.DeadEnds(roads.Edges.Select(e => (e.From, e.To, e.Kind is not (RoadKind.Main or RoadKind.Outer))).ToList(),
            n => roads.Nodes[n].Kind is not ("end" or "junction"));
        foreach (var edge in roads.Edges.Where(e => e.Kind == RoadKind.Street && dead.Contains(e.Id)))
            yield return $"次街 {edge.Id} 只通向一片地（应为巷）";
        foreach (var landmark in r.Get(Keys.Landmarks)!.Landmarks.Where(l => l.Kind != LandmarkKind.TownHall))
        {
            if (landmark.Plot.Intersect(roads.Footprint).Area >= Tolerance)
                yield return $"道路压在{landmark.KindLabel}用地上";
            if (landmark.Plot.Offset(LandmarkAccess.Reach + grid.CellSize).Intersect(roads.Footprint).Area < Tolerance)
                yield return $"{landmark.KindLabel}用地旁边没有道路";
        }
        foreach (var edge in roads.Edges)
        {
            bool? streetInside = null;
            foreach (var p in Geometry.Samples(edge.Points, 2))
            {
                int k = grid.CellAt(p);
                bool nearGate = gates.Any(g => g.Position.DistanceTo(p) < grid.CellSize * 1.5f);
                bool onBridge = bridges.Any(b => Geometry.DistanceToSegment(p, b.BankA, b.BankB) <= grid.CellSize);
                if (k < 0 || buildable.Block(k) == SiteBlock.Water && !onBridge)
                {
                    yield return $"路段 {edge.Id} 在渡口以外穿过水域 {p}";
                    break;
                }

                if (nearGate)
                    continue;
                bool inside = interior.Contains(p);
                bool expected = edge.Kind switch
                {
                    RoadKind.Main => true,
                    RoadKind.Outer or RoadKind.Track => false,
                    _ => streetInside ??= inside,
                };
                if (inside != expected)
                {
                    yield return $"路段 {edge.Id}（{edge.Kind}）在城门以外穿越城墙 {p}";
                    break;
                }

                if (edge.Kind is RoadKind.Street or RoadKind.Lane && !inside && !grid.AnyNear(k, 1, growth.Built))
                {
                    yield return $"城外巷道 {edge.Id} 离开了城外街区 {p}";
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Planned areas lie on land built in phase 3 or later; their streets are roads, cell to cell, and keep to their
    /// iso-lines (a cell of slack beside the band, where the search needed it).
    /// </summary>
    private static IEnumerable<string> PlanRules(IKeyReader r)
    {
        var plan = r.Get(Keys.StreetPlan)!;
        var growth = r.Get(Keys.Growth)!;
        var links = r.Get(Keys.RoadLinks)!;
        var grid = plan.Grid;
        float slack = PlannedStreets.HalfBand + 1.5f * grid.CellSize;
        foreach (var (area, i) in plan.Areas.Select((a, i) => (a, i)))
        {
            if (area.Cells.Any(k => growth.Phase[k] < 3))
                yield return $"规划区 {i} 有三期以前建成或没建成的格";
            foreach (var street in area.Streets)
            {
                string name = street.Cross ? $"横街（沿线 {street.Offset:0} m）" : $"平行街（偏移 {street.Offset:0} m）";
                if (street.Cells.Zip(street.Cells.Skip(1)).Any(p => !links.TryGet(p.First, p.Second, out _)))
                    yield return $"规划区 {i} 的{name}有两格之间没有道路连线";
                float worst = street.Cells.Select(k => area.Frame(grid.Center(k)))
                    .Max(f => MathF.Abs((street.Cross ? f.Along : f.Offset) - street.Offset));
                if (worst > slack)
                    yield return $"规划区 {i} 的{name}偏离等值线 {worst:0.0} m";
            }
        }
    }

    /// <summary>
    /// Small squares stand on buildable cells inside the wall, off the landmark plots and each other, next to a road,
    /// and paved.
    /// </summary>
    private static IEnumerable<string> SquareRules(IKeyReader r)
    {
        var squares = r.Get(Keys.Squares)!.Squares;
        var buildable = r.Get(Keys.Buildable)!;
        var grid = buildable.Grid;
        var interior = r.Get(Keys.Wall)!.Interior;
        var plots = r.Get(Keys.Landmarks)!.Plots;
        var roads = r.Get(Keys.Roads)!;
        var lines = Region.Lines(roads.Edges.Select(e => ((IReadOnlyList<Vector2>)e.Points, e.Width)));
        for (int i = 0; i < squares.Count; i++)
        {
            var square = squares[i];
            string name = $"{square.Label}（{square.Of}）";
            if (grid.CellsIn(square.Area).Any(k => !buildable.Buildable(k)))
                yield return $"{name}压在不可建设格上";
            if (square.Area.Subtract(interior).Area >= Tolerance)
                yield return $"{name}伸出城墙";
            if (square.Area.Intersect(plots).Area >= Tolerance)
                yield return $"{name}压在地标用地上";
            if (squares.Take(i).Any(other => other.Area.Intersect(square.Area).Area >= Tolerance))
                yield return $"{name}与别的小广场重叠";
            if (square.Area.Offset(0.5f).Intersect(lines).Area < Tolerance)
                yield return $"{name}没有接上道路";
            if (square.Area.Subtract(roads.Footprint).Area >= Tolerance)
                yield return $"{name}没有并进铺装区";
        }
    }

    /// <summary>The wall's thickness as drawn, and how far from a gate a road through it may overlap the wall.</summary>
    private const float WallThickness = 2.6f, GateClearance = 12;

    /// <summary>Every connected set of tracks reaches a trade or a landmark out in the country: no track leads nowhere.</summary>
    private static IEnumerable<string> TrackRules(RoadGraph roads, CategoryField land, LandmarkSet landmarks)
    {
        var grid = land.Grid;
        var tracks = roads.Edges.Where(e => e.Kind == RoadKind.Track).ToList();
        var group = tracks.ToDictionary(e => e.Id, e => e.Id);
        int Root(int id)
        {
            while (group[id] != id)
                id = group[id] = group[group[id]];
            return id;
        }

        foreach (var a in tracks)
            foreach (var b in tracks.Where(b => b.Id > a.Id && (b.From == a.From || b.From == a.To || b.To == a.From || b.To == a.To)))
                group[Root(b.Id)] = Root(a.Id);

        bool NearTrade(Vector2 p) => grid.CellAt(p) is int k && k >= 0
            && grid.AnyNear(k, 1, j => LandUses.Trades.Contains(land.Use(j)));
        var country = landmarks.Landmarks.Where(l => !l.InTown).Select(l => l.Plot.Offset(LandmarkAccess.Reach + grid.CellSize))
            .ToList();
        bool NearLandmark(Vector2 p) => country.Any(plot => plot.Contains(p));
        var reaching = tracks.Where(e => Geometry.Samples(e.Points, 2).Any(p => NearTrade(p) || NearLandmark(p)))
            .Select(e => Root(e.Id)).ToHashSet();
        foreach (var root in tracks.Select(e => Root(e.Id)).Distinct().Where(g => !reaching.Contains(g)))
            yield return $"便道 {root} 一带没有通到任何作坊或城外地标";
    }

    private static IEnumerable<string> BlockRules(IKeyReader r)
    {
        var blocks = r.Get(Keys.Blocks)!;
        var roads = r.Get(Keys.Roads)!;
        var ring = r.Get(Keys.Wall)!.Ring;
        if (blocks.Developable.Intersect(Region.Lines(new[] { ((IReadOnlyList<Vector2>)ring.Append(ring[0]).ToArray(), 4f) }))
            .Area >= Tolerance)
            yield return "可建区压到城墙";
        var growth = r.Get(Keys.Growth)!;
        var trades = r.Get(Keys.LandUse)!.Area(LandUses.Trades);
        if (blocks.Developable.Subtract(growth.Footprint.Union(trades).Offset(growth.Grid.CellSize)).Area >= Tolerance)
            yield return "可建区超出建成格和作坊格一格以上（压到菜园或田地）";
        if (blocks.Developable.Intersect(roads.Footprint).Area >= Tolerance)
            yield return "可建区压到道路";
    }

    private static IEnumerable<string> ParcelRules(IKeyReader r)
    {
        var developable = r.Get(Keys.Blocks)!.Developable;
        var footprint = r.Get(Keys.Roads)!.Footprint;
        var parcels = r.Get(Keys.Parcels)!.Parcels;
        var land = r.Get(Keys.LandUse)!;
        var interior = r.Get(Keys.Wall)!.Interior;
        var regions = parcels.Select(p => Region.Polygon(p.Ring)).ToList();
        var edges = r.Get(Keys.Roads)!.Edges.Where(e => e.Kind != RoadKind.Track).ToList();
        var gates = r.Get(Keys.Gates)!.Gates;
        var market = r.Get(Keys.Plaza)!.Area.Offset(ParcelKinds.MarketReach + 1);
        if (parcels.Count(p => p.Kind == ParcelKind.Court) > 2 * gates.Count + 1)
            yield return "客栈大院比城门内外和市场边的个数还多";
        for (int i = 0; i < regions.Count; i++)
        {
            var parcel = parcels[i];
            if (parcel.Id != i)
                yield return $"地块 {i} 的编号是 {parcel.Id}";
            if (LandUses.Trades.Contains(parcel.Use) != (parcel.Kind == ParcelKind.Trade))
                yield return $"地块 {i} 的类型{parcel.Kind.Label()}与用途{parcel.Use.Label()}不符";
            var front = (parcel.FrontB - parcel.FrontA).Normalized();
            if (parcel.Kind == ParcelKind.Corner)
            {
                if (parcel.Side is not { Length: 2 } side
                    || side.Any(end => !edges.Any(e => ParcelKinds.AlongSide(e, end, front, ParcelKinds.SideReach + 0.3f))))
                    yield return $"街角地块 {i} 的侧边不在另一条路旁";
            }
            else if (parcel.Side != null)
                yield return $"地块 {i} 不是街角却记了侧边";
            if (parcel.Kind == ParcelKind.Court)
            {
                if (parcel.Street is not (RoadKind.Main or RoadKind.Outer))
                    yield return $"客栈大院 {i} 不临主街或城外道路";
                if (!gates.Any(g => g.Position.DistanceTo(parcel.Probe) <= ParcelKinds.MaxCourtReach) && !market.Contains(parcel.Probe))
                    yield return $"客栈大院 {i} 不在城门或市场附近";
                if (parcel.Frontage < ParcelKinds.MinCourtFrontage || parcel.Frontage > ParcelKinds.MaxCourtFrontage)
                    yield return $"客栈大院 {i} 面宽 {parcel.Frontage:0.0} m";
            }

            bool inside = interior.Contains(parcel.Probe);
            if (!LandUses.Plots.Contains(parcel.Use) || LandUses.Inside.Contains(parcel.Use) != inside)
                yield return $"地块 {i} 在墙{(inside ? "内" : "外")}，用途却是{parcel.Use.Label()}";
            else if (parcel.Use != land.PlotUse(parcel.Probe, inside))
                yield return $"地块 {i} 的用途{parcel.Use.Label()}与临街处的用地不符";
            if (regions[i].Subtract(developable).Area >= Tolerance)
                yield return $"地块 {i} 超出可建区";
            if (regions[i].Intersect(footprint).Area >= Tolerance)
                yield return $"地块 {i} 压到道路";
        }

        foreach (var (a, b) in Overlaps(regions))
            yield return $"地块 {a} 与 {b} 重叠";
    }

    /// <summary>
    /// Fences keep off the paving and out of the houses; back buildings and gardens stay in their plots, clear of
    /// the plot's house and of each other.
    /// </summary>
    private static IEnumerable<string> YardRules(IKeyReader r)
    {
        var yards = r.Get(Keys.Yards)!;
        var parcels = r.Get(Keys.Parcels)!.Parcels;
        var houses = r.Get(Keys.Buildings)!.Buildings.ToDictionary(b => b.ParcelId, b => Region.Polygon(b.Corners));
        var paving = r.Get(Keys.Roads)!.Footprint.Offset(-0.3f);
        var inner = houses.Values.Aggregate(Region.Empty, (all, h) => all.Union(h.Offset(-0.1f)));
        foreach (var (fence, i) in yards.Fences.Select((f, i) => (f, i)))
        {
            var samples = Geometry.Samples(fence.Points, 2).ToList();
            if (samples.Any(paving.Contains))
                yield return $"院墙 {i} 压在路面上";
            if (samples.Any(inner.Contains))
                yield return $"院墙 {i} 穿过房屋";
        }

        var sheds = new Dictionary<int, Region>();
        foreach (var shed in yards.Outbuildings)
        {
            var area = Region.Polygon(shed.Corners);
            sheds[shed.ParcelId] = area;
            if (area.Subtract(Region.Polygon(parcels[shed.ParcelId].Ring)).Area >= Tolerance)
                yield return $"地块 {shed.ParcelId} 的{shed.Label}超出地块";
            if (houses.TryGetValue(shed.ParcelId, out var house) && area.Intersect(house).Area >= Tolerance)
                yield return $"地块 {shed.ParcelId} 的{shed.Label}压到房屋";
        }

        if (yards.Outbuildings.GroupBy(b => b.ParcelId).Any(g => g.Count() > 1))
            yield return "有地块不止一间后屋";
        foreach (var garden in yards.Gardens)
        {
            if (garden.Area.Subtract(Region.Polygon(parcels[garden.ParcelId].Ring)).Area >= Tolerance)
                yield return $"地块 {garden.ParcelId} 的菜园超出地块";
            if (houses.TryGetValue(garden.ParcelId, out var house) && garden.Area.Intersect(house).Area >= Tolerance)
                yield return $"地块 {garden.ParcelId} 的菜园压到房屋";
            if (sheds.TryGetValue(garden.ParcelId, out var shed) && garden.Area.Intersect(shed).Area >= Tolerance)
                yield return $"地块 {garden.ParcelId} 的菜园压到后屋";
        }
    }

    /// <summary>Wells stand in the paving, apart; every built cell has a well or the water within reach.</summary>
    private static IEnumerable<string> FixtureRules(IKeyReader r)
    {
        var wells = r.Get(Keys.Fixtures)!.Wells.Select(f => f.Position).ToList();
        var paving = r.Get(Keys.Roads)!.Footprint.Offset(0.1f);
        var growth = r.Get(Keys.Growth)!;
        var shore = r.Get(Keys.Shore)!;
        if (wells.Any(w => !paving.Contains(w)))
            yield return "有水井不在铺装上";
        for (int i = 0; i < wells.Count; i++)
        {
            if (wells.Skip(i + 1).Any(w => w.DistanceTo(wells[i]) < Wells.MinSpacing - 0.01f))
                yield return $"水井 {i} 离另一口井不到 {Wells.MinSpacing:0} m";
        }

        int dry = growth.BuiltCells.Count(k => shore.Values[k] > Wells.Reach
            && !wells.Any(w => w.DistanceTo(growth.Grid.Center(k)) <= Wells.Reach + 0.01f));
        if (dry > 0)
            yield return $"{dry} 个建成格 {Wells.Reach:0} m 内既没有井也没有水";
    }

    /// <summary>
    /// Every crossing a route uses has a bridge. A deck's ends stand on land, the deck clears the water, its piers stand
    /// in the water no farther apart than allowed, and the road meets the deck at its height.
    /// </summary>
    private static IEnumerable<string> BridgeRules(IKeyReader r)
    {
        var bridges = r.Get(Keys.Bridges)!.Bridges;
        var shore = r.Get(Keys.Shore)!;
        foreach (var crossing in r.Get(Keys.Crossings)!.UsedBy(r.Get(Keys.Routes)!))
        {
            if (bridges.All(b => b.Crossing != crossing.Id))
                yield return $"渡口 {crossing.Id} 没有桥";
        }

        var plan = r.Get(Keys.Grading);
        foreach (var bridge in bridges)
        {
            if (shore.Nearest(bridge.Deck[0]) < 0 || shore.Nearest(bridge.Deck[^1]) < 0)
                yield return $"桥 {bridge.Crossing} 的桥台在水里";
            if (bridge.Height < bridge.Water + bridge.Clearance - 0.01f)
                yield return $"桥 {bridge.Crossing} 的桥面只高出水面 {bridge.Height - bridge.Water:0.0} m";
            // A pier between two wet points of a straight deck may stand on a spit of the bank; not farther in than that.
            if (bridge.Piers.Any(p => shore.Nearest(p) > 4))
                yield return $"桥 {bridge.Crossing} 有桥墩在岸上";
            // The spans: between piers, and from the outer piers to the banks (the water's edge, found along the deck).
            var wet = Geometry.Samples(bridge.Deck, 0.5f).Where(p => shore.Nearest(p) < 0).ToList();
            var supports = wet.Count == 0 ? bridge.Deck.ToList() : bridge.Piers.Prepend(wet[0]).Append(wet[^1]).ToList();
            for (int i = 1; i < supports.Count; i++)
            {
                if (supports[i].DistanceTo(supports[i - 1]) > bridge.MaxSpan + 0.5f)
                    yield return $"桥 {bridge.Crossing} 有一跨 {supports[i].DistanceTo(supports[i - 1]):0} m，超过 {bridge.MaxSpan:0} m";
            }

            if (plan == null || plan.Corridors.Count == 0)
                continue;
            foreach (var end in new[] { bridge.Deck[0], bridge.Deck[^1] })
            {
                bool met = plan.Corridors.Where(k => k.Edge == bridge.Edge).Any(k => Enumerable.Range(0, k.Points.Length)
                    .Any(i => k.Points[i].DistanceSquaredTo(end) < 0.01f && MathF.Abs(k.Heights[i] - bridge.Height) <= 0.05f));
                if (!met)
                    yield return $"桥 {bridge.Crossing} 的桥头处道路不在桥面的高度上";
            }
        }
    }

    /// <summary>
    /// Grading leaves the water, the rim of the map and everything away from the roads and platforms as the terrain has
    /// them. A road's center line stays within the earthwork limit of the terrain; a road not marked steep keeps to
    /// its grade limit, and only a small share is steep; the roads meeting at a junction meet at one height; a road is
    /// level across; the market is one plane.
    /// </summary>
    private static IEnumerable<string> GradingRules(IKeyReader r)
    {
        var plan = r.Get(Keys.Grading)!;
        var patch = plan.Patch;
        var shore = r.Get(Keys.Shore)!;
        float half = r.Terrain.Half;
        if (!patch.Offsets.All(float.IsFinite))
            yield return "修整后的高度存在非有限值";

        int wet = 0, rim = 0, moved = 0;
        for (int z = 0; z < patch.Nz; z++)
        {
            for (int x = 0; x < patch.Nx; x++)
            {
                if (MathF.Abs(patch.Offsets[z * patch.Nx + x]) < 1e-4f)
                    continue;
                moved++;
                var p = new Vector2(patch.X0 + x, patch.Z0 + z);
                if (shore.Nearest(p) < 0)
                    wet++;
                if (half - MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)) < GradeBake.Rim - 0.01f)
                    rim++;
            }
        }

        if (wet > 0)
            yield return $"{wet} 个水面点被挖填";
        if (rim > 0)
            yield return $"{rim} 个离图边 {GradeBake.Rim:0} m 以内的点被挖填";
        // The reach counts lattice steps, a diagonal as one, so it may stretch to the reach times √2.
        float reach = GradeBake.Reach * 1.5f;
        double room = plan.Corridors.Sum(c => (double)(c.Length + 2 * reach) * (c.Width + 2 * reach + 2))
            + plan.Platforms.Sum(p => p.Area.Offset(reach + 1).Area);
        if (moved > room)
            yield return $"被挖填的地面 {moved} m² 超出道路和平台周围的范围 {room:0} m²";
        var bridges = r.Get(Keys.Bridges)!.Bridges;
        // A bridge's approaches rise to its deck, however far that stands above the bank.
        float lift = bridges.Select(b => MathF.Max(b.Height - r.Terrain.HeightAt(b.Deck[0]), b.Height - r.Terrain.HeightAt(b.Deck[^1])))
            .DefaultIfEmpty(0).Max();
        if (plan.MaxCut > plan.EarthworkLimit + MaxCrossFall || plan.MaxFill > plan.EarthworkLimit + MaxCrossFall + MathF.Max(0, lift))
            yield return $"最深挖 {plan.MaxCut:0.0} m、最高填 {plan.MaxFill:0.0} m，超过上限 {plan.EarthworkLimit:0.0} m 加横坡余量";

        // Inside the market a road lies in the market's plane, which may sit farther from the terrain.
        var markets = plan.Platforms.Where(p => p.Kind == PlatformKind.Market).Select(p => p.Area).ToList();
        foreach (var error in Continuity(r, plan))
            yield return error;

        float total = 0, steep = 0;
        int deep = 0, tilted = 0, crossChecked = 0;
        var meet = new Dictionary<(int, int), (float Low, float High)>();
        foreach (var corridor in plan.Corridors)
        {
            total += corridor.Length;
            if (corridor.Steep)
                steep += corridor.Length;
            else if (corridor.MaxGrade > corridor.GradeLimit * 1.02f + 0.01f)
                yield return $"路段 {corridor.Edge} 坡度 {corridor.MaxGrade:P1}，超过上限 {corridor.GradeLimit:P1}";

            for (int i = 0; i < corridor.Points.Length; i++)
            {
                var p = corridor.Points[i];
                bool water = shore.Nearest(p) < 0;
                if (!water && MathF.Abs(corridor.Heights[i] - r.Terrain.HeightAt(p)) > plan.EarthworkLimit + RoadProfiles.Slack + 0.05f
                    && !markets.Any(m => m.Contains(p))
                    && !bridges.Any(b => b.Deck[0].DistanceTo(p) < ApproachReach || b.Deck[^1].DistanceTo(p) < ApproachReach))
                    deep++;
                // Level across, away from the ends (where other roads take over), the water and the rim.
                if (i < 5 || i >= corridor.Points.Length - 5 || i % 5 != 0 || water
                    || half - MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)) < 2 * GradeBake.Rim + corridor.Width)
                    continue;
                var across = (corridor.Points[i + 1] - corridor.Points[i - 1]).Normalized().Orthogonal() * corridor.Width * 0.35f;
                if (shore.Nearest(p + across) < 0 || shore.Nearest(p - across) < 0)
                    continue;
                crossChecked++;
                if (MathF.Abs(plan.HeightAt(p + across) - plan.HeightAt(p - across)) > 0.15f)
                    tilted++;
            }

            foreach (int i in new[] { 0, corridor.Points.Length - 1 })
            {
                var key = ((int)MathF.Round(corridor.Points[i].X * 10), (int)MathF.Round(corridor.Points[i].Y * 10));
                float h = corridor.Heights[i];
                meet[key] = meet.TryGetValue(key, out var range) ? (MathF.Min(range.Low, h), MathF.Max(range.High, h)) : (h, h);
            }
        }

        if (deep > 0)
            yield return $"{deep} 个路中线点离原地形超过挖填上限 {plan.EarthworkLimit:0.0} m";
        if (total > 0 && steep > total * MaxSteepShare)
            yield return $"陡坡段 {steep:0} m，占道路的 {steep / total:P0}，超过 {MaxSteepShare:P0}";
        if (tilted > crossChecked * 0.05f)
            yield return $"{tilted}/{crossChecked} 处路面两侧高差超过 0.15 m";
        int apart = meet.Values.Count(range => range.High - range.Low > 0.05f);
        if (apart > 0)
            yield return $"{apart} 个路口处各条路的高度相差超过 5 cm";

        int sloped = plan.Platforms.Count(p => p.Kind is PlatformKind.Plot or PlatformKind.Landmark && p.Plane.Grade > 0);
        if (sloped > 0)
            yield return $"{sloped} 块地块或地标平台不是水平的";
        if (plan.Walls.Any(w => w.High <= w.Low || w.Points.Length < 2))
            yield return "有挡土墙没有高差或不成线";

        foreach (var platform in plan.Platforms.Where(p => p.Kind == PlatformKind.Market))
        {
            if (platform.Plane.Grade > MaxMarketGrade + 0.001f)
                yield return $"市场平面坡度 {platform.Plane.Grade:P1}";
            var inner = platform.Area.Offset(-1.5f);
            var box = inner.Bounds;
            int off = 0;
            for (float z = MathF.Ceiling(box.Position.Y); z <= box.End.Y; z += 2)
            {
                for (float x = MathF.Ceiling(box.Position.X); x <= box.End.X; x += 2)
                {
                    var p = new Vector2(x, z);
                    if (inner.Contains(p) && shore.Nearest(p) >= 0 && MathF.Abs(plan.HeightAt(p) - platform.Plane.HeightAt(p)) > 0.1f)
                        off++;
                }
            }

            if (off > 0)
                yield return $"市场有 {off} 个点离它的平面超过 0.1 m";
        }
    }

    /// <summary>
    /// The graded ground is continuous: between neighboring lattice points it steps no more than the terrain did, plus a
    /// little, or than a steep bank would. The exceptions are a platform's edge, and the ground within a couple of meters
    /// of it, too narrow to ease the step (where a retaining wall stands), and the water's edge (where a bridge's
    /// approach, filled up beside the river, falls to the bank within a few meters: a wing wall's place). A few breaks
    /// remain where the plan puts two roads at different heights within meters of each other (the legs of a road
    /// zigzagging up a slope, a track hugging a road); no ground could join them, so up to <see cref="MaxBreakShare"/>
    /// of the graded points may break.
    /// </summary>
    private static IEnumerable<string> Continuity(IKeyReader r, GradePlan plan)
    {
        var patch = plan.Patch;
        var shore = r.Get(Keys.Shore)!;
        var buckets = new Dictionary<(int, int), List<GradePlatform>>();
        foreach (var platform in plan.Platforms)
        {
            var box = platform.Area.Bounds;
            for (int bz = (int)MathF.Floor(box.Position.Y / 16); bz <= (int)MathF.Floor(box.End.Y / 16); bz++)
            {
                for (int bx = (int)MathF.Floor(box.Position.X / 16); bx <= (int)MathF.Floor(box.End.X / 16); bx++)
                {
                    if (!buckets.TryGetValue((bx, bz), out var list))
                        buckets[(bx, bz)] = list = new List<GradePlatform>();
                    list.Add(platform);
                }
            }
        }

        IEnumerable<GradePlatform> Near(Vector2 p) =>
            buckets.TryGetValue(((int)MathF.Floor(p.X / 16), (int)MathF.Floor(p.Y / 16)), out var list) ? list : [];
        GradePlatform? On(Vector2 p) => Near(p).FirstOrDefault(q => q.Area.Contains(p));
        bool ByWall(Vector2 p) => Near(p).Any(q => q.Kind is PlatformKind.Plot or PlatformKind.Landmark
            && q.Area.Rings.Any(ring => Geometry.DistanceToRing(p, ring) <= WallGap));

        int broken = 0, graded = 0;
        string? example = null;
        for (int z = patch.Z0 - 1; z < patch.Z0 + patch.Nz; z++)
        {
            for (int x = patch.X0 - 1; x < patch.X0 + patch.Nx; x++)
            {
                foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                {
                    float da = patch.At(x, z), db = patch.At(x + dx, z + dz);
                    if (dx == 1 && da != 0)
                        graded++;
                    if (MathF.Abs(da - db) < MaxGroundStep)
                        continue;
                    Vector2 p = new(x, z), q = new(x + dx, z + dz);
                    float before = r.Terrain.HeightAt(q) - r.Terrain.HeightAt(p);
                    float after = before + db - da;
                    if (MathF.Abs(after) <= MathF.Max(MaxGroundStep, MathF.Abs(before) + MaxGroundStep / 2))
                        continue;
                    if (shore.Nearest(p) < BankReach || shore.Nearest(q) < BankReach || !ReferenceEquals(On(p), On(q)) || ByWall(p) || ByWall(q))
                        continue;
                    broken++;
                    example ??= $"({x}, {z}) 处高差 {MathF.Abs(after):0.0} m";
                }
            }
        }

        if (broken > graded * MaxBreakShare)
            yield return $"{broken} 处相邻两点间的地面断开（{example}），不在平台边或水边，超过修整面积 {graded} m² 的 {MaxBreakShare:P2}";
    }

    private static IEnumerable<string> BuildingRules(IKeyReader r)
    {
        var parcels = r.Get(Keys.Parcels)!.Parcels;
        var buildings = r.Get(Keys.Buildings)!.Buildings;
        var regions = buildings.Select(b => Region.Polygon(b.Corners)).ToList();
        if (regions.Count == 0)
            yield return "没有建筑";
        for (int i = 0; i < regions.Count; i++)
        {
            var building = buildings[i];
            if (regions[i].Subtract(Region.Polygon(parcels[building.ParcelId].Ring)).Area >= Tolerance)
                yield return $"建筑 {i} 超出地块";
            if (building.Eaves <= building.Base || building.Ridge <= building.Eaves)
                yield return $"建筑 {i} 高度无效";
        }

        foreach (var (a, b) in Overlaps(regions))
            yield return $"建筑 {a} 与 {b} 重叠";

        // On graded ground a house stands level: few have corners at different heights.
        if (r.Get(Keys.Grading) is { Platforms.Count: > 0 } grading && grading.Platforms.Any(p => p.Kind == PlatformKind.Plot))
        {
            // Measured a little inside the walls: at a wall shared with a neighbor the ground steps to its platform.
            int uneven = buildings.Count(b =>
            {
                var middle = (b.Corners[0] + b.Corners[2]) / 2;
                var under = b.Corners.Select(v => grading.HeightAt(v.MoveToward(middle, 1.5f))).ToList();
                return under.Max() - under.Min() > MaxFloorStep;
            });
            if (uneven > buildings.Count * MaxUnevenShare)
                yield return $"{uneven}/{buildings.Count} 栋房屋四角处的地面高差超过 {MaxFloorStep} m";
        }
    }

    private static HashSet<int> Reachable(RoadGraph roads)
    {
        var seen = new HashSet<int> { roads.CoreNode };
        var queue = new Queue<int>(seen);
        while (queue.TryDequeue(out int node))
        {
            foreach (var edge in roads.Edges.Where(e => e.From == node || e.To == node))
            {
                int next = edge.From == node ? edge.To : edge.From;
                if (seen.Add(next))
                    queue.Enqueue(next);
            }
        }

        return seen;
    }

    private static IEnumerable<(int, int)> Overlaps(List<Region> regions)
    {
        var order = Enumerable.Range(0, regions.Count).OrderBy(i => regions[i].Bounds.Position.X).ToArray();
        for (int i = 0; i < order.Length; i++)
        {
            var a = regions[order[i]];
            for (int j = i + 1; j < order.Length; j++)
            {
                var b = regions[order[j]];
                if (b.Bounds.Position.X > a.Bounds.End.X)
                    break;
                if (a.Bounds.Intersects(b.Bounds) && a.Intersect(b).Area > Tolerance)
                    yield return (order[i], order[j]);
            }
        }
    }
}
