using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PcgCitySandbox;

public partial class Main
{
    private async Task CaptureTerrainVisuals(string directory, bool benchmark)
    {
        Directory.CreateDirectory(directory);
        var ui = GetNode<CanvasLayer>("Interface");
        ui.Visible = false;
        var water = (ShaderMaterial)_map.GetNode<MeshInstance3D>("River").MaterialOverride;
        water.SetShaderParameter("preview_time", 5.0f);
        var shots = new List<object>();
        void Focus(Vector2 p, float size, float pitch = .85f, float yaw = -.38f)
        {
            _camera.Focus = new(p.X, _data.HeightAt(p), p.Y);
            _camera.Size = size; _camera.Pitch = pitch; _camera.Yaw = yaw; _camera.UpdateTransform();
        }
        async Task Save(string name)
        {
            for (int i = 0; i < 12; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var screenshot = GetViewport().GetTexture().GetImage();
            if (screenshot.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok) throw new IOException(name);
            shots.Add(new { name, focus = new[] { _camera.Focus.X, _camera.Focus.Y, _camera.Focus.Z }, size = _camera.Size, pitch = _camera.Pitch, yaw = _camera.Yaw });
            GD.Print($"TERRAIN_CAPTURE {name}");
        }
        Focus(Vector2.Zero, 2600); await Save("01_overview");
        Focus(Vector2.Zero, 2200, 1.55f, 0); await Save("02_top");
        Focus(_data.River[100].Position, 340, .72f); await Save("03_river_bend");
        Focus(_data.River[180].Position, 180, .42f, .65f); await Save("04_bank_close");
        Focus(new(-530, -450), 460, .75f); await Save("05_forest_edge");
        Focus(_data.River[6].Position, 290, .6f, .25f); await Save("06_river_north");
        Focus(_data.River[^7].Position, 290, .55f, 3.25f); await Save("07_river_south");
        Focus(_data.River[100].Position, 460, .26f); await Save("08_low_angle");

        if (benchmark)
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;
            var viewport = GetViewport().GetViewportRid();
            RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
            var results = new List<object>();
            foreach (var view in new[] { ("overview", Vector2.Zero, 2600f), ("regional", _data.River[100].Position, 800f), ("river", _data.River[180].Position, 180f) })
            {
                Focus(view.Item2, view.Item3);
                var warmup = Stopwatch.StartNew();
                while (warmup.Elapsed.TotalSeconds < 2) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var frames = new List<double>(); var cpu = new List<double>(); var gpu = new List<double>();
                var timer = Stopwatch.StartNew(); double last = 0;
                while (timer.Elapsed.TotalSeconds < 10)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    double now = timer.Elapsed.TotalMilliseconds; frames.Add(now - last); last = now;
                    cpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport));
                    gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport));
                }
                static double Q(List<double> values, double quantile) { values.Sort(); return values[Math.Min(values.Count - 1, (int)(values.Count * quantile))]; }
                results.Add(new { view = view.Item1, frames = frames.Count, medianMs = Q(frames, .5), p95Ms = Q(frames, .95), cpuRenderMs = Q(cpu, .5), gpuRenderMs = Q(gpu, .5),
                    drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame), primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame), videoMemory = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) });
            }
            File.WriteAllText(Path.Combine(directory, "performance.json"), JsonSerializer.Serialize(new { adapter = RenderingServer.GetVideoAdapterName(), api = RenderingServer.GetVideoAdapterApiVersion(), renderer = "gl_compatibility", resolution = "1440x900", vsync = "disabled", maxFps = 0, warmupSecondsPerView = 2, measuredSeconds = 30, results }, new JsonSerializerOptions { WriteIndented = true }));
        }
        ApplyView(TerrainView.Slope); Focus(Vector2.Zero, 2200, 1.55f, 0); await Save("09_slope");
        ApplyView(TerrainView.Landscape);
        await RunSamplingAsync(); await RunSettlementAsync(); await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync(); await RunBuildingsAsync(); await RunPlacementAsync();
        if (CurrentPlacement == null) throw new InvalidOperationException("Terrain capture requires valid town placement");
        _placementLayer.Select(1); ShowPlacement(); FocusPlacement("town"); _camera.Size *= 1.2f; _camera.UpdateTransform(); await Save("10_town");
        FocusPlacement("market"); await Save("11_market");
        ui.Visible = true; _camera.ResetView(); await Save("12_interface");
        File.WriteAllText(Path.Combine(directory, "cameras.json"), JsonSerializer.Serialize(new { seed = _settings.Seed, waterTime = 5, shots }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print("TERRAIN_CAPTURE_PASS views=12");
    }
}
