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
/// <see cref="Sweep"/> runs the contracts alone over many seeds (`tools/project.ps1 sweep`).
/// </summary>
public static class Checks
{
    private static readonly int[] Seeds = { 73129, 7, 22017 };

    /// <param name="createTerrain">Builds the terrain for a seed; the checks never see how.</param>
    public static async Task<int> Run(Func<int, ITerrain> createTerrain)
    {
        var failures = new List<string>();
        failures.AddRange(Wiring(createTerrain(Seeds[0])));
        failures.AddRange(Documentation(TownGraph.Create(createTerrain(Seeds[0]))));
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

    /// <summary>
    /// Generates the town for seeds 1 to <paramref name="count"/> and checks every key's contract on each: the wide,
    /// shallow counterpart of <see cref="Run"/>, for rules and parameters tuned on the three fixed seeds. Prints one row
    /// of measurements per seed, then each seed's failures, then the spread of every measurement. Returns 0 when no
    /// node failed and no contract broke.
    /// </summary>
    public static async Task<int> Sweep(Func<int, ITerrain> createTerrain, int count)
    {
        var columns = new (string Title, Func<CityGraph, double?> Value, string Format)[]
        {
            ("城内 ha", g => g.Get(Keys.Wall)?.Interior.Area / 10000, "0.0"),
            ("墙长比", g => g.Get(Keys.Wall)?.Winding, "0.00"),
            ("围住 %", g => g.Get(Keys.Wall)?.BuiltShare * 100, "0"),
            ("城内不可建 %", UnbuildableInside, "0.0"),
            ("城门", g => g.Get(Keys.Gates)?.Gates.Count, "0"),
            ("建成 ha", g => g.Get(Keys.Growth) is { } growth ? growth.AreaAfter(growth.Latest) / 10000 : null, "0.0"),
            ("最深挖 m", g => g.Get(Keys.Grading)?.MaxCut, "0.0"),
            ("最高填 m", g => g.Get(Keys.Grading)?.MaxFill, "0.0"),
            ("陡坡段 %", g => g.Get(Keys.Grading) is { Corridors.Count: > 0 } plan
                ? plan.SteepLength / plan.Corridors.Sum(c => c.Length) * 100 : null, "0.0"),
            ("挡土墙 m", g => g.Get(Keys.Grading)?.Walls.Sum(w => w.Length), "0"),
            ("挡土墙段", g => g.Get(Keys.Grading)?.Walls.Count, "0"),
            ("外露墙 m", ExposedWalls, "0"),
            ("台地", g => g.Get(Keys.Terraces)?.Terraces.Count(t => t.Kind == TerraceKind.Block), "0"),
            ("超限台阶", g => g.Get(Keys.Terraces)?.Steps.Count(s => s.Steep), "0"),
            ("桥", g => g.Get(Keys.Bridges)?.Bridges.Count, "0"),
            ("地块", g => g.Get(Keys.Parcels)?.Parcels.Count, "0"),
            ("房屋", g => g.Get(Keys.Buildings)?.Buildings.Count, "0"),
            ("无房地块", g => g.Get(Keys.Parcels)?.Parcels.Count - g.Get(Keys.Buildings)?.Buildings.Count, "0"),
            ("提示", g => Enumerable.Range(0, g.Nodes.Count).Sum(i => g.Notices(i).Count), "0"),
            ("耗时 s", g => Enumerable.Range(0, g.Nodes.Count).Sum(g.Seconds), "0.0"),
        };
        // Chinese characters are two columns wide in a terminal; pad by what is shown, not by characters.
        static int Shown(string text) => text.Sum(ch => ch > 0xff ? 2 : 1);
        static string Pad(string text, int width) => new string(' ', Math.Max(0, width - Shown(text))) + text;
        string Row(string first, IEnumerable<string> cells) =>
            Pad(first, 6) + " " + string.Join(" ", cells.Zip(columns, (cell, column) => Pad(cell, Math.Max(7, Shown(column.Title)))));

        GD.Print(Row("种子", columns.Select(c => c.Title)) + "  失败");
        var values = columns.Select(_ => new List<double>()).ToArray();
        var failures = new List<string>();
        int failedSeeds = 0;
        for (int seed = 1; seed <= count; seed++)
        {
            var graph = TownGraph.Create(createTerrain(seed));
            await graph.Run();
            var errors = Enumerable.Range(0, graph.Nodes.Count).Where(i => graph.Status(i) == NodeStatus.Failed)
                .Select(i => $"{graph.Nodes[i].Title}: {graph.Error(i)}").ToList();
            // A failed node leaves its keys and everything downstream missing; the contracts would only repeat that.
            if (errors.Count == 0)
                errors = Invariants(graph);

            var cells = new List<string>();
            for (int c = 0; c < columns.Length; c++)
            {
                double? value = columns[c].Value(graph);
                if (value is { } v)
                    values[c].Add(v);
                cells.Add(value?.ToString(columns[c].Format) ?? "—");
            }

            GD.Print(Row(seed.ToString(), cells) + (errors.Count == 0 ? "" : $"  {errors.Count}"));
            failures.AddRange(errors.Select(e => $"seed={seed} {e}"));
            if (errors.Count > 0)
                failedSeeds++;
        }

        foreach (var (name, pick) in new (string, Func<List<double>, double>)[]
            { ("最小", v => v.Min()), ("中位", v => v.OrderBy(x => x).ElementAt(v.Count / 2)), ("最大", v => v.Max()) })
        {
            GD.Print(Row(name, values.Select((v, c) => v.Count == 0 ? "—" : pick(v).ToString(columns[c].Format))));
        }

        foreach (var failure in failures)
            GD.PrintErr("SWEEP FAILED: " + failure);
        GD.Print(failedSeeds == 0 ? $"SWEEP PASSED ({count} seeds)" : $"SWEEP FAILED ({failedSeeds} of {count} seeds)");
        return failedSeeds == 0 ? 0 : 1;
    }

    /// <summary>
    /// How much retaining wall stands outside the houses, in meters: a wall under a house is its side or back wall.
    /// </summary>
    private static double? ExposedWalls(CityGraph graph)
    {
        if (graph.Get(Keys.Grading) is not { } plan || graph.Get(Keys.Buildings) is not { } buildings)
            return null;
        var houses = buildings.Buildings.Select(b => Region.Polygon(b.Corners).Offset(0.4f)).ToList();
        double exposed = 0;
        foreach (var wall in plan.Walls)
        {
            for (int i = 1; i < wall.Points.Length; i++)
            {
                var m = (wall.Points[i - 1] + wall.Points[i]) / 2;
                if (!houses.Any(h => h.Bounds.HasPoint(m) && h.Contains(m)))
                    exposed += wall.Points[i - 1].DistanceTo(wall.Points[i]);
            }
        }

        return exposed;
    }

    /// <summary>The share of the cells inside the wall that cannot be built on, in percent.</summary>
    private static double? UnbuildableInside(CityGraph graph)
    {
        if (graph.Get(Keys.Wall) is not { } wall || graph.Get(Keys.Buildable) is not { } buildable)
            return null;
        var inside = buildable.Grid.CellsIn(wall.Interior);
        return inside.Count == 0 ? null : inside.Count(k => !buildable.Buildable(k)) * 100.0 / inside.Count;
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

    /// <summary>Everything the interface names explains itself: every node, node parameter and key has a description.</summary>
    private static IEnumerable<string> Documentation(CityGraph graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Description))
                yield return $"节点 {node.Title} 没有说明";
            foreach (var property in node.Options.GetType().GetProperties())
            {
                if (property.GetCustomAttributes(typeof(ParamAttribute), false) is [ParamAttribute param] &&
                    string.IsNullOrWhiteSpace(param.Hint))
                    yield return $"节点 {node.Title} 的参数“{param.Label}”没有说明";
            }
        }

        foreach (var field in typeof(Keys).GetFields())
        {
            if (field.GetValue(null) is IKey key && string.IsNullOrWhiteSpace(key.Description))
                yield return $"键 {key.Name} 没有说明";
        }
    }

    /// <summary>The graph rejects a missing input and a second fresh writer of a key.</summary>
    private static IEnumerable<string> Wiring(ITerrain terrain)
    {
        if (!Throws(() => new CityGraph(terrain, new ChooseOrigin())))
            yield return "读取没有上游的键时没有报错";
        if (!Throws(() => new CityGraph(terrain, new SampleGrid(), new SampleGrid())))
            yield return "两个节点重复写入同一个键时没有报错";
    }

    /// <summary>
    /// Invalidating a node makes exactly it and its downstream stale; rerunning restores the same result. Checked on
    /// nodes at several depths, each with the list of nodes that must stay valid: the road skeleton, the wall lane, the
    /// workshops, the road profiles, the church, the castle, the wall, the habitability score and the sampled heights.
    /// </summary>
    private static async Task<List<string>> Invalidation(CityGraph graph)
    {
        var errors = new List<string>();
        errors.AddRange(await Invalidation<SkeletonLinks>(graph, "骨架道路",
            node => node.Group is NodeGroups.Sampling or NodeGroups.Evaluation or NodeGroups.Regional or NodeGroups.Town
                or NodeGroups.Zoning || node is RoadZoning));
        errors.AddRange(await Invalidation<WallLane>(graph, "墙根巷",
            node => node.Group is NodeGroups.Sampling or NodeGroups.Evaluation or NodeGroups.Regional or NodeGroups.Town
                or NodeGroups.Zoning || node is RoadZoning or SkeletonLinks or PlannedStreets or StreetMesh));
        errors.AddRange(await Invalidation<Workshops>(graph, "作坊",
            node => node is not (Workshops or Countryside or WorkshopLanes or LandmarkAccess or Squares or StreetRanks or ExtractRoads
                or StreetBlocks or FrontageParcels or ParcelKinds or Wells or Bridges or RoadProfiles or Terraces or Platforms
                or GableHouses or Yards)));
        errors.AddRange(await Invalidation<RoadProfiles>(graph, "道路纵断面",
            node => node.Group is NodeGroups.Sampling or NodeGroups.Evaluation or NodeGroups.Regional or NodeGroups.Town
                or NodeGroups.Zoning or NodeGroups.Roads || node is Bridges or Wells));
        errors.AddRange(await Invalidation<ChurchSite>(graph, "教堂",
            node => node.Group is NodeGroups.Sampling or NodeGroups.Evaluation or NodeGroups.Regional ||
                node is Village or MarketPlace));
        errors.AddRange(await Invalidation<CastleSite>(graph, "城堡",
            node => node.Group is NodeGroups.Sampling or NodeGroups.Evaluation or NodeGroups.Regional ||
                node is Village or MarketPlace or ChurchSite or MarketGrowth or EnclosingWall));
        errors.AddRange(await Invalidation<EnclosingWall>(graph, "城墙",
            node => node.Group is NodeGroups.Sampling or NodeGroups.Evaluation or NodeGroups.Regional ||
                node is Village or MarketPlace or ChurchSite or MarketGrowth));
        errors.AddRange(await Invalidation<Habitability>(graph, "宜居度",
            node => node.Group == NodeGroups.Sampling || node is BuildableLand or TravelCost or RiverCrossings or RegionalRoutes));
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
        var routes = graph.Get(Keys.Routes);
        var origin = graph.Get(Keys.Origin);
        var growth = graph.Get(Keys.Growth);
        var plaza = graph.Get(Keys.Plaza);
        var landmarks = graph.Get(Keys.Landmarks);
        if (growth == null || plaza == null || landmarks == null || roads == null || parcels == null || buildings == null
            || buildable == null || routes == null || origin == null
            || fields.Any(f => f == null))
            return "incomplete";
        return string.Join("|",
            string.Join(",", fields.Select(f => f!.Values.Sum(v => (double)v).ToString("F3"))),
            string.Join(",", buildable.Counts),
            string.Join(",", routes.Routes.Select(r => r.Cells.Length)),
            origin.Position,
            string.Join(",", growth.Phases.Select(p => p.Cells)),
            plaza.Area.Area.ToString("F1"),
            string.Join(",", landmarks.Landmarks.Select(l => l.Center)),
            graph.Get(Keys.Wall)!.Interior.Area.ToString("F1"),
            graph.Get(Keys.Gates)!.Gates.Count,
            roads.Edges.Count,
            roads.Edges.Sum(e => e.Length).ToString("F2"),
            parcels.Parcels.Count,
            parcels.Parcels.Sum(p => p.Area).ToString("F1"),
            string.Join(",", parcels.Parcels.GroupBy(p => p.Use).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")),
            string.Join(",", parcels.Parcels.GroupBy(p => p.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")),
            buildings.Buildings.Count,
            graph.Get(Keys.Fixtures)?.Fixtures.Count ?? -1,
            graph.Get(Keys.Yards) is { } yards
                ? $"{yards.Fences.Sum(f => f.Length):F1},{yards.Outbuildings.Count},{yards.Gardens.Sum(g => g.Area.Area):F1}"
                : "",
            string.Join(",", graph.Get(Keys.LandUse)?.Counts ?? Array.Empty<int>()),
            graph.Get(Keys.Grading) is { } grading
                ? $"{grading.MaxCut:F2},{grading.MaxFill:F2},{grading.CutVolume:F0},{grading.FillVolume:F0},{grading.SteepLength:F0},{grading.Platforms.Count},{grading.Walls.Sum(w => w.Length):F0}"
                : "",
            string.Join(",", graph.Get(Keys.Bridges)?.Bridges.Select(b => $"{b.Height:F2}/{b.Length:F0}/{b.Piers.Length}") ?? []),
            graph.Get(Keys.Terraces) is { } terraces
                ? $"{terraces.Terraces.Count},{terraces.Steps.Count},{terraces.Terraces.Sum(t => t.Height):F1},"
                    + $"{graph.Get(Keys.Grading)?.Walls.Count}"
                : "");
    }
}
