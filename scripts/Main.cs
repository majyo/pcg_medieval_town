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
    private Atmosphere _atmosphere = null!;
    private MapPanel _mapPanel = null!;
    private TerrainView _view = TerrainView.Landscape;
    private bool _showForest = true, _showWater = true, _showCover = true;
    private object? _treeMaskSource;

    public override async void _Ready()
    {
        try
        {
            _settings = GD.Load<MapSettings>("res://configs/temperate_plain.tres");
            var args = OS.GetCmdlineUserArgs();
            if (args.Contains("--test"))
            {
                int terrain = TerrainChecks.Run(_settings);
                int city = await Checks.Run(seed => TemperateMapData.Create(_settings, seed));
                GetTree().Quit(Math.Max(terrain, city));
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
                var bank = BankView(core);
                var forest = ForestEdgeView(core);
                var closeups = new (string, Vector3, float, float, float)[]
                {
                    ("bank", bank.Focus, 0.35f, bank.Yaw, 45),
                    ("bank_low", bank.Focus, 0.12f, bank.Yaw + 0.6f, 16),
                    ("forest", forest.Focus, 0.16f, forest.Yaw, 32),
                };
                await Capture.Save(this, _camera, core, closeups, capture["--capture=".Length..]);
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
        _atmosphere = new Atmosphere { Name = "Atmosphere" };
        AddChild(_atmosphere);
        _camera = new MapCamera { MapSize = _settings.SizeMeters, GroundHeight = p => _map.HeightAt(p) };
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
        _mapPanel.AddToggle("地被与岩石", _showCover, on => _terrain.ShowCover(_showCover = on));
        foreach (var (layer, name) in new[]
        {
            (CityLayer.Site, "场地限制"), (CityLayer.Layout, "广场与城墙"), (CityLayer.Roads, "道路"),
            (CityLayer.Parcels, "地块"), (CityLayer.Buildings, "建筑"),
        })
        {
            _mapPanel.AddToggle(name, _city.IsVisible(layer), on => _city.SetVisible(layer, on));
        }

        ui.AddChild(_mapPanel);
        ui.AddChild(new MapCompass { Camera = _camera, MetersPerPixel = _camera.MetersPerPixel, Position = new Vector2(280, 16) });

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
        _terrain.ShowCover(_showCover);
        _treeMaskSource = null;
        _terrain.Follow(_camera.GlobalPosition, _camera.Focus);
        _mapPanel.Stats = $"种子 {_map.Settings.Seed}\n高程 {_map.MinHeight:0}–{_map.MaxHeight:0} m\n" +
            $"林地 {_map.ForestFraction:P0}   开阔缓坡 {_map.OpenGentleFraction:P0}";
    }

    private Vector3 FocusOnTown()
    {
        var center = _pipeline.Get<TownLayout>()?.Core ?? Vector2.Zero;
        var core = new Vector3(center.X, _map.HeightAt(center), center.Y);
        _camera.Frame(core, 0.83f, -0.38f, 1200);
        return core;
    }

    /// <summary>
    /// A riverbank close-up away from the town: a point just inland of the water line, about 230 m upstream of
    /// where the river passes the town, seen from the land side.
    /// </summary>
    private (Vector3 Focus, float Yaw) BankView(Vector3 core)
    {
        var river = _map.River;
        var town = new Vector2(core.X, core.Z);
        int nearest = 0;
        for (int i = 1; i < river.Count; i++)
        {
            if (river[i].Position.DistanceSquaredTo(town) < river[nearest].Position.DistanceSquaredTo(town))
                nearest = i;
        }

        int i0 = Math.Clamp(nearest - 30, 0, river.Count - 2);
        var point = river[i0];
        var normal = (river[i0 + 1].Position - point.Position).Normalized().Orthogonal();
        if (normal.Dot(town - point.Position) < 0)
            normal = -normal;
        var focus = point.Position + normal * (point.HalfWidth + 3);
        return (new Vector3(focus.X, _map.HeightAt(focus), focus.Y), Mathf.Atan2(normal.X, normal.Y));
    }

    /// <summary>
    /// A forest edge seen from the meadow: the tree 300–600 m from the town core closest to 350 m, looked at
    /// from 18 m further toward the town.
    /// </summary>
    private (Vector3 Focus, float Yaw) ForestEdgeView(Vector3 core)
    {
        var town = new Vector2(core.X, core.Z);
        var tree = _map.Trees
            .Select(t => new Vector2(t.Position.X, t.Position.Z))
            .Where(p => p.DistanceTo(town) is > 300 and < 600)
            .DefaultIfEmpty(town + new Vector2(350, 0))
            .MinBy(p => Mathf.Abs(p.DistanceTo(town) - 350));
        var toTown = (town - tree).Normalized();
        var focus = tree + toTown * 18;
        return (new Vector3(focus.X, _map.HeightAt(focus), focus.Y), Mathf.Atan2(toTown.X, toTown.Y));
    }

    /// <summary>
    /// The one place city results reach the terrain view: no trees, rocks or ground cover inside the walls or
    /// within a few meters of a road.
    /// </summary>
    private void OnPipelineChanged()
    {
        _city.Refresh();
        var layout = _pipeline.Get<TownLayout>();
        var roads = _pipeline.Get<RoadGraph>();
        if (ReferenceEquals(_treeMaskSource, roads) || _terrain == null)
            return;
        _treeMaskSource = roads;
        _terrain.SetExclusion(layout == null ? null : new Exclusion(layout.Interior.Contains, layout.Interior.Bounds,
            roads?.Edges.Select(e => (e.Points, e.Width / 2 + 3)) ?? Enumerable.Empty<(Vector2[], float)>()));
    }

    public override void _Process(double delta)
    {
        if (_mapPanel == null)
            return;
        _atmosphere.FitShadows(_camera.Distance);
        _terrain.Follow(_camera.GlobalPosition, _camera.Focus);
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
