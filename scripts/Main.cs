using System;
using System.Linq;
using Godot;
using PcgCitySandbox.CameraControl;
using PcgCitySandbox.City;
using PcgCitySandbox.Rendering;
using PcgCitySandbox.Terrain;
using PcgCitySandbox.Ui;

namespace PcgCitySandbox;

/// <summary>
/// Composition root. Creates the components and wires them together; holds no generation, rendering,
/// camera or widget logic of its own. Command line: `--test` runs the headless checks;
/// `--capture=DIR` generates the town and saves screenshots.
/// </summary>
public partial class Main : Node3D
{
    private MapSettings _settings = null!;
    private TemperateMapData _map = null!;
    private Pipeline _pipeline = null!;
    private TerrainRenderer _terrain = null!;
    private CityView _city = null!;
    private MapCamera _camera = null!;
    private MapPanel _mapPanel = null!;
    private TerrainView _view = TerrainView.Landscape;
    private bool _showForest = true, _showWater = true;
    private object? _treeMaskSource;

    public override async void _Ready()
    {
        try
        {
            _settings = GD.Load<MapSettings>("res://configs/temperate_plain.tres");
            var args = OS.GetCmdlineUserArgs();
            if (args.Contains("--test"))
            {
                GetTree().Quit(await Checks.Run(seed => TemperateMapData.Create(_settings, seed)));
                return;
            }

            _map = TemperateMapData.Create(_settings, _settings.Seed);
            _pipeline = TownPipeline.Create(_map);
            _pipeline.Changed += OnPipelineChanged;
            BuildScene();
            BuildInterface();
            ShowMap();

            await _pipeline.Run();
            var core = FocusOnTown();
            var capture = args.FirstOrDefault(a => a.StartsWith("--capture="));
            if (capture != null)
            {
                await Capture.Save(this, _camera, core, capture["--capture=".Length..]);
                GetTree().Quit();
            }
        }
        catch (Exception ex)
        {
            GD.PushError(ex.ToString());
            GetTree().Quit(1);
        }
    }

    private void BuildScene()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("d6dfdc"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("d3dfe4"),
                AmbientLightEnergy = 0.45f,
            },
        });
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-38, -38, 0),
            LightColor = new Color("fff7ed"),
            LightEnergy = 0.85f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 4500,
        });
        _camera = new MapCamera { MapSize = _settings.SizeMeters };
        AddChild(_camera);
        _city = new CityView { Name = "City" };
        AddChild(_city);
        _city.Bind(_pipeline);
    }

    private void BuildInterface()
    {
        var ui = new CanvasLayer();
        AddChild(ui);

        _mapPanel = new MapPanel { Title = "中世纪城镇沙盒", Seed = _settings.Seed, Position = new Vector2(16, 16) };
        _mapPanel.RebuildRequested += seed =>
        {
            _map = TemperateMapData.Create(_settings, seed);
            _pipeline.SetTerrain(_map);
            ShowMap();
            _ = _pipeline.Run();
        };
        _mapPanel.ResetViewRequested += _camera.ResetView;
        _mapPanel.AddChoice(new[] { "地表", "高程", "坡度", "适宜性" }, (int)_view, i =>
        {
            _view = (TerrainView)i;
            _terrain.SetView(_view);
        });
        _mapPanel.AddToggle("森林", _showForest, on => _terrain.ShowForest(_showForest = on));
        _mapPanel.AddToggle("河面", _showWater, on => _terrain.ShowWater(_showWater = on));
        foreach (var (layer, name) in new[]
        {
            (CityLayer.Site, "场地限制"), (CityLayer.Layout, "广场与城墙"), (CityLayer.Roads, "道路"),
            (CityLayer.Parcels, "地块"), (CityLayer.Buildings, "建筑"),
        })
        {
            _mapPanel.AddToggle(name, _city.IsVisible(layer), on => _city.SetVisible(layer, on));
        }

        ui.AddChild(_mapPanel);
        ui.AddChild(new MapCompass { Camera = _camera, Position = new Vector2(280, 16) });

        var stages = new StagePanel { Name = "Stages" };
        ui.AddChild(stages);
        UiStyle.DockRight(stages, 364);
        stages.Bind(_pipeline);
    }

    /// <summary>(Re)creates the terrain renderer for the current map.</summary>
    private void ShowMap()
    {
        _terrain?.QueueFree();
        _terrain = new TerrainRenderer { Name = "Terrain" };
        AddChild(_terrain);
        _terrain.Build(_map);
        _terrain.SetView(_view);
        _terrain.ShowForest(_showForest);
        _terrain.ShowWater(_showWater);
        _treeMaskSource = null;
        _mapPanel.Stats = $"种子 {_map.Settings.Seed}\n高程 {_map.MinHeight:0}–{_map.MaxHeight:0} m\n" +
            $"林地 {_map.ForestFraction:P0}   开阔缓坡 {_map.OpenGentleFraction:P0}";
    }

    private Vector3 FocusOnTown()
    {
        var center = _pipeline.Get<TownLayout>()?.Core ?? Vector2.Zero;
        var core = new Vector3(center.X, _map.HeightAt(center), center.Y);
        _camera.Frame(core, 0.83f, -0.38f, 900);
        return core;
    }

    /// <summary>The one place city results reach the terrain view: trees vanish inside the walls and on roads.</summary>
    private void OnPipelineChanged()
    {
        _city.Refresh();
        var layout = _pipeline.Get<TownLayout>();
        var roads = _pipeline.Get<RoadGraph>();
        if (ReferenceEquals(_treeMaskSource, roads) || _terrain == null)
            return;
        _treeMaskSource = roads;
        var near = roads?.Footprint.Offset(4);
        _terrain.SetTreeMask(layout == null ? null : p => layout.Interior.Contains(p) || near?.Contains(p) == true);
    }

    public override void _Process(double delta)
    {
        if (_mapPanel == null)
            return;
        var mouse = GetViewport().GetMousePosition();
        if (!_map.Pick(_camera.ProjectRayOrigin(mouse), _camera.ProjectRayNormal(mouse), out var hit))
        {
            _mapPanel.Probe = "";
            return;
        }

        var sample = _map.Sample(hit);
        var site = _pipeline.Get<Site>();
        int cell = site?.CellAt(hit) ?? -1;
        string block = cell < 0 ? "" : site!.Block[cell].Label();
        _mapPanel.Probe = $"({hit.X:0}, {hit.Y:0})  高程 {sample.Height:0.0} m  坡度 {sample.SlopeDegrees:0.0}°  {block}";
    }
}
