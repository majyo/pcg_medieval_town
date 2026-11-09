using System;
using System.Collections.Generic;
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
    private CityGraph _graph = null!;
    private TerrainRenderer _terrain = null!;
    private CityView _city = null!;
    private MapCamera _camera = null!;
    private Atmosphere _atmosphere = null!;
    private MapPanel _mapPanel = null!;
    /// <summary>Ground coloring: 0 landscape, 1 terrain suitability, 2 + i the field of <c>FieldStyles.All[i]</c>.</summary>
    private int _groundView;
    private ScalarField? _paintedField;
    private OptionButton _groundChoice = null!;
    private readonly Dictionary<CityLayer, CheckBox> _layerToggles = new();
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
            _graph = TownGraph.Create(_map);
            _graph.Changed += OnGraphChanged;
            BuildScene();
            BuildInterface();
            ShowMap();

            await _graph.Run();
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
                await Capture.Save(this, _camera, core, closeups, FieldViews(core), capture["--capture=".Length..]);
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
        _city.Bind(_graph);
    }

    private void BuildInterface()
    {
        var ui = new CanvasLayer();
        AddChild(ui);

        _mapPanel = new MapPanel { Title = "中世纪城镇沙盒", Seed = _settings.Seed, Position = new Vector2(16, 16) };
        _mapPanel.RebuildRequested += seed =>
        {
            _map = TemperateMapData.Create(_settings, seed);
            _graph.SetTerrain(_map);
            ShowMap();
            _ = _graph.Run();
        };
        _mapPanel.ResetViewRequested += _camera.ResetView;
        var grounds = new[] { "地表", "适宜性（地形）" }.Concat(FieldStyles.All.Select(s => "采样 · " + s.Name)).ToArray();
        _groundChoice = _mapPanel.AddChoice(grounds, _groundView, SelectGround);
        _mapPanel.AddLegend();
        _mapPanel.AddToggle("森林", _showForest, on => _terrain.ShowForest(_showForest = on));
        _mapPanel.AddToggle("河面", _showWater, on => _terrain.ShowWater(_showWater = on));
        _mapPanel.AddToggle("地被与岩石", _showCover, on => _terrain.ShowCover(_showCover = on));
        foreach (var (layer, name) in new[]
        {
            (CityLayer.Contours, "等高线"), (CityLayer.Site, "场地限制"), (CityLayer.Layout, "广场与城墙"), (CityLayer.Roads, "道路"),
            (CityLayer.Parcels, "地块"), (CityLayer.Buildings, "建筑"),
        })
        {
            _layerToggles[layer] = _mapPanel.AddToggle(name, _city.IsVisible(layer), on => _city.SetVisible(layer, on));
        }

        ui.AddChild(_mapPanel);
        ui.AddChild(new MapCompass { Camera = _camera, MetersPerPixel = _camera.MetersPerPixel, Position = new Vector2(280, 16) });

        var stages = new StagePanel { Name = "Stages" };
        ui.AddChild(stages);
        UiStyle.DockRight(stages, 364);
        stages.Bind(_graph);
    }

    /// <summary>(Re)creates the terrain renderer for the current map.</summary>
    private void ShowMap()
    {
        _terrain?.QueueFree();
        _terrain = new TerrainRenderer { Name = "Terrain" };
        AddChild(_terrain);
        _terrain.Build(_map);
        _paintedField = null;
        ApplyGround();
        _terrain.ShowForest(_showForest);
        _terrain.ShowWater(_showWater);
        _terrain.ShowCover(_showCover);
        _treeMaskSource = null;
        _terrain.Follow(_camera.GlobalPosition, _camera.Focus);
        _mapPanel.Stats = $"种子 {_map.Settings.Seed}\n高程 {_map.MinHeight:0}–{_map.MaxHeight:0} m\n" +
            $"林地 {_map.ForestFraction:P0}   开阔缓坡 {_map.OpenGentleFraction:P0}";
    }

    private void SelectGround(int view)
    {
        _groundView = view;
        _groundChoice.Selected = view;
        _paintedField = null;
        ApplyGround();
    }

    /// <summary>
    /// Shows the chosen ground coloring. A field is painted when its value changes; while it is being regenerated
    /// the last painting stays (marked as waiting) rather than flashing back to the landscape.
    /// </summary>
    private void ApplyGround()
    {
        if (_terrain == null || _mapPanel == null)
            return;
        var style = _groundView >= 2 ? FieldStyles.All[_groundView - 2] : null;
        var field = style == null ? null : _graph.Get(style.Key);
        if (style == null || field == null && _paintedField == null)
        {
            _mapPanel.HideLegend();
            if (_groundView == 1)
                _terrain.ShowSuitability();
            else
                _terrain.ShowLandscape();
            return;
        }

        var shown = field ?? _paintedField!;
        if (!ReferenceEquals(field, _paintedField) && field != null)
        {
            _paintedField = field;
            _terrain.ShowColors(p => style.ColorOf(field, field.Sample(p)));
        }

        var (low, high) = style.Range(shown);
        string caption = shown.Label + (shown.Unit == "" ? "" : $"（{shown.Unit}）") + (field == null ? " · 等待重新生成" : "");
        _mapPanel.ShowLegend(caption, style.Ramp, style.Describe(shown, low), style.Describe(shown, high));
    }

    /// <summary>
    /// Capture views: each sampled field over the whole map, then slope around the town, then height above water
    /// around the town with contours.
    /// </summary>
    private List<(string Name, Action Setup)> FieldViews(Vector3 core)
    {
        var views = new List<(string Name, Action Setup)>();
        for (int i = 0; i < FieldStyles.All.Count; i++)
        {
            int view = 2 + i;
            views.Add(($"field_{FieldStyles.All[i].Key.Name.Replace("site.", "")}", () => ShowField(view, _camera.TopView)));
        }

        foreach (var (key, contours) in new[] { (Keys.Slope, false), (Keys.AboveWater, true) })
        {
            int view = 2 + FieldStyles.All.ToList().FindIndex(s => s.Key == key);
            string name = $"field_{key.Name.Replace("site.", "")}_town";
            views.Add((name, () => ShowField(view, () =>
            {
                _city.SetVisible(CityLayer.Contours, contours);
                _layerToggles[CityLayer.Contours].SetPressedNoSignal(contours);
                _camera.Frame(core, 0.6f, -0.5f, 850);
            })));
        }

        return views;
    }

    private void ShowField(int view, Action frame)
    {
        SelectGround(view);
        frame();
    }

    private Vector3 FocusOnTown()
    {
        var center = _graph.Get(Keys.Core)?.Position ?? Vector2.Zero;
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
    private void OnGraphChanged()
    {
        _city.Refresh();
        ApplyGround();
        var wall = _graph.Get(Keys.Wall);
        var roads = _graph.Get(Keys.Roads);
        if (ReferenceEquals(_treeMaskSource, roads) || _terrain == null)
            return;
        _treeMaskSource = roads;
        _terrain.SetExclusion(wall == null ? null : new Exclusion(wall.Interior.Contains, wall.Interior.Bounds,
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

        var parts = new List<string> { $"({hit.X:0}, {hit.Y:0})" };
        foreach (var style in FieldStyles.All)
        {
            var field = _graph.Get(style.Key);
            if (field != null)
                parts.Add($"{field.Label} {style.Describe(field, field.Sample(hit))}");
        }

        var site = _graph.Get(Keys.Site);
        int cell = site?.CellAt(hit) ?? -1;
        if (cell >= 0)
            parts.Add(site!.Block[cell].Label());
        _mapPanel.Probe = string.Join("  ", parts);
    }
}
