using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

/// <summary>
/// Headless check: runs the town graph for fixed seeds and asserts every key's contract, graph wiring and
/// invalidation, and determinism. Run with `tools/project.ps1 test`. Exit code 0 means every check passed.
/// </summary>
public static class Checks
{
    private static readonly int[] Seeds = { 73129, 7, 22017 };

    /// <param name="createTerrain">Builds the terrain for a seed; the checks never see how.</param>
    public static async Task<int> Run(Func<int, ITerrain> createTerrain)
    {
        var failures = new List<string>();
        failures.AddRange(Wiring(createTerrain(Seeds[0])));
        string? firstFingerprint = null;
        foreach (int seed in Seeds)
        {
            var graph = TownGraph.Create(createTerrain(seed));
            await graph.Run();
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                var node = graph.Nodes[i];
                bool valid = graph.Status(i) == NodeStatus.Valid;
                string line = valid ? $"{graph.Summary(i)}  ({graph.Seconds(i):0.00} s)" : $"{graph.Status(i)} {graph.Error(i)}";
                GD.Print($"seed={seed} {node.Group}/{node.Title}: {line}");
                if (!valid)
                    failures.Add($"seed={seed} {node.Title}: {graph.Status(i)} {graph.Error(i)}");
                foreach (var notice in graph.Notices(i))
                    GD.Print($"    notice: {notice}");
            }

            if (graph.Get(Keys.Buildings) == null)
                continue;

            GD.Print($"seed={seed} fingerprint: {Fingerprint(graph)}");
            failures.AddRange(Invariants(graph).Select(e => $"seed={seed} {e}"));
            failures.AddRange(ContourRules(graph).Select(e => $"seed={seed} {e}"));
            failures.AddRange((await Invalidation(graph)).Select(e => $"seed={seed} {e}"));
            if (seed == Seeds[0])
                firstFingerprint = Fingerprint(graph);
        }

        var again = TownGraph.Create(createTerrain(Seeds[0]));
        await again.Run();
        if (firstFingerprint == null || Fingerprint(again) != firstFingerprint)
            failures.Add("同一种子两次生成结果不一致");

        foreach (var failure in failures)
            GD.PrintErr("CHECK FAILED: " + failure);
        GD.Print(failures.Count == 0 ? "CHECKS PASSED" : $"CHECKS FAILED ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Every key contract, plus: no node changed a value it read (summaries still match what was written).</summary>
    public static List<string> Invariants(CityGraph graph)
    {
        var errors = new List<string>();
        foreach (var invariant in Contracts.All)
        {
            if (!graph.Has(invariant.Key))
                errors.Add($"缺少 {invariant.Key.Name}");
            else
                errors.AddRange(invariant.Check(graph).Select(e => $"{invariant.Key.Name}: {e}"));
        }

        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            if (graph.Summary(i) != graph.SummaryWhenRun(i))
                errors.Add($"{graph.Nodes[i].Title} 的输出在写入后被修改");
        }

        return errors.Take(20).ToList();
    }

    /// <summary>Every contour point of the sampled heights lies at its level when the field is sampled there.</summary>
    private static IEnumerable<string> ContourRules(CityGraph graph)
    {
        var height = graph.Get(Keys.Height)!;
        var segments = Contours.Extract(height, 2);
        GD.Print($"    contours: {segments.Count} 段（2 m 间距）");
        if (segments.Count == 0)
            yield return "等高线为空";
        foreach (var s in segments)
        {
            float a = height.Sample(s.A), b = height.Sample(s.B);
            if (MathF.Abs(a - s.Level) > 0.01f || MathF.Abs(b - s.Level) > 0.01f)
            {
                yield return $"等高线 {s.Level} m 的端点 {s.A}–{s.B} 处高程为 {a:0.000} / {b:0.000} m";
                yield break;
            }
        }
    }

    /// <summary>The graph rejects a missing input and a second fresh writer of a key.</summary>
    private static IEnumerable<string> Wiring(ITerrain terrain)
    {
        if (!Throws(() => new CityGraph(terrain, new ChooseCore())))
            yield return "读取没有上游的键时没有报错";
        if (!Throws(() => new CityGraph(terrain, new SampleGrid(), new SampleGrid())))
            yield return "两个节点重复写入同一个键时没有报错";
    }

    /// <summary>
    /// Invalidating a node makes exactly it and its downstream stale; rerunning restores the same result.
    /// Checked on the outer roads node (sampling, evaluation, layout, zoning and main streets stay valid), on the
    /// habitability score (the sampled fields and the other evaluations stay valid, the town goes stale) and on the
    /// sampled heights (only the grid and the forest, which do not depend on them, stay valid).
    /// </summary>
    private static async Task<List<string>> Invalidation(CityGraph graph)
    {
        var errors = new List<string>();
        errors.AddRange(await Invalidation<OuterRoads>(graph, "城外道路", node => node.Group is Sampling.Group or Evaluation.Group ||
            node is ChooseCore or SquarePlaza or StarWall or PlaceGates or RoadZoning or MainStreets));
        errors.AddRange(await Invalidation<Habitability>(graph, "宜居度",
            node => node.Group == Sampling.Group || node is BuildableLand or TravelCost));
        errors.AddRange(await Invalidation<SampleHeight>(graph, "高程", node => node is SampleGrid or SampleForest));
        return errors;
    }

    private static async Task<List<string>> Invalidation<T>(CityGraph graph, string title, Func<CityNode, bool> staysValid)
        where T : CityNode
    {
        var errors = new List<string>();
        string before = Fingerprint(graph);
        graph.Invalidate(Index<T>(graph));
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            if (staysValid(graph.Nodes[i]) != (graph.Status(i) == NodeStatus.Valid))
                errors.Add($"使 {title} 过期后，{graph.Nodes[i].Title} 的状态为 {graph.Status(i)}");
        }

        await graph.Run();
        if (Fingerprint(graph) != before)
            errors.Add($"{title} 局部重新生成的结果与完整生成不同");
        return errors;
    }

    private static int Index<T>(CityGraph graph) where T : CityNode
    {
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            if (graph.Nodes[i] is T)
                return i;
        }

        throw new InvalidOperationException($"图中没有 {typeof(T).Name}");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string Fingerprint(CityGraph graph)
    {
        var roads = graph.Get(Keys.Roads);
        var parcels = graph.Get(Keys.Parcels);
        var buildings = graph.Get(Keys.Buildings);
        var fields = Keys.Fields.Concat(Keys.Scores).Select(graph.Get).ToArray();
        var buildable = graph.Get(Keys.Buildable);
        if (roads == null || parcels == null || buildings == null || buildable == null || fields.Any(f => f == null))
            return "incomplete";
        return string.Join("|",
            string.Join(",", fields.Select(f => f!.Values.Sum(v => (double)v).ToString("F3"))),
            string.Join(",", buildable.Counts),
            graph.Get(Keys.Core)!.Position,
            roads.Edges.Count,
            roads.Edges.Sum(e => e.Length).ToString("F2"),
            parcels.Parcels.Count,
            parcels.Parcels.Sum(p => p.Area).ToString("F1"),
            buildings.Buildings.Count);
    }
}
