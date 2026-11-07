using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace PcgCitySandbox;

public static class TerrainVisualValidation
{
    public static string Fingerprint(TemperateMapData map)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        foreach (var field in new[] { map.Heights, map.Slopes, map.Forest, map.Suitability, map.RiverDistances }) foreach (float v in field) writer.Write(v);
        foreach (bool water in map.Water) writer.Write(water);
        foreach (var normal in map.Normals) { writer.Write(normal.X); writer.Write(normal.Y); writer.Write(normal.Z); }
        foreach (var r in map.River) { writer.Write(r.Position.X); writer.Write(r.Position.Y); writer.Write(r.WaterHeight); writer.Write(r.HalfWidth); }
        foreach (var t in map.Trees) { writer.Write(t.Position.X); writer.Write(t.Position.Y); writer.Write(t.Position.Z); writer.Write(t.Scale); writer.Write(t.Rotation); writer.Write(t.Species); writer.Write(t.Tint); }
        writer.Flush(); return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    public static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("TERRAIN_VISUAL_FAILED: " + message); }

    public static void Run(MapSettings defaults)
    {
        var reports = new List<object>();
        foreach (var variant in new[] { (defaults.Seed, 48f, .76f), (7, 20f, 0f), (22017, 100f, .76f) })
        {
            using var settings = (MapSettings)defaults.Duplicate(); settings.Seed = variant.Item1; settings.RiverWidthMeters = variant.Item2; settings.ForestDensity = variant.Item3;
            var map = new TemperateMapData(settings); string source = Fingerprint(map);
            using var visual = new TerrainVisualData(map); var mesh = new RiverVisualMesh(map); var spatial = new VisualRiverIndex(map);
            Require(source == Fingerprint(map), "render data must not change any environment field, river or tree");
            Require(visual.RiverValues.All(float.IsFinite), "finite river field");
            Require(visual.ControlHasMipmaps, "filtered source control image (headless has a dummy GPU)");
            if (variant.Item3 == 0) for (int i = 0; i < visual.NaturalBytes.Length; i += 4) Require(visual.NaturalBytes[i] == 0, "zero forest has no forest-floor mask");
            float maxRiverError = 0;
            for (int i = 0; i < map.River.Count; i += 3)
            {
                var r = map.River[i]; var tangent = (map.River[Math.Min(i + 1, map.River.Count - 1)].Position - map.River[Math.Max(i - 1, 0)].Position).Normalized();
                var across = new Vector2(tangent.Y, -tangent.X);
                foreach (float fraction in new[] { -1.5f, -.5f, 0, .5f, 1.5f })
                {
                    var p = (r.Position + across * r.HalfWidth * fraction).Clamp(Vector2.One * -map.Half, Vector2.One * map.Half);
                    var expected = map.NearestRiver(p); var actual = spatial.Query(p);
                    Require(actual.Found && Math.Abs(expected.Distance - actual.Distance) < .0001f && Math.Abs(expected.WaterHeight - actual.WaterHeight) < .0001f, "river acceleration agrees with source near both banks");
                }
            }
            for (int i = 0; i < mesh.Positions.Length; i++)
            {
                var v = mesh.Positions[i]; var p = new Vector2(v.X, v.Z); Require(v.IsFinite() && map.Contains(p), "water vertices stay within map");
                Require(Math.Abs(mesh.FlowDirections[i].Length() - 1) < .001f && mesh.FlowDirections[i].Y > 0, "flow goes downstream through bends");
                float error = Math.Abs(v.Y - .015f - map.NearestRiver(p).WaterHeight); maxRiverError = Math.Max(error, maxRiverError);
                Require(error < .0001f, "water elevations track local rather than nominal water level");
                if (i >= RiverVisualMesh.Columns + 1) Require(mesh.FlowCoordinates[i].Y > mesh.FlowCoordinates[i - RiverVisualMesh.Columns - 1].Y, "continuous increasing flow coordinate");
                int col = i % (RiverVisualMesh.Columns + 1);
                if (col is 0 or RiverVisualMesh.Columns && Math.Abs(p.Y) < map.Half - 1)
                    Require(map.HeightAt(p) >= v.Y, "water ribbon ends beneath dry banks");
            }
            float minArea = float.MaxValue;
            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                var a = mesh.Positions[mesh.Indices[i]]; var b = mesh.Positions[mesh.Indices[i + 1]]; var c = mesh.Positions[mesh.Indices[i + 2]];
                var cross = (b - a).Cross(c - a); minArea = Math.Min(minArea, cross.Length() * .5f);
                Require(cross.Y < -.000001f, "water triangles have nonzero area and consistent clockwise winding");
            }
            // A repeated build must reproduce control fields, including river-width/zero-forest edge cases.
            using var repeated = new TerrainVisualData(map);
            Require(visual.NaturalBytes.SequenceEqual(repeated.NaturalBytes) && visual.RiverValues.SequenceEqual(repeated.RiverValues), "deterministic control maps");
            reports.Add(new { seed = variant.Item1, riverWidth = variant.Item2, forestDensity = variant.Item3, source, maxRiverError, minWaterTriangleArea = minArea, triangles = mesh.Indices.Length / 3, controlBuildMs = visual.BuildMilliseconds, controlTextureBytes = visual.TextureBytes });
            GD.Print($"TERRAIN_VISUAL_TEST PASS seed={variant.Item1} river_width={variant.Item2} forest={variant.Item3}");
        }
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/terrain_visual_validation.json"), JsonSerializer.Serialize(new { passed = true, maps = reports }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print("TERRAIN_VISUAL_VALIDATION_PASS immutable=ok deterministic=ok banks=ok flow=ok boundaries=ok");
    }
}
