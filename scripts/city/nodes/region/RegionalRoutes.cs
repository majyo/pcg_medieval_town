using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace PcgCitySandbox.City;

public sealed class RegionalRoutesOptions
{
    [Param("入口数", 2, 5,
        Hint = "图边入口的个数，道路数随之变化")]
    public int EntryCount { get; set; } = 4;
    [Param("入口最小间距 m", 200, 1500, 50,
        Hint = "入口之间至少相隔多远。调大后入口分布到更多条边上")]
    public float EntryGap { get; set; } = 600;
    [Param("河谷入口加分", 0, 1, 0.05,
        Hint = "靠近河谷的入口加多少分。调大后入口更集中在河流出入地图的地方")]
    public float ValleyBonus { get; set; } = 0.3f;
    [Param("过河代价倍数", 1, 10, 0.5,
        Hint = "走渡口时每米相当于平地几米。调大后道路尽量少过河")]
    public float CrossingCost { get; set; } = 3;
    [Param("免费坡度 %", 2, 15, 0.5,
        Hint = "道路沿行进方向的坡度在它以内不加价")]
    public float FreeGrade { get; set; } = 6;
    [Param("超坡惩罚", 0, 5, 0.1,
        Hint = "超过免费坡度后，按超出量的平方乘这个系数加价。调大后道路更绕、更斜着爬坡")]
    public float GradePenalty { get; set; } = 1;
    [Param("种子", 0, 99999,
        Hint = "只影响入口的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;
}

/// <summary>
/// The roads that cross the country before any town: entries on the map edges, joined by least-cost routes (the
/// river only at crossings). Entries face easy country inland, keep apart, and include at least one on each bank of
/// the river; entries on the north and south edges near the river (where the valley leaves the map) are preferred.
/// The trunk runs between the two entries farthest apart; every other entry then joins the network where that is
/// cheapest, cheapest entry first, so junctions appear where branches meet the trunk.
/// </summary>
public sealed class RegionalRoutes : CityNode<RegionalRoutesOptions>
{
    private const float EdgeMargin = 60, EntryStep = 20, InlandReach = 80, SameEdgePenalty = 0.3f, BankShare = 0.05f;

    private static readonly string[] EdgeNames = { "北", "南", "西", "东" };

    public override string Group => RegionalFrame.Group;
    public override string Title => "过境道路";
    public override string Description =>
        "在图边选出入口，按通行代价修出连接它们的过境道路：先修相距最远的两个入口之间的干线，其余入口再接到路网上。";
    public override IReadOnlyList<IKey> Reads => [Keys.TravelCost, Keys.Height, Keys.Shore, Keys.Crossings];
    public override IReadOnlyList<IKey> Writes => [Keys.Routes];

    protected override void Run(NodeContext c, RegionalRoutesOptions o)
    {
        var travel = c.Get(Keys.TravelCost);
        var shore = c.Get(Keys.Shore);
        var crossings = c.Get(Keys.Crossings).Crossings;
        var grid = travel.Grid;
        var costs = new RouteCosts(travel, c.Get(Keys.Height), crossings, o.CrossingCost, o.FreeGrade / 100, o.GradePenalty);
        var banks = Banks(grid, travel);
        var entries = ChooseEntries(grid, travel, shore, banks, o);
        if (entries.Count < 2)
            throw new InvalidOperationException("图边找不到两个可用的道路入口");

        // The trunk: a through route between the two entries farthest apart that can reach each other.
        var routes = new List<Route>();
        var onNetwork = new bool[grid.Count];
        void Add(MapEntry from, string to, List<int> path)
        {
            path = RouteSearch.Straighten(costs, path, out var corners);
            foreach (int k in path)
                onNetwork[k] = true;
            var onPath = path.ToHashSet();
            var used = crossings.Where(x => x.Cells.Any(k => travel.IsNoData(travel.Values[k]) && onPath.Contains(k)))
                .Select(x => x.Id).ToList();
            // Lines between the corners; runs the straightening kept cell by cell lose their one-cell jogs.
            var points = Geometry.Simplify(corners.Select(k => grid.Center(k)).ToList(), grid.CellSize * 0.75f);
            routes.Add(new Route($"route_{routes.Count + 1}", from.Id, to, path.ToArray(), points, used));
        }

        var pairs = from a in entries
                    from b in entries
                    where string.CompareOrdinal(a.Id, b.Id) < 0
                    orderby a.Position.DistanceTo(b.Position) descending
                    select (a, b);
        var connected = new List<MapEntry>();
        foreach (var (a, b) in pairs)
        {
            var path = RouteSearch.Find(costs, a.Cell, b.Cell, c.Cancellation);
            if (path == null)
                continue;
            Add(a, b.Id, path);
            connected.AddRange(new[] { a, b });
            break;
        }

        if (routes.Count == 0)
            throw new InvalidOperationException("任意两个入口之间都找不到道路");

        // Branches: the entry cheapest to join goes next, by its cheapest path to any cell of the network so far.
        var waiting = entries.Except(connected).ToList();
        while (waiting.Count > 0)
        {
            var found = new (List<int>? Path, float Cost)[waiting.Count];
            Parallel.For(0, waiting.Count, new ParallelOptions { CancellationToken = c.Cancellation },
                i => found[i] = RouteSearch.FindToAny(costs, waiting[i].Cell, onNetwork, c.Cancellation));
            int next = Enumerable.Range(0, waiting.Count).Where(i => found[i].Path != null).DefaultIfEmpty(-1)
                .MinBy(i => i < 0 ? 0 : found[i].Cost);
            if (next < 0)
            {
                c.Notice($"{string.Join("、", waiting.Select(e => e.Id))} 无法接入路网（河上没有可用的渡口？）");
                break;
            }

            var branch = found[next].Path!;
            int join = branch[^1];
            Add(waiting[next], routes.First(route => route.Cells.Contains(join)).Id, branch);
            waiting.RemoveAt(next);
        }

        c.Set(Keys.Routes, new RouteNetwork(grid, entries, routes));
    }

    /// <summary>
    /// Which bank each cell is on: the large connected areas of passable land (4-neighbors, no crossings), numbered
    /// from the largest; -1 for impassable cells and small islands.
    /// </summary>
    private static int[] Banks(SiteGrid grid, ScalarField travel)
    {
        var label = new int[grid.Count];
        Array.Fill(label, -2);
        var sizes = new List<int>();
        var queue = new Queue<int>();
        for (int start = 0; start < grid.Count; start++)
        {
            if (label[start] != -2)
                continue;
            if (travel.IsNoData(travel.Values[start]))
            {
                label[start] = -1;
                continue;
            }

            int id = sizes.Count, size = 0;
            label[start] = id;
            queue.Enqueue(start);
            while (queue.TryDequeue(out int k))
            {
                size++;
                int x = k % grid.Nx, z = k / grid.Nx;
                foreach (var (jx, jz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                {
                    if (!grid.InGrid(jx, jz))
                        continue;
                    int j = grid.Index(jx, jz);
                    if (label[j] != -2)
                        continue;
                    if (travel.IsNoData(travel.Values[j]))
                    {
                        label[j] = -1;
                        continue;
                    }

                    label[j] = id;
                    queue.Enqueue(j);
                }
            }

            sizes.Add(size);
        }

        var rank = Enumerable.Range(0, sizes.Count).OrderByDescending(i => sizes[i]).ToList();
        var bank = new int[sizes.Count];
        for (int i = 0; i < sizes.Count; i++)
            bank[i] = sizes[i] >= grid.Count * BankShare ? rank.IndexOf(i) : -1;
        for (int k = 0; k < label.Length; k++)
            label[k] = label[k] < 0 ? -1 : bank[label[k]];
        return label;
    }

    private List<MapEntry> ChooseEntries(SiteGrid grid, ScalarField travel, ScalarField shore, int[] banks, RegionalRoutesOptions o)
    {
        var candidates = new List<(int Cell, int Edge, int Bank, float Score)>();
        int step = Math.Max(1, (int)MathF.Round(EntryStep / grid.CellSize));
        int margin = (int)MathF.Ceiling(EdgeMargin / grid.CellSize);
        for (int edge = 0; edge < 4; edge++)
        {
            bool horizontal = edge < 2;
            int length = horizontal ? grid.Nx : grid.Nz;
            for (int i = margin; i < length - margin; i += step)
            {
                var (x, z) = edge switch { 0 => (i, 0), 1 => (i, grid.Nz - 1), 2 => (0, i), _ => (grid.Nx - 1, i) };
                var (dx, dz) = edge switch { 0 => (0, 1), 1 => (0, -1), 2 => (1, 0), _ => (-1, 0) };
                int k = grid.Index(x, z);
                if (banks[k] < 0)
                    continue;
                float total = 0;
                int count = 0;
                bool open = true;
                for (int t = 0; t * grid.CellSize <= InlandReach; t++)
                {
                    float v = travel.Values[grid.Index(x + dx * t, z + dz * t)];
                    if (travel.IsNoData(v))
                    {
                        open = false;
                        break;
                    }

                    total += v;
                    count++;
                }

                if (!open)
                    continue;
                float score = count / total;
                if (horizontal && shore.Values[k] >= 30)
                    score += o.ValleyBonus * (1 - Mathf.SmoothStep(100, 500, shore.Values[k]));
                score += Geometry.Hash(k, o.Seed) * 0.1f;
                candidates.Add((k, edge, banks[k], score));
            }
        }

        var bankList = candidates.Select(x => x.Bank).Distinct().OrderBy(b => b).ToList();
        var picked = new List<(int Cell, int Edge, int Bank, float Score)>();
        for (int n = 0; n < o.EntryCount; n++)
        {
            var uncovered = bankList.Where(b => picked.All(p => p.Bank != b)).ToList();
            bool mustCover = uncovered.Count > 0 && uncovered.Count >= o.EntryCount - n;
            var options = candidates
                .Where(x => picked.All(p => grid.Center(p.Cell).DistanceTo(grid.Center(x.Cell)) >= o.EntryGap))
                .Where(x => !mustCover || uncovered.Contains(x.Bank))
                .ToList();
            if (options.Count == 0)
                break;
            picked.Add(options.MaxBy(x => x.Score - (picked.Any(p => p.Edge == x.Edge) ? SameEdgePenalty : 0)));
        }

        return picked.Select((p, i) => new MapEntry($"entry_{i + 1}", grid.Center(p.Cell), p.Cell, EdgeNames[p.Edge], p.Bank))
            .ToList();
    }
}
