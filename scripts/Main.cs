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
/// runs the headless checks; `--capture=DIR` generates the town and saves screenshots; `--seed=N` replaces the map
/// seed from the settings for this run.
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
    private object? _painted;
    private GroundStyle? _paintedStyle;

    /// <summary>The version the painted value's node started from, when it edits the value (its cells are grayed).</summary>
    private object? _paintedPrevious;
    private object? _treeMaskSource;

    // What is shown. Several controls may show one setting; changing it anywhere updates the scene and them all.
    /// <summary>The node or stage whose outputs the scene shows; none shows the town as the settings below say.</summary>
    private readonly Setting<GraphFocus> _focus = new(GraphFocus.None);

    /// <summary>Which output of the focus is painted on the ground, when it has several; null picks the first.</summary>
    private IKey? _focusGround;

    /// <summary>Ground coloring without a focus: <see cref="LandscapeGround"/> or <see cref="FieldGround"/>.</summary>
    private readonly Setting<int> _ground = new(LandscapeGround);

    /// <summary>City layers shown without a focus.</summary>
    private readonly Dictionary<CityLayer, Setting<bool>> _layers = new();
    private readonly Setting<bool> _forest = new(true), _water = new(true), _cover = new(true);
    private List<Shown> _shown = new();

    private const int LandscapeGround = 0;

    /// <summary>Ground coloring that paints the field of <c>FieldStyles.All[index]</c>.</summary>
    private static int FieldGround(int index) => 1 + index;

    /// <summary>The style the ground is colored by, or null for the landscape.</summary>
    private GroundStyle? ShownStyle => _ground.Value >= FieldGround(0) ? FieldStyles.All[_ground.Value - FieldGround(0)] : null;

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

            var seed = args.FirstOrDefault(a => a.StartsWith("--seed="));
            if (seed != null)
                _settings.Seed = int.Parse(seed["--seed=".Length..]);
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
                }.Concat(LandmarkViews()).Concat(SuburbViews()).Concat(WorkshopViews()).ToArray();
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

        var hidden = new[] { CityLayer.Contours, CityLayer.Region, CityLayer.Blocks };
        foreach (var layer in Enum.GetValues<CityLayer>())
        {
            var setting = new Setting<bool>(!hidden.Contains(layer));
            setting.Changed += _ => RefreshView();
            _layers[layer] = setting;
        }

        _ground.Changed += _ => RefreshView();
        _focus.Changed += _ =>
        {
            _focusGround = null;
            RefreshView();
        };
        _forest.Changed += _ => ShowEnvironment();
        _water.Changed += _ => ShowEnvironment();
        _cover.Changed += _ => ShowEnvironment();
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
        _painted = null;
        ApplyGround(force: true);
        ShowEnvironment();
        _treeMaskSource = null;
        _terrain.Follow(_camera.GlobalPosition, _camera.Focus);
        MapChanged();
    }

    /// <summary>
    /// The outputs of the focus, in order: each key with the version the focused node wrote (and, for a key it edits,
    /// the version it started from), or for a stage the last version any of its nodes wrote.
    /// </summary>
    private List<(IKey Key, object? Value, object? Previous)> FocusOutputs()
    {
        var focus = _focus.Value;
        var outputs = new List<(IKey Key, object? Value, object? Previous)>();
        if (focus.IsNone)
            return outputs;
        var nodes = focus.Node >= 0 ? new[] { focus.Node } : _stages[focus.Stage].Nodes;
        foreach (int n in nodes)
        {
            var node = _graph.Nodes[n];
            foreach (var key in node.Writes.Concat(node.Edits))
            {
                var previous = focus.Node >= 0 && node.Edits.Contains(key) ? _graph.Input(n, key) : null;
                outputs.RemoveAll(o => o.Key == key);
                outputs.Add((key, _graph.Output(n, key), previous));
            }
        }

        return outputs;
    }

    /// <summary>
    /// The ground coloring on screen: with a focus, the chosen (else the first) of its outputs that can be painted,
    /// preferring measurements and evaluations over intermediate grids for a stage; otherwise the "视图" choice.
    /// </summary>
    private (GroundStyle? Style, object? Value, object? Previous) GroundShown()
    {
        if (_focus.Value.IsNone)
            return (ShownStyle, ShownStyle?.Value(_graph), null);
        var painted = FocusOutputs().Select(o => (Style: FieldStyles.For(o.Key), o.Value, o.Previous))
            .Where(o => o.Style != null).ToList();
        if (painted.Count == 0)
            return (null, null, null);
        var chosen = painted.FirstOrDefault(o => o.Style!.Key == _focusGround);
        if (chosen.Style == null)
            chosen = _focus.Value.Node < 0 ? painted.FirstOrDefault(o => o.Style!.Probe, painted[0]) : painted[0];
        return chosen;
    }

    /// <summary>Updates everything on screen from the focus and the view settings: city meshes, ground and legend.</summary>
    private void RefreshView()
    {
        _shown = _focus.Value.IsNone
            ? Enum.GetValues<CityLayer>().Where(l => _layers[l].Value).SelectMany(CityView.LayerKeys)
                .Select(k => (Key: k, Value: _graph.Final(k))).Where(s => s.Value != null).Select(s => new Shown(s.Key, s.Value!)).ToList()
            : FocusOutputs().Where(o => o.Value != null && _city.Draws(o.Key))
                .Select(o => new Shown(o.Key, o.Value!, o.Previous, Detail: true)).ToList();
        _city.Show(_shown);
        ShowEnvironment();
        ApplyGround();
    }

    /// <summary>Trees, rocks and ground cover only without a focus: they would hide the outputs being inspected.</summary>
    private void ShowEnvironment()
    {
        if (_terrain == null)
            return;
        _terrain.ShowForest(_forest && _focus.Value.IsNone);
        _terrain.ShowWater(_water);
        _terrain.ShowCover(_cover && _focus.Value.IsNone);
    }

    /// <summary>
    /// Shows the ground coloring. A field is painted when its value changes; while it is being regenerated the last
    /// painting of the same field stays (marked as waiting in the legend) rather than flashing back to the landscape.
    /// </summary>
    private void ApplyGround(bool force = false)
    {
        if (_terrain == null)
            return;
        var (style, value, previous) = GroundShown();
        if (style == null || value == null && _paintedStyle != style)
        {
            _terrain.ShowLandscape();
            (_painted, _paintedPrevious, _paintedStyle) = (null, null, null);
        }
        else if (value != null && (force || !ReferenceEquals(value, _painted) || !ReferenceEquals(previous, _paintedPrevious)))
        {
            (_painted, _paintedPrevious, _paintedStyle) = (value, previous, style);
            _terrain.ShowColors(style.Painter(value, previous));
        }

        UpdateLegend();
    }

    /// <summary>Says what is on screen: the focus, then every color that needs explaining.</summary>
    private void UpdateLegend()
    {
        if (_ui == null)
            return;
        var entries = new List<LegendEntry>();
        var focus = _focus.Value;
        if (!focus.IsNone)
        {
            string what = focus.Node >= 0 ? $"节点「{_graph.Nodes[focus.Node].Title}」的输出" : $"阶段「{_stages[focus.Stage].Title}」的输出";
            var outputs = FocusOutputs();
            if (focus.Stage == 0)
                what = "地形（不显示城市）";
            else if (outputs.Count > 0 && outputs.All(o => o.Value == null))
                what += " · 等待生成";
            entries.Add(new LegendEntry(what, null));
        }

        var (style, value, previous) = GroundShown();
        if (style != null && (value ?? _painted) is { } painted)
        {
            var legend = style.Legend(painted, value == null ? null : previous);
            string caption = legend.Caption + (value == null ? " · 等待重新生成" : "");
            entries.Add(new LegendEntry(caption, legend.Ramp, legend.Low, legend.High, legend.Categories));
        }

        foreach (var shown in _shown)
        {
            if (_city.Legend(shown) is { } legend && entries.All(e => e.Caption != legend.Caption))
                entries.Add(new LegendEntry(legend.Caption, legend.Ramp, legend.Low, legend.High, legend.Categories));
        }

        _ui.Legend.Show(entries);
    }

    /// <summary>
    /// Capture views: each sampled field and evaluation over the whole map, then slope around the town, height above
    /// water around the town with contours, buildability and habitability around the town, and last the regional
    /// frame over the map and around the town's origin.
    /// </summary>
    private List<(string Name, Action Setup)> FieldViews(Vector3 core)
    {
        var views = new List<(string Name, Action Setup)>();
        for (int i = 0; i < FieldStyles.All.Count; i++)
        {
            int view = FieldGround(i);
            views.Add(($"field_{ShortName(FieldStyles.All[i].Key)}", () => ShowField(view, _camera.TopView)));
        }

        var townViews = new (IKey Key, bool Contours)[]
        {
            (Keys.Slope, false), (Keys.AboveWater, true), (Keys.Buildable, false), (Keys.Habitability, true),
            (Keys.LandUse, false),
        };
        foreach (var (key, contours) in townViews)
        {
            int view = FieldGround(FieldStyles.All.ToList().FindIndex(s => s.Key == key));
            string name = $"field_{ShortName(key)}_town";
            views.Add((name, () => ShowField(view, () =>
            {
                _layers[CityLayer.Contours].Value = contours;
                _camera.Frame(core, 0.6f, -0.5f, 850);
            })));
        }

        // Each node's outputs on their own: the site and the region over the whole map, the town around its core.
        void Focus(int node)
        {
            _focus.Value = new GraphFocus(StageOf(node), node);
            if (_graph.Nodes[node].Group is NodeGroups.Sampling or NodeGroups.Evaluation or NodeGroups.Regional)
                _camera.TopView();
            else
                _camera.Frame(core, 0.75f, -0.5f, 900);
        }

        void Result()
        {
            _focus.Value = GraphFocus.None;
            _camera.Frame(core, 0.6f, -0.5f, 850);
        }

        for (int i = 0; i < _graph.Nodes.Count; i++)
        {
            int node = i;
            views.Add(($"node_{node + 1:00}_{_graph.Nodes[node].GetType().Name}", () => Focus(node)));
        }

        views.Add(("result", Result));
        return views;
    }

    /// <summary>A close-up of each landmark from the south-east, so its building and plot can be checked.</summary>
    private IEnumerable<(string, Vector3, float, float, float)> LandmarkViews() =>
        (_graph.Get(Keys.Landmarks)?.Landmarks ?? Array.Empty<Landmark>()).Select(l =>
            ($"landmark_{l.Id}", new Vector3(l.Center.X, _map.HeightAt(l.Center), l.Center.Y), 0.35f, 2.4f, 110f));

    /// <summary>The suburb outside each gate, seen steeply from the town side, so its lanes and back rows can be checked.</summary>
    private IEnumerable<(string, Vector3, float, float, float)> SuburbViews() =>
        (_graph.Get(Keys.Gates)?.Gates ?? Array.Empty<Gate>()).Select(g =>
        {
            var focus = g.Position + g.Outward * 110;
            return ($"suburb_{g.Id}", new Vector3(focus.X, _map.HeightAt(focus), focus.Y), 0.95f,
                Mathf.Atan2(-g.Outward.X, -g.Outward.Y), 520f);
        });

    /// <summary>Each patch of wet or fire trades from above at an angle, so its tracks, yards and sheds can be checked.</summary>
    private IEnumerable<(string, Vector3, float, float, float)> WorkshopViews()
    {
        var land = _graph.Get(Keys.LandUse);
        if (land == null)
            return Enumerable.Empty<(string, Vector3, float, float, float)>();
        var trades = new HashSet<LandUse> { LandUse.WetWorks, LandUse.FireWorks };
        return land.Patches(trades).Select((patch, i) =>
        {
            var center = patch.Aggregate(Vector2.Zero, (sum, k) => sum + land.Grid.Center(k)) / patch.Count;
            return ($"workshop_{i + 1}", new Vector3(center.X, _map.HeightAt(center), center.Y), 0.7f, 2.4f, 200f);
        });
    }

    private static string ShortName(IKey key) => key.Name[(key.Name.IndexOf('.') + 1)..];

    private void ShowField(int view, Action frame)
    {
        _ground.Value = view;
        frame();
    }

    private Vector3 FocusOnTown()
    {
        var center = _graph.Get(Keys.Plaza)?.Center ?? _graph.Get(Keys.Origin)?.Position ?? Vector2.Zero;
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
    /// The one place city results reach the terrain view: no trees, rocks or ground cover inside the walls, on the
    /// built land outside them, on a landmark's plot, or within a few meters of a road.
    /// </summary>
    private void OnGraphChanged()
    {
        RefreshView();
        var wall = _graph.Get(Keys.Wall);
        var growth = _graph.Get(Keys.Growth);
        var roads = _graph.Get(Keys.Roads);
        if (ReferenceEquals(_treeMaskSource, roads) || _terrain == null)
            return;
        _treeMaskSource = roads;
        var town = wall == null ? null : growth == null ? wall.Interior : wall.Interior.Union(growth.Footprint);
        if (town != null && _graph.Get(Keys.Landmarks) is { } landmarks)
            town = town.Union(landmarks.Plots);
        _terrain.SetExclusion(town == null ? null : new Exclusion(town.Contains, town.Bounds,
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
        foreach (var style in FieldStyles.All.Where(s => s.Probe))
        {
            if (style.Value(_graph) is { } field)
                parts.Add(style.Describe(field, hit));
        }

        return string.Join("   ", parts);
    }
}
