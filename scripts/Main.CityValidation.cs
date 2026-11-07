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
    private async Task VerifyCityVisuals(bool graphical)
    {
        void Check(bool ok, string message) => CityVisualValidation.Require(ok, message);
        async Task Frames(int n = 3) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Chain() { await RunSamplingAsync(); await RunSettlementAsync(); await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync(); await RunBuildingsAsync(); await RunPlacementAsync(); }
        var reports = new List<object>();
        var seedArg=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--validation-seed="));
        foreach (int seed in seedArg!=null?new[]{int.Parse(seedArg.Split('=')[1])}:graphical ? new[] { 73129 } : new[] { 73129, 7, 22017 })
        {
            if (_data.Settings.Seed != seed) { _seed.Value = seed; Generate(); }
            await Chain(); Check(CurrentPlacement != null, "valid full city prerequisite");
            var source = CurrentPlacement!; string fingerprint = CityVisualValidation.Fingerprint(source); string environment = TerrainVisualValidation.Fingerprint(_data);
            _citySelector.Select(PlacementStage); _citySelector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)PlacementStage); UpdateCityPresentation(true); await Frames();
            reports.Add(CityVisualValidation.Geometry(_cityPresentation));
            object?[] Results() => new object?[] { SampledEnvironment, CurrentSettlement, CurrentRoads, CurrentParcels, CurrentBuildings, CurrentPlacement };
            var results = Results(); int rebuilds = _cityPresentation.RebuildCount;
            for (int mode = 0; mode < 3; mode++)
            {
                _cityMode.Select(mode); _cityMode.EmitSignal(OptionButton.SignalName.ItemSelected, (long)mode);
                for (int theme = 0; theme < _cityTheme.ItemCount; theme++) { _cityTheme.Select(theme); _cityTheme.EmitSignal(OptionButton.SignalName.ItemSelected, (long)theme); await Frames(1); }
            }
            for (int i = 0; i < 3; i++) { _cityWalls.Select(i); _cityWalls.EmitSignal(OptionButton.SignalName.ItemSelected, (long)i); _cityRoof.ButtonPressed = i % 2 == 0; _cityDetails.ButtonPressed = i % 2 == 1; await Frames(); }
            _cityWalls.Select(0); _cityRoof.ButtonPressed = true; _cityDetails.ButtonPressed = true; _cityMode.Select(0); _cityTheme.Select(0); UpdateCityPresentation(true); await Frames();
            _cityShow.ButtonPressed = false; await Frames(); Check(!_cityPresentation.Visible, "result visibility off");
            _cityShow.ButtonPressed = true; await Frames(); Check(_cityPresentation.Visible, "result visibility on");
            Check(_cityPresentation.RebuildCount == rebuilds, "mode/theme/wall/roof changes reuse scene geometry");
            Check(results.Zip(Results()).All(pair => ReferenceEquals(pair.First, pair.Second)), "visual settings do not invalidate PCG nodes");
            Check(CityVisualValidation.Fingerprint(source) == fingerprint && TerrainVisualValidation.Fingerprint(_data) == environment, "visual settings leave all source geometry unchanged");
            GD.Print($"CITY_VISUAL_DATA_PASS seed={seed} native_ground=ok windows=ok roof_envelope=ok gates=ok immutable=ok cache=ok");
        }
        if (graphical)
        {
            string dir = ProjectSettings.GlobalizePath("res://artifacts/city_visual/checks"); Directory.CreateDirectory(dir);
            async Task Save(string name) { await Frames(6); await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw); using var image = GetViewport().GetTexture().GetImage(); Check(image.SavePng(Path.Combine(dir,name+".png"))==Error.Ok,"capture "+name); }
            var data = CurrentPlacement!; var house = data.Buildings.Buildings.Where(b => b.ParcelId.HasValue && b.InsideCore && b.Floors > 1).OrderBy(b => b.Door.DistanceSquaredTo(data.Roads.Settlement.Core)).First();
            _camera.Focus = new(house.Footprint.Center.X, house.BaseY + 3, house.Footprint.Center.Y); _camera.Size = 45; _camera.Pitch = .65f; _camera.Yaw = Mathf.Atan2(-house.Inward.X,-house.Inward.Y); _camera.UpdateTransform(); await Frames(5);
            var roof = _cityPresentation.Bodies; int face = Enumerable.Range(0, roof.Triangles).Select(i => i * 3).First(i => roof.Owners[i] == $"B{house.Id}" && roof.Parts[i] == "roof");
            var center = (roof.Positions[face] + roof.Positions[face + 1] + roof.Positions[face + 2]) / 3; var screen = _camera.UnprojectPosition(center);
            var timer = Stopwatch.StartNew();
            GetViewport().PushInput(new InputEventMouseButton { Position = screen, GlobalPosition = screen, Pressed = true, ButtonIndex = MouseButton.Left }, true);
            GetViewport().PushInput(new InputEventMouseButton { Position = screen, GlobalPosition = screen, Pressed = false, ButtonIndex = MouseButton.Left }, true);
            double selectionMs = timer.Elapsed.TotalMilliseconds; int selectionTriangles = _cityPresentation.LastPickTriangles; await Frames();
            Check(_citySelection == $"B{house.Id}" && _selectedPlacement == _citySelection, "visible roof picking and source ownership");
            Check(selectionTriangles < _cityPresentation.Bodies.Triangles / 5, "picking rejects unrelated object bounds");
            await Save("selected_building");
            int rebuilds = _cityPresentation.RebuildCount; FocusCitySelection(); await Frames(); Check(_camera.Size < 120, "single-building focus");
            _cityRoof.ButtonPressed = false; await Frames(); Check(_cityPresentation.RebuildCount == rebuilds, "roof hiding reuses geometry"); _cityRoof.ButtonPressed = true;
            var prior = CurrentPlacement; _buildingHeight.Value = 1.1; UpdateCityPresentation(true); await Frames();
            Check(CurrentPlacement == null && _cityPresentation.Context?.Stale == true && ReferenceEquals(_cityPresentation.Context.Placement, prior), "stale snapshot is explicit and coherent");
            _buildingHeight.Value = 1; await RunBuildingsAsync(); await RunPlacementAsync(); UpdateCityPresentation(true); await Frames();
            Check(_citySelection == null && _cityPresentation.Context?.Stale == false, "regeneration clears stale selection");
            foreach (int mode in new[] { 0, 1, 2 }) { _cityMode.Select(mode); _cityTheme.Select(mode==1?2:0); UpdateCityPresentation(true); _camera.Size=240;_camera.UpdateTransform(); await Save($"mode_{mode}"); }
            _cityMode.Select(1); _cityTheme.Select(3); UpdateCityPresentation(true); await Save("access");
            _cityMode.Select(0); _cityWalls.Select(1); _cityRoof.ButtonPressed=false; UpdateCityPresentation(true); await Save("low_wall_no_roof");
            _cityRoof.ButtonPressed=true;_cityWalls.Select(0);UpdateCityPresentation(true);
            DisplayServer.WindowSetSize(new(1280,720)); await Frames(10); await Save("interface_1280");
            Check(_cityToolbar.GetGlobalRect().End.X <= GetViewport().GetVisibleRect().Size.X - 330 && _cityToolbar.GetGlobalRect().Size.Y < 150, "toolbar fits narrow viewport");
            DisplayServer.WindowSetSize(new(1440,900)); await Frames();
            File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/city_visual_ui.json"), JsonSerializer.Serialize(new { passed=true, selectionMs, testedCandidateTriangles=selectionTriangles, cachedModeSwitches=true, staleSnapshot=true, resolutionChecks=new[]{"1440x900","1280x720"}},new JsonSerializerOptions{WriteIndented=true}));
            GD.Print($"CITY_VISUAL_UI_PASS picking=ok selection_ms={selectionMs:F2} cache=ok stale=ok resolution=ok");
        }
        File.WriteAllText(ProjectSettings.GlobalizePath(graphical ? "res://artifacts/city_visual_geometry_ui.json" : "res://artifacts/city_visual_validation.json"), JsonSerializer.Serialize(new {passed=true,reports},new JsonSerializerOptions{WriteIndented=true}));
    }
}
