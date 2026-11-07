using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace PcgCitySandbox;

public static class MapValidation
{
    public static void Run(MapSettings settings)
    {
        var reports = new List<object>();
        string? firstHash = null;
        foreach (int seed in new[] { settings.Seed, 7, 22017 })
        {
            var cfg = (MapSettings)settings.Duplicate(); cfg.Seed = seed;
            var map = new TemperateMapData(cfg);
            Require(map.WaterFraction > 0.015f && map.WaterFraction < 0.10f, "River must occupy a useful, bounded part of the plain");
            Require(map.ForestFraction > 0.10f && map.ForestFraction < 0.65f, "Woodland and clearings must both be present");
            Require(map.OpenGentleFraction > 0.30f, "Keep a large open, gently sloping area for future city tests");
            Require(map.MaxHeight - map.MinHeight > 15 && map.MaxHeight - map.MinHeight < 90, "Relief should remain at plain scale");
            Require(map.Slopes.Count(s => s < 5) / (float)map.Slopes.Length > 0.80f, "Most land should be gently sloping");
            Require(map.Trees.Count > 500 && map.Trees.Count < 14000, "Forest instances must be populated and bounded");
            for (int i = 0; i < map.Heights.Length; i++)
            {
                Require(float.IsFinite(map.Heights[i]) && float.IsFinite(map.Slopes[i]), "Finite terrain samples");
                Require(map.Suitability[i] >= 0 && map.Suitability[i] <= 1, "Suitability range");
                Require(!map.Water[i] || map.Suitability[i] == 0, "Water must be unbuildable");
                Require(Mathf.Abs(map.Normals[i].Length() - 1) < 0.001f, "Unit surface normals");
            }
            for (int i = 0; i < map.River.Count; i++)
            {
                var r = map.River[i]; var sample = map.Sample(r.Position);
                Require(sample.IsWater && sample.Height < r.WaterHeight - 2, "Continuous submerged channel centerline");
                if (i > 0) Require(r.WaterHeight < map.River[i - 1].WaterHeight, "Downstream water level must descend");
            }
            Require(WaterConnectsBoundaries(map), "Raster water must connect upstream and downstream boundaries");
            foreach (var tree in map.Trees)
            {
                var p = new Vector2(tree.Position.X, tree.Position.Z); var sample = map.Sample(p);
                Require(!sample.IsWater && sample.SlopeDegrees <= 18, "Trees must stay on suitable dry ground");
                Require(Mathf.Abs(tree.Position.Y - sample.Height) < 0.2f, "Trees must be grounded");
            }
            string fingerprint = Fingerprint(map);
            if (firstHash == null) firstHash = fingerprint;
            else Require(fingerprint != firstHash, "Changing the seed must alter the environment");
            var sortedSlopes = map.Slopes.Order().ToArray();
            reports.Add(new { seed, vertices = map.Heights.Length, trees = map.Trees.Count,
                minHeight = map.MinHeight, maxHeight = map.MaxHeight, waterFraction = map.WaterFraction,
                forestFraction = map.ForestFraction, openGentleFraction = map.OpenGentleFraction,
                slope95 = sortedSlopes[(int)(sortedSlopes.Length * 0.95f)], riverLength = map.RiverLength, fingerprint });
            GD.Print($"MAP_TEST seed={seed} PASS trees={map.Trees.Count} open={map.OpenGentleFraction:P1} water={map.WaterFraction:P1}");
        }
        var repeat = new TemperateMapData(settings);
        Require(Fingerprint(repeat) == firstHash, "Same settings and seed must reproduce terrain and trees exactly");
        bool rejected = false;
        try { repeat.Sample(new Vector2(settings.SizeMeters, 0)); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected, "Out-of-map samples must be rejected");
        var bare = (MapSettings)settings.Duplicate(); bare.ForestDensity = 0; bare.GridSegments = 128;
        var bareMap = new TemperateMapData(bare);
        Require(bareMap.Trees.Count == 0 && bareMap.ForestFraction == 0, "Zero density must remove forests");
        string folder = ProjectSettings.GlobalizePath("res://artifacts"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "map_validation.json"), JsonSerializer.Serialize(new
        { passed = true, checks = new[] { "three_seeds", "deterministic_repeat", "river_connectivity", "downstream_gradient", "tree_grounding", "open_plains", "bounds", "zero_forest" }, maps = reports },
            new JsonSerializerOptions { WriteIndented = true }));
        GD.Print("MAP_VALIDATION_PASS seeds=3 deterministic=ok river_connectivity=ok tree_grounding=ok zero_forest=ok");
    }

    private static bool WaterConnectsBoundaries(TemperateMapData map)
    {
        var visited = new bool[map.Water.Length]; var queue = new Queue<int>();
        for (int x = 0; x < map.Side; x++) if (map.Water[x]) { visited[x] = true; queue.Enqueue(x); }
        while (queue.TryDequeue(out int k))
        {
            int x = k % map.Side, z = k / map.Side;
            if (z == map.Side - 1) return true;
            void Visit(int nx, int nz)
            {
                if (nx < 0 || nx >= map.Side || nz < 0 || nz >= map.Side) return;
                int n = map.Index(nx, nz);
                if (!visited[n] && map.Water[n]) { visited[n] = true; queue.Enqueue(n); }
            }
            Visit(x - 1, z); Visit(x + 1, z); Visit(x, z - 1); Visit(x, z + 1);
        }
        return false;
    }

    private static string Fingerprint(TemperateMapData map)
    {
        using var bytes = new MemoryStream();
        using (var w = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
        {
            foreach (float h in map.Heights) w.Write(h);
            foreach (float f in map.Forest) w.Write(f);
            foreach (var t in map.Trees)
            { w.Write(t.Position.X); w.Write(t.Position.Y); w.Write(t.Position.Z); w.Write(t.Scale); w.Write(t.Rotation); w.Write(t.Species); w.Write(t.Tint); }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("MAP_VALIDATION_FAILED: " + message); }
}
