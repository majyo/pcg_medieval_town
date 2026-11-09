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
        new(Keys.Aspect, r => FieldRules(r, Keys.Aspect).Concat(AspectRules(r))),
        new(Keys.Shore, r => FieldRules(r, Keys.Shore).Concat(ShoreRules(r))),
        new(Keys.AboveWater, r => FieldRules(r, Keys.AboveWater).Concat(AboveWaterRules(r))),
        new(Keys.Relief, r => FieldRules(r, Keys.Relief).Concat(ReliefRules(r))),
        new(Keys.Forest, r => FieldRules(r, Keys.Forest).Concat(Range(r, Keys.Forest, 0, 1)).Concat(ForestRules(r))),
        new(Keys.Site, SiteRules),
        new(Keys.Core, CoreRules),
        new(Keys.Plaza, PlazaRules),
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

    /// <summary>Every value (no-data cells excepted) lies in [<paramref name="min"/>, <paramref name="max"/>].</summary>
    private static IEnumerable<string> Range(IKeyReader r, Key<ScalarField> key, float min, float max)
    {
        var field = r.Get(key)!;
        if (field.Values.Any(v => !field.IsNoData(v) && (v < min || v > max)))
            yield return $"取值超出 [{min}, {max}]：{field.Min}–{field.Max}";
    }

    private static IEnumerable<string> AspectRules(IKeyReader r)
    {
        var aspect = r.Get(Keys.Aspect)!;
        if (aspect.Values.Any(v => !aspect.IsNoData(v) && v is < 0 or >= 360))
            yield return $"坡向超出 [0, 360)：{aspect.Min}–{aspect.Max}";
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

    private static IEnumerable<string> SiteRules(IKeyReader r)
    {
        var site = r.Get(Keys.Site)!;
        if (site.BuildableFraction is <= 0.05f or >= 0.95f)
            yield return $"场地可建设比例异常 {site.BuildableFraction:P0}";
    }

    private static IEnumerable<string> CoreRules(IKeyReader r)
    {
        var site = r.Get(Keys.Site)!;
        if (!site.Buildable(site.CellAt(r.Get(Keys.Core)!.Position)))
            yield return "城址不在可建设格";
    }

    private static IEnumerable<string> PlazaRules(IKeyReader r)
    {
        if (r.Get(Keys.Plaza)!.Area.Subtract(r.Get(Keys.Wall)!.Interior).Area >= 1)
            yield return "广场超出城墙";
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
