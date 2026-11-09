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
        new(Keys.Site, SiteRules),
        new(Keys.Crossings, CrossingRules),
        new(Keys.Routes, RouteRules),
        new(Keys.Origin, OriginRules),
        new(Keys.Growth, GrowthRules),
        new(Keys.Plaza, PlazaRules),
        new(Keys.Landmarks, LandmarkRules),
        new(Keys.Core, CoreRules),
        new(Keys.Gates, GateRules),
        new(Keys.Roads, RoadRules),
        new(Keys.Blocks, BlockRules),
        new(Keys.Parcels, ParcelRules),
        new(Keys.Buildings, BuildingRules),
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
            else if (cells.Zip(cells.Skip(1)).Any(p => Chebyshev(grid, p.First, p.Second) != 1 || Diagonal(grid, p.First, p.Second)))
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
            if (path.Zip(path.Skip(1)).Any(p => Chebyshev(grid, p.First, p.Second) != 1))
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

    /// <summary>
    /// Phases are numbered from 1 with the cell counts they record; every built cell is buildable; and after each
    /// phase, every group of built cells joined by side neighbors reaches a route cell (or touches one).
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
            if (counts[n] == 0)
                yield return $"{growth.Label(n)} 没有新建成的格";
        }

        int unbuildable = Enumerable.Range(0, grid.Count).Count(k => growth.Built(k) && !buildable.Buildable(k));
        if (unbuildable > 0)
            yield return $"有 {unbuildable} 个建成格不可建设";

        bool TouchesRoad(int k)
        {
            int x = k % grid.Nx, z = k / grid.Nx;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (grid.InGrid(x + dx, z + dz) && network.Cells.Contains(grid.Index(x + dx, z + dz)))
                        return true;
                }
            }

            return false;
        }

        for (int n = 1; n <= growth.Latest; n++)
        {
            int phase = n;
            bool Built(int k) => growth.Phase[k] is var p && p >= 1 && p <= phase;
            var reached = new bool[grid.Count];
            var queue = new Queue<int>();
            for (int k = 0; k < grid.Count; k++)
            {
                if (Built(k) && TouchesRoad(k))
                {
                    reached[k] = true;
                    queue.Enqueue(k);
                }
            }

            while (queue.TryDequeue(out int k))
            {
                Growth.ForSides(grid, k, j =>
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
        var cells = Growth.CellsIn(grid, plaza.Area);
        if (cells.Count == 0)
            yield return "市场没有覆盖任何格";
        if (!cells.All(buildable.Buildable))
            yield return "市场压在不可建设格上";
        int center = grid.CellAt(plaza.Center);
        if (center < 0 || !growth.CellsOf(1).Any(k => Chebyshev(grid, k, center) <= 2))
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
            if (landmark.X0 < 0 || landmark.Z0 < 0 || landmark.X0 + landmark.Nx > grid.Nx || landmark.Z0 + landmark.Nz > grid.Nz)
            {
                yield return $"{landmark.KindLabel}用地超出网格";
                continue;
            }

            var cells = landmark.Cells(grid).ToList();
            if (Math.Abs(landmark.Plot.Area - cells.Count * grid.CellSize * grid.CellSize) > 1)
                yield return $"{landmark.KindLabel}用地与它的格不符";
            if (!cells.All(buildable.Buildable))
                yield return $"{landmark.KindLabel}用地压在不可建设格上";
            if (cells.Any(routes.Cells.Contains))
                yield return $"{landmark.KindLabel}用地压在过境道路上";
            if (landmark.Plot.Intersect(plaza.Area).Area >= 1)
                yield return $"{landmark.KindLabel}用地和市场重叠";
            if (growth.Latest >= landmark.Phase && !cells.All(growth.Built))
                yield return $"{landmark.KindLabel}用地在{GrowthHistory.Ordinal(landmark.Phase)}结束时没有全部建成";
        }
    }

    private static int Chebyshev(SiteGrid grid, int a, int b) =>
        Math.Max(Math.Abs(a % grid.Nx - b % grid.Nx), Math.Abs(a / grid.Nx - b / grid.Nx));

    private static bool Diagonal(SiteGrid grid, int a, int b) => a % grid.Nx != b % grid.Nx && a / grid.Nx != b / grid.Nx;

    private static IEnumerable<string> SiteRules(IKeyReader r)
    {
        var site = r.Get(Keys.Site)!;
        if (!ReferenceEquals(site.Grid, r.Get(Keys.Grid)))
            yield return "没有使用共用的采样网格";
        if (site.BuildableFraction is <= 0.05f or >= 0.95f)
            yield return $"场地可建设比例异常 {site.BuildableFraction:P0}";
    }

    private static IEnumerable<string> CoreRules(IKeyReader r)
    {
        var site = r.Get(Keys.Site)!;
        if (!site.Buildable(site.CellAt(r.Get(Keys.Core)!.Position)))
            yield return "城址不在可建设格";
    }

    private static IEnumerable<string> GateRules(IKeyReader r)
    {
        var gates = r.Get(Keys.Gates)!.Gates;
        var wall = r.Get(Keys.Wall)!.Ring;
        if (gates.Count == 0)
            yield return "没有城门";
        if (!gates.All(g => wall.Contains(g.Position)))
            yield return "城门不在城墙上";
    }

    private static IEnumerable<string> RoadRules(IKeyReader r)
    {
        var site = r.Get(Keys.Site)!;
        var interior = r.Get(Keys.Wall)!.Interior;
        var gates = r.Get(Keys.Gates)!.Gates;
        var roads = r.Get(Keys.Roads)!;
        if (!roads.Edges.All(e => e.From < roads.Nodes.Count && e.To < roads.Nodes.Count))
            yield return "路段引用了不存在的节点";
        if (!roads.Edges.All(e => e.Length > 0.01f))
            yield return "存在零长度路段";
        if (roads.Nodes.Count(n => n.Kind == "gate") != gates.Count)
            yield return "城门未全部接入路网";
        if (Reachable(roads).Count != roads.Nodes.Count)
            yield return "路网不连通";

        foreach (var edge in roads.Edges)
        {
            foreach (var p in Geometry.Samples(edge.Points, 2))
            {
                int k = site.CellAt(p);
                bool nearGate = gates.Any(g => g.Position.DistanceTo(p) < site.CellSize * 1.5f);
                if (k < 0 || site.Block[k] == SiteBlock.Water)
                {
                    yield return $"路段 {edge.Id} 穿过水域 {p}";
                    break;
                }

                if (!nearGate && interior.Contains(p) != (edge.Kind != RoadKind.Outer))
                {
                    yield return $"路段 {edge.Id}（{edge.Kind}）在城门以外穿越城墙 {p}";
                    break;
                }
            }
        }
    }

    private static IEnumerable<string> BlockRules(IKeyReader r)
    {
        var blocks = r.Get(Keys.Blocks)!;
        var roads = r.Get(Keys.Roads)!;
        if (blocks.Developable.Subtract(r.Get(Keys.Wall)!.Interior).Area >= Tolerance)
            yield return "可建区超出城墙";
        if (blocks.Developable.Intersect(roads.Footprint).Area >= Tolerance)
            yield return "可建区压到道路";
    }

    private static IEnumerable<string> ParcelRules(IKeyReader r)
    {
        var developable = r.Get(Keys.Blocks)!.Developable;
        var footprint = r.Get(Keys.Roads)!.Footprint;
        var regions = r.Get(Keys.Parcels)!.Parcels.Select(p => Region.Polygon(p.Ring)).ToList();
        for (int i = 0; i < regions.Count; i++)
        {
            if (regions[i].Subtract(developable).Area >= Tolerance)
                yield return $"地块 {i} 超出可建区";
            if (regions[i].Intersect(footprint).Area >= Tolerance)
                yield return $"地块 {i} 压到道路";
        }

        foreach (var (a, b) in Overlaps(regions))
            yield return $"地块 {a} 与 {b} 重叠";
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
