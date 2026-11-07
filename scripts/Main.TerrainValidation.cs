using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PcgCitySandbox;

public partial class Main
{
    private async Task VerifyTerrainVisualInterface()
    {
        void Check(bool ok, string message) => TerrainVisualValidation.Require(ok, message);
        async Task Frames(int count = 3) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        await RunSamplingAsync(); await RunSettlementAsync(); await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync(); await RunBuildingsAsync(); await RunPlacementAsync();
        Check(CurrentPlacement != null, "full pipeline prerequisite");
        // Inspect the GPU image; Texture2D's new virtual HasMipmaps binding is not reliable on this build.
        using (var uploaded = _map.VisualData.Natural.GetImage()) Check(uploaded.HasMipmaps(), "uploaded control image retains mipmaps");
        object?[] Results() => new object?[] { SampledEnvironment, CurrentSettlement, CurrentRoads, CurrentParcels, CurrentBuildings, CurrentPlacement };
        var results = Results(); string original = TerrainVisualValidation.Fingerprint(_data);
        var ground = _map.GetNode<MeshInstance3D>("Terrain"); var natural = ground.Mesh;
        for (int quality = 0; quality < 3; quality++)
        {
            _terrainQuality.Select(quality); _terrainQuality.EmitSignal(OptionButton.SignalName.ItemSelected, (long)quality); await Frames();
            Check(_map.VisualQuality == (TerrainVisualQuality)quality && ReferenceEquals(natural, ground.Mesh), "quality callback reuses terrain mesh");
            Check(results.Zip(Results()).All(pair => ReferenceEquals(pair.First, pair.Second)), "quality does not invalidate or rerun any PCG node");
        }
        using var changed = (TerrainVisualSettings)GD.Load<TerrainVisualSettings>("res://configs/terrain_visual.tres").Duplicate(); changed.BankWidth = 1.5f; changed.MacroVariation = .12f;
        _map.ApplyVisualSettings(changed); await Frames(); _map.ApplyVisualSettings(GD.Load<TerrainVisualSettings>("res://configs/terrain_visual.tres"));
        Check(original == TerrainVisualValidation.Fingerprint(_data) && results.Zip(Results()).All(pair => ReferenceEquals(pair.First, pair.Second)), "material settings are render-only");
        _terrainQuality.Select(1);
        var layerMeshes = new Dictionary<TerrainView, Mesh>();
        for (int cycle = 0; cycle < 3; cycle++) foreach (TerrainView view in Enum.GetValues<TerrainView>())
        {
            _map.SetView(view); await Frames(1);
            if (cycle == 0) layerMeshes.Add(view, ground.Mesh); else Check(ReferenceEquals(layerMeshes[view], ground.Mesh), "diagnostic layers reuse cached meshes");
            Check(view == TerrainView.Landscape ? ground.MaterialOverride is ShaderMaterial : ground.MaterialOverride is StandardMaterial3D { ShadingMode: BaseMaterial3D.ShadingModeEnum.Unshaded }, "diagnostic colors bypass natural lighting/materials");
        }
        _map.SetView(TerrainView.Landscape); _map.ShowWater(false); await Frames(); Check(!_map.GetNode<MeshInstance3D>("River").Visible, "water toggle"); _map.ShowWater(true);
        var ui = GetNode<CanvasLayer>("Interface");
        for (int i = 0; i < 2; i++)
        {
            GetViewport().PushInput(new InputEventKey { Pressed = true, PhysicalKeycode = Key.F9, Keycode = Key.F9 }, true);
            GetViewport().PushInput(new InputEventKey { Pressed = false, PhysicalKeycode = Key.F9, Keycode = Key.F9 }, true); await Frames();
            Check(ui.Visible == (i == 1), "F9 presentation toggle");
        }
        // Time-separated frames verify animation while fixed-time recaptures verify deterministic rendering.
        string dir = ProjectSettings.GlobalizePath("res://artifacts/terrain_visual/motion"); Directory.CreateDirectory(dir);
        var river = _data.River[180]; _camera.Focus = new(river.Position.X, river.WaterHeight, river.Position.Y); _camera.Size = 150; _camera.Pitch = .62f; _camera.UpdateTransform(); ui.Visible = false;
        var water = (ShaderMaterial)_map.GetNode<MeshInstance3D>("River").MaterialOverride;
        var hashes = new List<string>();
        foreach (float time in new[] { 5f, 6f, 7f, 5f })
        {
            water.SetShaderParameter("preview_time", time); await Frames(6); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage(); image.SavePng(Path.Combine(dir, $"flow_{hashes.Count}.png"));
            hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.GetData())));
        }
        Check(hashes[0] == hashes[3] && hashes[0] != hashes[1] && hashes[1] != hashes[2], "frozen time repeat and animated water");
        water.SetShaderParameter("preview_time", -1f); ui.Visible = true;
        var memory = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            var old = _map; Generate(); await Frames(8); GC.Collect(); GC.WaitForPendingFinalizers(); await Frames(8);
            Check(!GodotObject.IsInstanceValid(old), "map rebuild frees the old render root");
            Check(original == TerrainVisualValidation.Fingerprint(_data), "same-seed rebuild leaves map content unchanged");
            memory.Add(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed));
        }
        Check(memory.Skip(1).Max() - memory.Skip(1).Min() < 4 * 1024 * 1024, "rebuild GPU memory settles within 4 MiB");
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/terrain_visual_ui.json"), JsonSerializer.Serialize(new { passed = true, source = original, unchangedPcgNodes = 6, qualityPresets = 3, cachedDiagnosticLayers = 4, frozenTimeRepeat = true, animatedWater = true, rebuildVideoMemory = memory }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print("TERRAIN_VISUAL_UI_PASS pcg_unchanged=6 presets=3 layers=4 animation=ok rebuilds=4 memory=stable");
    }
}
