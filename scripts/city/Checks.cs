using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

/// <summary>
/// Headless check: runs the full chain for fixed seeds, asserts each stage's invariants and determinism.
/// Run with `tools/project.ps1 test`. Exit code 0 means every check passed.
/// </summary>
public static class Checks
{
    private static readonly int[] Seeds = { 73129, 7, 22017 };
    private const double Tolerance = 0.5;

    /// <param name="createTerrain">Builds the terrain for a seed; the checks never see how.</param>
    public static async Task<int> Run(Func<int, ITerrain> createTerrain)
    {
        var failures = new List<string>();
        string? firstFingerprint = null;
        foreach (int seed in Seeds)
        {
            var pipeline = TownPipeline.Create(createTerrain(seed));
            await pipeline.Run();
            for (int i = 0; i < pipeline.Stages.Count; i++)
            {
                var result = pipeline.Result(i) as IStageResult;
                string line = result == null
                    ? $"FAILED {pipeline.Error(i)}"
                    : $"{result.Summary}  ({pipeline.Seconds(i):0.00} s)";
                GD.Print($"seed={seed} {pipeline.Stages[i].Title}: {line}");
                if (result == null)
                    failures.Add($"seed={seed} {pipeline.Stages[i].Title}: {pipeline.Error(i)}");
                foreach (var notice in result?.Notices ?? Array.Empty<string>())
                    GD.Print($"    notice: {notice}");
            }

            if (pipeline.Get<BuildingSet>() == null)
                continue;

            var errors = Invariants(pipeline);
            failures.AddRange(errors.Select(e => $"seed={seed} {e}"));
            if (seed == Seeds[0])
                firstFingerprint = Fingerprint(pipeline);
        }

        var again = TownPipeline.Create(createTerrain(Seeds[0]));
        await again.Run();
        if (firstFingerprint == null || Fingerprint(again) != firstFingerprint)
            failures.Add("同一种子两次生成结果不一致");

        foreach (var failure in failures)
            GD.PrintErr("CHECK FAILED: " + failure);
        GD.Print(failures.Count == 0 ? "CHECKS PASSED" : $"CHECKS FAILED ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }

    public static List<string> Invariants(Pipeline pipeline)
    {
        var site = pipeline.Get<Site>()!;
        var layout = pipeline.Get<TownLayout>()!;
        var roads = pipeline.Get<RoadGraph>()!;
        var parcels = pipeline.Get<ParcelSet>()!;
        var buildings = pipeline.Get<BuildingSet>()!;
        var errors = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition)
                errors.Add(message);
        }

        Require(site.BuildableFraction is > 0.05f and < 0.95f, $"场地可建设比例异常 {site.BuildableFraction:P0}");

        Require(site.Buildable(site.CellAt(layout.Core)), "核心不在可建设格");
        Require(layout.Plaza.Subtract(layout.Interior).Area < 1, "广场超出城墙");
        Require(layout.Gates.Count > 0, "没有城门");
        Require(layout.Gates.All(g => layout.Wall.Contains(g.Position)), "城门不在城墙上");

        var gateCells = layout.Gates.Select(g => g.Position).ToList();
        Require(roads.Edges.All(e => e.From < roads.Nodes.Count && e.To < roads.Nodes.Count), "路段引用了不存在的节点");
        Require(roads.Edges.All(e => e.Length > 0.01f), "存在零长度路段");
        Require(roads.Nodes.Count(n => n.Kind == "gate") == layout.Gates.Count, "城门未全部接入路网");
        Require(Reachable(roads).Count == roads.Nodes.Count, "路网不连通");
        foreach (var edge in roads.Edges)
        {
            foreach (var p in Samples(edge.Points, 2))
            {
                int k = site.CellAt(p);
                bool nearGate = gateCells.Any(g => g.DistanceTo(p) < site.CellSize * 1.5f);
                if (k < 0 || site.Block[k] == SiteBlock.Water)
                {
                    errors.Add($"路段 {edge.Id} 穿过水域 {p}");
                    break;
                }

                if (!nearGate && layout.Interior.Contains(p) != (edge.Kind != RoadKind.Outer))
                {
                    errors.Add($"路段 {edge.Id}（{edge.Kind}）在城门以外穿越城墙 {p}");
                    break;
                }
            }
        }

        var parcelRegions = parcels.Parcels.Select(p => Region.Polygon(p.Ring)).ToList();
        for (int i = 0; i < parcelRegions.Count; i++)
        {
            var region = parcelRegions[i];
            Require(region.Subtract(parcels.Developable).Area < Tolerance, $"地块 {i} 超出可建区");
            Require(region.Intersect(roads.Footprint).Area < Tolerance, $"地块 {i} 压到道路");
        }

        foreach (var (a, b) in Overlaps(parcelRegions))
            errors.Add($"地块 {a} 与 {b} 重叠");

        var buildingRegions = buildings.Buildings.Select(b => Region.Polygon(b.Corners)).ToList();
        Require(buildingRegions.Count > 0, "没有建筑");
        for (int i = 0; i < buildingRegions.Count; i++)
        {
            var building = buildings.Buildings[i];
            Require(buildingRegions[i].Subtract(parcelRegions[building.ParcelId]).Area < Tolerance, $"建筑 {i} 超出地块");
            Require(building.Eaves > building.Base && building.Ridge > building.Eaves, $"建筑 {i} 高度无效");
        }

        foreach (var (a, b) in Overlaps(buildingRegions))
            errors.Add($"建筑 {a} 与 {b} 重叠");

        return errors.Take(20).ToList();
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

    private static IEnumerable<Vector2> Samples(Vector2[] points, float step)
    {
        for (int i = 1; i < points.Length; i++)
        {
            int count = Math.Max(1, (int)(points[i - 1].DistanceTo(points[i]) / step));
            for (int j = 0; j <= count; j++)
                yield return points[i - 1].Lerp(points[i], j / (float)count);
        }
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

    private static string Fingerprint(Pipeline pipeline)
    {
        var roads = pipeline.Get<RoadGraph>();
        var parcels = pipeline.Get<ParcelSet>();
        var buildings = pipeline.Get<BuildingSet>();
        if (roads == null || parcels == null || buildings == null)
            return "incomplete";
        return string.Join("|",
            pipeline.Get<TownLayout>()!.Core,
            roads.Edges.Count,
            roads.Edges.Sum(e => e.Length).ToString("F2"),
            parcels.Parcels.Count,
            parcels.Parcels.Sum(p => p.Area).ToString("F1"),
            buildings.Buildings.Count);
    }
}
