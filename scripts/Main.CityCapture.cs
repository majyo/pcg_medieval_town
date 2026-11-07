using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;

namespace PcgCitySandbox;

public partial class Main
{
    private async Task CaptureCityViews(string directory, bool benchmark)
    {
        Directory.CreateDirectory(directory);
        await RunSamplingAsync(); await RunSettlementAsync(); await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync(); await RunBuildingsAsync(); await RunPlacementAsync();
        if (CurrentPlacement == null) throw new InvalidOperationException("City capture requires valid placement");
        var selector = (OptionButton)FindChild("PcgNodeSelector", true, false); selector.Select(PlacementStage); selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)PlacementStage);
        _placementLayer.Select(1); ShowPlacement();
        var ui = GetNode<CanvasLayer>("Interface"); ui.Visible = false;
        ((ShaderMaterial)_map.GetNode<MeshInstance3D>("River").MaterialOverride).SetShaderParameter("preview_time", 5f);
        var data = CurrentPlacement; var buildings = data.Buildings;
        var house = buildings.Buildings.Where(b => b.ParcelId.HasValue && b.InsideCore && b.Floors >= 2).OrderBy(b => b.Door.DistanceSquaredTo(data.Roads.Settlement.Core)).First();
        var gate = data.Roads.Settlement.Town!.Gates[0];
        var shots = new List<object>();
        void Focus(Vector2 p, float size, float pitch = .8f, float yaw = -.35f, float lift = 2)
        { _camera.Focus = new(p.X, DisplayTerrain.HeightAt(p) + lift, p.Y); _camera.Size = size; _camera.Pitch = pitch; _camera.Yaw = yaw; _camera.UpdateTransform(); }
        async Task Save(string name)
        {
            for (int i = 0; i < 10; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage(); if (image.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok) throw new IOException(name);
            shots.Add(new { name, focus = new[] { _camera.Focus.X, _camera.Focus.Y, _camera.Focus.Z }, size = _camera.Size, pitch = _camera.Pitch, yaw = _camera.Yaw }); GD.Print($"CITY_CAPTURE {name}");
        }
        FocusPlacement("town"); await Save("01_overview");
        Focus(data.Roads.Settlement.Core, 420, 1.55f, 0); await Save("02_core_top");
        float front = Mathf.Atan2(-house.Inward.X, -house.Inward.Y);
        Focus(house.Footprint.Center, 90, .58f, front); await Save("03_street");
        Focus(house.Footprint.Center, 28, .42f, front, 4); await Save("04_house");
        Focus(gate.Position, 100, .50f, Mathf.Atan2(gate.Outward.X, gate.Outward.Y)); await Save("05_gate");
        foreach (var id in new[] { "church", "abbey" })
        {
            var precinct = data.Roads.Settlement.Town.Precincts.First(p => p.Id == id);
            var b = buildings.Buildings.First(p => p.PrecinctId == id);
            Focus(precinct.Shape.Center, id == "church" ? 110 : 160, .62f, Mathf.Atan2(-b.Inward.X, -b.Inward.Y));
            await Save(id == "church" ? "06_church" : "07_abbey");
        }
        FocusPlacement("market"); await Save("08_market"); FocusPlacement("yard"); await Save("09_yard");
        var junction = data.Roads.Nodes.Where(n => data.Roads.Edges.Count(e => e.From == n.Id || e.To == n.Id) >= 3).OrderBy(n => n.Position.DistanceSquaredTo(data.Roads.Settlement.Core)).First();
        Focus(junction.Position, 85, .65f); await Save("10_junction");
        Focus(gate.Position, 180, .25f, Mathf.Atan2(gate.Outward.X, gate.Outward.Y)); await Save("11_low_angle");
        ui.Visible = true; FocusPlacement("town"); await Save("12_interface"); ui.Visible = false;
        if (benchmark)
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); Engine.MaxFps = 0;
            var rid = GetViewport().GetViewportRid(); RenderingServer.ViewportSetMeasureRenderTime(rid, true);
            var results = new List<object>();
            foreach (string view in new[] { "town", "street", "gate" })
            {
                if (view == "town") FocusPlacement("town");
                else if (view == "street") Focus(house.Footprint.Center, 90, .58f, front);
                else Focus(gate.Position, 100, .5f, Mathf.Atan2(gate.Outward.X, gate.Outward.Y));
                var warmup = Stopwatch.StartNew(); while (warmup.Elapsed.TotalSeconds < 2) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var times = new List<double>(); var cpu = new List<double>(); var gpu = new List<double>(); var timer = Stopwatch.StartNew(); double last = 0;
                while (timer.Elapsed.TotalSeconds < 10) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); double now = timer.Elapsed.TotalMilliseconds; times.Add(now - last); last = now; cpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid)); gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid)); }
                static double Q(List<double> a, double q) { a.Sort(); return a[Math.Min(a.Count - 1, (int)(a.Count * q))]; }
                results.Add(new { view, medianMs = Q(times, .5), p95Ms = Q(times, .95), cpuMs = Q(cpu, .5), gpuMs = Q(gpu, .5), drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame), primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame), videoMemory = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) });
            }
            File.WriteAllText(Path.Combine(directory, "performance.json"), JsonSerializer.Serialize(new { adapter = RenderingServer.GetVideoAdapterName(), resolution = "1440x900", vsync = false, seconds = 30, buildings = buildings.Buildings.Count, props = data.Props.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if(OS.GetCmdlineUserArgs().Contains("--benchmark-road-construction"))await BenchmarkRoadConstruction(directory);
        File.WriteAllText(Path.Combine(directory, "cameras.json"), JsonSerializer.Serialize(new { seed = _settings.Seed, shots }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print("CITY_CAPTURE_PASS views=12");
    }
}
