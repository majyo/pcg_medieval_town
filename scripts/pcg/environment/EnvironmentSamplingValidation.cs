using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PcgCitySandbox.Pcg;

public static class EnvironmentSamplingValidation
{
    private sealed class Fixture : IEnvironmentSource
    {
        public string SourceId => "analytic_fixture";
        public long Revision { get; set; } = 1;
        public Rect2 Bounds { get; set; } = new(0, 0, 24, 17);
        public float? NativeSpacing => null;
        public EnvironmentLayers Layers { get; set; } = new(LayerStatus.Provided, LayerStatus.Provided,
            LayerStatus.Provided, LayerStatus.Provided, LayerStatus.KnownEmpty, LayerStatus.KnownEmpty);
        public IReadOnlyList<ConstraintPolygon> NoBuildZones { get; set; } = Array.Empty<ConstraintPolygon>();
        public IReadOnlyList<ConstraintPolygon> Obstacles { get; set; } = Array.Empty<ConstraintPolygon>();
        public Func<Vector2, EnvironmentPoint> Read { get; set; } = p => new(3 + 2 * p.X - 0.5f * p.Y, 12, false, 0.4f);
        public int Queries { get; private set; }
        public bool Contains(Vector2 p) => ConstraintPolygon.Inside(Bounds, p);
        public EnvironmentPoint Query(Vector2 p)
        {
            if (!Contains(p)) throw new Exception("Query outside source bounds");
            Queries++; return Read(p);
        }
    }

    public static async Task Run(MapSettings mapSettings)
    {
        int checks = 0;
        void Check(bool condition, string message)
        { checks++; if (!condition) throw new InvalidOperationException("SAMPLING_TEST_FAILED: " + message); }
        void Reject(Action action, string message)
        {
            bool threw = false;
            try { action(); } catch (ArgumentException) { threw = true; } catch (InvalidOperationException) { threw = true; }
            Check(threw, message);
        }
        var fixture = new Fixture();
        var options = new EnvironmentSampleOptions(fixture.Bounds);
        var flat = EnvironmentSampler.Sample(fixture, options);
        Check(flat.CellsX == 3 && flat.CellsZ == 2 && flat.Summary.ProbeCount == 18, "non-divisible region and query count");
        Check(flat.Summary.CandidateCells == 6, "slope exactly equal to threshold is allowed");
        foreach (var p in new[] { new Vector2(0, 0), new Vector2(24, 17), new Vector2(3, 11), new Vector2(21, 14) })
            Check(flat.TryHeightAt(p, out float h) && Math.Abs(h - (3 + 2 * p.X - 0.5f * p.Y)) < 0.0001f, "plane reconstruction including max boundary");
        Check(!flat.TryHeightAt(new Vector2(24.1f, 0), out _) && !flat.TryGetCell(new Vector2(-0.1f, 0), out _), "outside queries must not clamp");
        Check(!flat.Contains(new Vector2(float.NaN, 0)), "nonfinite query rejected");
        Reject(() => flat.Cell(flat.CellsX, 0), "cell index cannot wrap to next row");
        Reject(() => flat.VertexHeight(flat.CellsX + 1, 0), "vertex index cannot wrap to next row");
        fixture.Read = p => new(5, 12.01f, false, 1);
        var steep = EnvironmentSampler.Sample(fixture, options);
        Check(steep.Summary.BlockedCells == 6 && steep.Cell(0, 0).Reasons.HasFlag(EnvironmentRestriction.TooSteep), "slope beyond threshold");
        fixture.Bounds = new Rect2(0, 0, 10, 10); options = new(fixture.Bounds);
        fixture.Read = p => new(5, 0, p == new Vector2(5, 5), 1);
        Check(EnvironmentSampler.Sample(fixture, options).Cell(0, 0).Water == true, "center-only water hit");
        fixture.Read = p => new(5, 0, false, 1);
        var polygon = new ConstraintPolygon("thin", new[] { new Vector2(2, -1), new Vector2(2.2f, -1), new Vector2(2.2f, 11), new Vector2(2, 11) });
        fixture.NoBuildZones = new[] { polygon }; fixture.Layers = fixture.Layers with { NoBuild = LayerStatus.Provided };
        Check(EnvironmentSampler.Sample(fixture, options).Cell(0, 0).Reasons.HasFlag(EnvironmentRestriction.NoBuildZone), "thin polygon intersects between sample locations");
        var touch = new ConstraintPolygon("touch", new[] { new Vector2(10, 3), new Vector2(11, 3), new Vector2(11, 4), new Vector2(10, 4) });
        Check(touch.Intersects(fixture.Bounds), "polygon edge contact counts");
        Check(!polygon.Intersects(new Rect2(20, 20, 2, 2)), "disjoint polygons");
        fixture.NoBuildZones = Array.Empty<ConstraintPolygon>();
        fixture.Obstacles = new[] { polygon }; fixture.Layers = fixture.Layers with { NoBuild = LayerStatus.KnownEmpty, Obstacles = LayerStatus.Provided };
        Check(EnvironmentSampler.Sample(fixture, options).Cell(0, 0).Reasons.HasFlag(EnvironmentRestriction.Occupied), "fixed obstacles");
        fixture.Obstacles = Array.Empty<ConstraintPolygon>(); fixture.Layers = fixture.Layers with { Obstacles = LayerStatus.KnownEmpty, NoBuild = LayerStatus.Unavailable };
        Check(EnvironmentSampler.Sample(fixture, options).Cell(0, 0).State == BuildCandidateState.Unknown, "missing constraints are unknown");
        fixture.Layers = fixture.Layers with { NoBuild = LayerStatus.KnownEmpty, Vegetation = LayerStatus.Unavailable };
        var noForest = EnvironmentSampler.Sample(fixture, options);
        Check(noForest.Cell(0, 0).State == BuildCandidateState.Candidate && noForest.Cell(0, 0).Vegetation == null, "optional vegetation does not become zero or block land");
        var partial = EnvironmentSampler.Sample(fixture, options with { Bounds = new Rect2(-5, 0, 15, 10) });
        Check(partial.Cell(0, 0).State == BuildCandidateState.Unknown && !partial.TryHeightAt(new Vector2(-4, 1), out _), "partial coverage remains invalid");
        Reject(() => EnvironmentSampler.Sample(fixture, options with { Bounds = new Rect2(30, 30, 10, 10) }), "entirely outside coverage");
        fixture.Read = p => new(5, p.X == 0 ? null : 0, p.Y == 0 ? null : false, null);
        Check(EnvironmentSampler.Sample(fixture, options).Cell(0, 0).State == BuildCandidateState.Unknown, "local unknown slope/water");
        fixture.Read = p => new(float.NaN, 0, false, null);
        Reject(() => EnvironmentSampler.Sample(fixture, options), "bad source value rejected");
        fixture.Read = p => new(5, 0, false, null);
        Reject(() => EnvironmentSampler.Sample(fixture, options with { TargetSpacing = 0 }), "zero spacing");
        Reject(() => EnvironmentSampler.Sample(fixture, options with { TargetSpacing = 0.00001f }), "query budget");
        Reject(() => EnvironmentSampler.Sample(fixture, options with { Bounds = new Rect2(0, 0, float.NaN, 10) }), "nonfinite bounds");
        Reject(() => (options with { Bounds = new Rect2(1e9f, 1e9f, 10, 10), TargetSpacing = 1 }).ValidateGrid(), "grid below float coordinate precision");
        fixture.Layers = fixture.Layers with { Water = LayerStatus.Unavailable };
        Reject(() => EnvironmentSampler.Sample(fixture, options), "missing required layer");
        fixture.Layers = fixture.Layers with { Water = LayerStatus.KnownEmpty };
        fixture.Read = p => new(5, 0, null, 0.8f);
        fixture.Layers = fixture.Layers with { Vegetation = LayerStatus.Provided };
        var disabledForest = EnvironmentSampler.Sample(fixture, options with { ReadVegetation = false });
        Check(disabledForest.Cell(0, 0).Vegetation == null && disabledForest.Cell(0, 0).State == BuildCandidateState.Candidate,
            "known-empty water and disabled vegetation");

        var node = new EnvironmentSampleNode();
        var first = await node.RunAsync(fixture, options);
        Check(node.IsCurrent(fixture, options), "node publishes valid output");
        Check(!node.IsCurrent(fixture, options with { MaxGroundSlope = 5 }), "parameter change invalidates result");
        await node.RunAsync(fixture, options);
        var old = node.LastResult; long resultRevision = node.ResultRevision;
        using (var cancel = new CancellationTokenSource())
        {
            bool cancelled = false;
            try { await node.RunAsync(fixture, options, new PcgContext(cancel.Token, p => { cancel.Cancel(); return Task.CompletedTask; })); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && node.State == SampleNodeState.Cancelled && ReferenceEquals(old, node.LastResult) && node.ResultRevision == resultRevision,
                "cancel retains only the old preview, never publishes partial output");
            Check(!node.IsCurrent(fixture, options), "cancelled rerun cannot make old result valid");
        }
        bool versionRejected = false;
        try { await node.RunAsync(fixture, options, new PcgContext(default, p => { fixture.Revision++; return Task.CompletedTask; })); }
        catch (InvalidOperationException) { versionRejected = true; }
        Check(versionRejected && node.State == SampleNodeState.Failed && ReferenceEquals(old, node.LastResult), "source changes during run rejected");
        await node.RunAsync(fixture, options);
        fixture.Revision++; Check(!node.IsCurrent(fixture, options), "external revision invalidates result");
        fixture.Read = p => new(200, 0, false, 0);
        Check(first.TryHeightAt(new Vector2(5, 5), out float oldHeight) && oldHeight == 5, "published snapshot is independent");
        Reject(() => new ConstraintPolygon("bad", new[] { Vector2.Zero, Vector2.One, new Vector2(1, 0), new Vector2(0, 1) }), "self-intersecting rings rejected");

        // The map is prepared by this test host, outside the sampling module.
        var map = new TemperateMapData(mapSettings);
        var source = new TemperateEnvironmentSource(map, "test_map", 1, Array.Empty<ConstraintPolygon>(), Array.Empty<ConstraintPolygon>());
        float before = map.Heights[0]; int treeCount = map.Trees.Count; int mapSeed = map.Settings.Seed;
        var fullOptions = new EnvironmentSampleOptions(source.Bounds);
        var real = EnvironmentSampler.Sample(source, fullOptions);
        var repeat = EnvironmentSampler.Sample(source, fullOptions);
        Check(real.CellsX == 200 && real.CellsZ == 200 && real.Summary.ProbeCount == 80401, "default full-map sampling");
        Check(real.Summary.UnknownCells == 0 && real.Summary.CandidateCells > 0 && real.Summary.BlockedCells > 0, "known fixture has useful land and water");
        Check(map.Heights[0] == before && map.Trees.Count == treeCount && map.Settings.Seed == mapSeed, "sampling preserves external map");
        bool equal = true;
        for (int z = 0; z <= real.CellsZ; z++) for (int x = 0; x <= real.CellsX; x++) equal &= real.VertexHeight(x, z) == repeat.VertexHeight(x, z);
        for (int z = 0; z < real.CellsZ; z++) for (int x = 0; x < real.CellsX; x++) equal &= real.Cell(x, z) == repeat.Cell(x, z);
        Check(equal, "exact repeat of all output samples and classifications");
        WriteReport("environment_validation.json", new { passed = true, checks, realMap = Describe(real), limitation = "Water uses five point samples per cell, not continuous boundary intersection." });
        GD.Print($"ENVIRONMENT_VALIDATION_PASS checks={checks} cells={real.CellsX * real.CellsZ} queries={real.Summary.SourceQueryCount}");
    }

    public static void RunStandalone(MapSettings settings, EnvironmentSampleOptions options)
    {
        var map = new TemperateMapData(settings); // Test-map preparation is explicitly outside the node.
        var source = new TemperateEnvironmentSource(map, "temperate_test_map", 1, Array.Empty<ConstraintPolygon>(), Array.Empty<ConstraintPolygon>());
        var node = new EnvironmentSampleNode();
        var result = node.RunAsync(source, options).GetAwaiter().GetResult();
        WriteReport("environment_sample.json", Describe(result));
        GD.Print($"ENVIRONMENT_SAMPLE_PASS cells={result.CellsX}x{result.CellsZ} source_queries={result.Summary.SourceQueryCount}");
    }

    private static object Describe(CityEnvironmentData d) => new
    {
        d.SourceId, d.SourceRevision, d.CellsX, d.CellsZ, d.StepX, d.StepZ,
        d.Summary.CandidateCells, d.Summary.BlockedCells, d.Summary.UnknownCells, d.Summary.CompleteCells,
        d.Summary.ProbeCount, d.Summary.SourceQueryCount, d.Summary.ElapsedSeconds,
        d.Summary.MinHeight, d.Summary.MaxHeight,
        reasons = d.Summary.ReasonCounts.ToDictionary(p => p.Key.ToString(), p => p.Value)
    };
    private static void WriteReport(string file, object report)
    {
        string dir = ProjectSettings.GlobalizePath("res://artifacts"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
