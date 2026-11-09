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
/// camera or widget logic of its own. The interface is assembled in MainInterface.cs. Command line: `--test`
/// runs the headless checks; `--capture=DIR` generates the town and saves screenshots.
/// </summary>
public partial class Main : Node3D
{
    private MapSettings _settings = null!;
    private TemperateMapData _map = null!;
    private CityGraph _graph = null!;
    private TerrainRenderer? _terrain;
    private CityView _city = null!;
    private MapCamera _camera = null!;
    private Atmosphere _atmosphere = null!;
    private ScalarField? _paintedField;
    private object? _treeMaskSource;

    // What is shown. Several controls may show one setting; changing it anywhere updates the scene and them all.
    /// <summary>Ground coloring: <see cref="LandscapeGround"/>, <see cref="SuitabilityGround"/> or <see cref="FieldGround"/>.</summary>
    private readonly Setting<int> _ground = new(LandscapeGround);
    private readonly Dictionary<CityLayer, Setting<bool>> _layers = new();
    private readonly Setting<bool> _forest = new(true), _water = new(true), _cover = new(true);

    private const int LandscapeGround = 0, SuitabilityGround = 1;

    /// <summary>Ground coloring that paints the field of <c>FieldStyles.All[index]</c>.</summary>
    private static int FieldGround(int index) => 2 + index;

    /// <summary>The field style the ground is colored by, or null for the landscape and suitability views.</summary>
    private FieldStyle? GroundStyle => _ground.Value >= FieldGround(0) ? FieldStyles.All[_ground.Value - FieldGround(0)] : null;

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

        foreach (var layer in Enum.GetValues<CityLayer>())
        {
            var setting = new Setting<bool>(_city.IsVisible(layer));
            setting.Changed += on =>
            {
                _city.SetVisible(layer, on);
                UpdateLegend();
            };
            _layers[layer] = setting;
        }

        _ground.Changed += _ =>
        {
            _paintedField = null;
            ApplyGround();
        };
        _forest.Changed += on => _terrain?.ShowForest(on);
        _water.Changed += on => _terrain?.ShowWater(on);
        _cover.Changed += on => _terrain?.ShowCover(on);
    }

    /// <summary>Generates the map for <paramref name="seed"/> with the current map settings and regenerates the town on it.</summary>
    private void RebuildMap(int seed)
    {
        _map = TemperateMapData.Create(_settings, seed);
        _graph.SetTerrain(_map);
        ShowMap();
        _ = _graph.Run();
    }

    /// <summary>(Re)creates the terrain renderer for the current map.</summary>
    private void ShowMap()
    {
        _terrain?.QueueFree();
        _terrain = new TerrainRenderer { Name = "Terrain" };
        AddChild(_terrain);
        _terrain.Build(_map);
        _paintedField = null;
        ApplyGround(force: true);
        _terrain.ShowForest(_forest);
        _terrain.ShowWater(_water);
        _terrain.ShowCover(_cover);
        _treeMaskSource = null;
        _terrain.Follow(_camera.GlobalPosition, _camera.Focus);
        MapChanged();
    }

    /// <summary>
    /// Shows the chosen ground coloring. A field is painted when its value changes; while it is being regenerated
    /// the last painting stays (marked as waiting in the legend) rather than flashing back to the landscape.
    /// </summary>
    private void ApplyGround(bool force = false)
    {
        if (_terrain == null)
            return;
        var style = GroundStyle;
        var field = style == null ? null : _graph.Get(style.Key);
        if (style == null || field == null && _paintedField == null)
        {
            if (_ground.Value == SuitabilityGround)
                _terrain.ShowSuitability();
            else
                _terrain.ShowLandscape();
        }
        else if (field != null && (force || !ReferenceEquals(field, _paintedField)))
        {
            _paintedField = field;
            _terrain.ShowColors(p => style.ColorOf(field, field.Sample(p)));
        }

        UpdateLegend();
    }

    /// <summary>Explains every color on screen that needs it: ground coloring, site overlay, roads, contours.</summary>
    private void UpdateLegend()
    {
        if (_ui == null)
            return;
        var entries = new List<LegendEntry>();
        if (GroundStyle is { } style)
        {
            var field = _graph.Get(style.Key);
            var shown = field ?? _paintedField;
            if (shown != null)
            {
                var (low, high) = style.Range(shown);
                string caption = shown.Label + (shown.Unit == "" ? "" : $"（{shown.Unit}）") + (field == null ? " · 等待重新生成" : "");
                entries.Add(new LegendEntry(caption, style.Ramp, style.Describe(shown, low), style.Describe(shown, high)));
            }
        }
        else if (_ground.Value == SuitabilityGround)
        {
            entries.Add(new LegendEntry("地形适宜性", TerrainRenderer.SuitabilityRamp, "不适宜", "适宜",
                new[] { (TerrainRenderer.SuitabilityWater, "水域") }));
        }

        if (_layers[CityLayer.Site].Value && _graph.Get(Keys.Site) != null)
        {
            entries.Add(new LegendEntry("不可建设的原因", null, Categories: new[] { SiteBlock.Water, SiteBlock.Bank, SiteBlock.Slope, SiteBlock.Forest }
                .Select(b => (CityView.SiteColor(b), b.Label())).ToArray()));
        }

        if (_layers[CityLayer.Roads].Value && _graph.Get(Keys.Roads) != null)
        {
            entries.Add(new LegendEntry("道路", null, Categories: new[] { (RoadKind.Main, "主街"), (RoadKind.Street, "街巷"), (RoadKind.Outer, "城外道路") }
                .Select(r => (CityView.RoadColor(r.Item1), r.Item2)).ToArray()));
        }

        if (_layers[CityLayer.Contours].Value && _graph.Get(Keys.Height) != null)
            entries.Add(new LegendEntry($"等高线 · 每 {_city.ContourInterval:0.#} m，每 5 条加粗", null));
        _ui.Legend.Show(entries);
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
            int view = FieldGround(i);
            views.Add(($"field_{FieldStyles.All[i].Key.Name.Replace("site.", "")}", () => ShowField(view, _camera.TopView)));
        }

        foreach (var (key, contours) in new[] { (Keys.Slope, false), (Keys.AboveWater, true) })
        {
            int view = FieldGround(FieldStyles.All.ToList().FindIndex(s => s.Key == key));
            string name = $"field_{key.Name.Replace("site.", "")}_town";
            views.Add((name, () => ShowField(view, () =>
            {
                _layers[CityLayer.Contours].Value = contours;
                _camera.Frame(core, 0.6f, -0.5f, 850);
            })));
        }

        return views;
    }

    private void ShowField(int view, Action frame)
    {
        _ground.Value = view;
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
        if (_ui == null || _terrain == null)
            return;
        _atmosphere.FitShadows(_camera.Distance);
        _terrain.Follow(_camera.GlobalPosition, _camera.Focus);
        _ui.Status.Probe = Probe();
    }

    /// <summary>What lies under the mouse: position, every sampled field, and whether it can be built on.</summary>
    private string Probe()
    {
        var mouse = GetViewport().GetMousePosition();
        if (GetViewport().GuiGetHoveredControl() != null ||
            !_map.Pick(_camera.ProjectRayOrigin(mouse), _camera.ProjectRayNormal(mouse), out var hit))
            return "鼠标移到地面上查看各采样场的读数";

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
        return string.Join("   ", parts);
    }
}
