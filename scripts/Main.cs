using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.Core;
using PcgCitySandbox.Stages;
using PcgCitySandbox.View;

namespace PcgCitySandbox;

/// <summary>
/// Composition root: map, pipeline, preview and panels. Command line:
/// `--test` runs the headless checks; `--capture=DIR` generates the town and saves screenshots.
/// </summary>
public partial class Main : Node3D
{
    private MapSettings _settings = null!;
    private Pipeline _pipeline = null!;
    private TerrainRenderer? _terrain;
    private CityView _city = null!;
    private MapCamera _camera = null!;
    private SpinBox _seed = null!;
    private OptionButton _view = null!;
    private Label _stats = null!, _probe = null!;
    private CheckBox _forest = null!, _water = null!;
    private object? _maskSource;

    public override async void _Ready()
    {
        try
        {
            _settings = GD.Load<MapSettings>("res://configs/temperate_plain.tres");
            var args = OS.GetCmdlineUserArgs();
            if (args.Contains("--test"))
            {
                GetTree().Quit(await Checks.Run(_settings));
                return;
            }

            BuildScene();
            BuildInterface();
            NewMap();
            await _pipeline.Run();
            if (_pipeline.Get<TownLayout>() is { } town)
                Frame(town.Core, 0.83f, -0.38f, 900);

            var capture = args.FirstOrDefault(a => a.StartsWith("--capture="));
            if (capture != null)
            {
                await Capture(capture["--capture=".Length..]);
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
    }

    private void NewMap()
    {
        var map = Town.CreateMap(_settings, (int)_seed.Value);
        _terrain?.QueueFree();
        _terrain = new TerrainRenderer { Name = "Terrain" };
        AddChild(_terrain);
        _terrain.Build(map);
        _terrain.SetView((TerrainView)_view.Selected);
        _terrain.ShowForest(_forest.ButtonPressed);
        _terrain.ShowWater(_water.ButtonPressed);
        _maskSource = null;
        if (_pipeline == null)
        {
            _pipeline = Town.CreatePipeline(map);
            _pipeline.Changed += OnPipelineChanged;
            _city.Bind(_pipeline);
            var panel = new StagePanel { Name = "Stages" };
            Dock(panel, right: true);
            panel.Bind(_pipeline);
        }
        else
        {
            _pipeline.SetMap(map);
        }

        _stats.Text = $"种子 {map.Settings.Seed}\n高程 {map.MinHeight:0}–{map.MaxHeight:0} m\n" +
            $"林地 {map.ForestFraction:P0}   开阔缓坡 {map.OpenGentleFraction:P0}";
    }

    private void OnPipelineChanged()
    {
        _city.Refresh();
        // Trees disappear inside the walls and on roads once those exist.
        var layout = _pipeline.Get<TownLayout>();
        var roads = _pipeline.Get<RoadGraph>();
        if (ReferenceEquals(_maskSource, roads) || _terrain == null)
            return;
        _maskSource = roads;
        var near = roads?.Footprint.Offset(4);
        _terrain.SetTreeMask(layout == null ? null : p => layout.Interior.Contains(p) || near?.Contains(p) == true);
    }

    private void BuildInterface()
    {
        var ui = new CanvasLayer();
        AddChild(ui);
        var left = new PanelContainer { Name = "Map" };
        left.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.075f, 0.12f, 0.10f, 0.94f)));
        ui.AddChild(left);
        left.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        left.Position = new Vector2(16, 16);
        left.CustomMinimumSize = new Vector2(250, 0);

        var box = new VBoxContainer();
        left.AddChild(box);
        box.AddChild(Ui.Text("中世纪城镇沙盒", 18, Ui.Accent));
        _stats = Ui.Text("", 12, Ui.Muted);
        box.AddChild(_stats);

        var seedRow = new HBoxContainer();
        _seed = new SpinBox { MinValue = 0, MaxValue = 999999, Value = _settings.Seed, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var rebuild = Ui.Button("重建地图");
        rebuild.Pressed += () =>
        {
            NewMap();
            _ = _pipeline.Run();
        };
        seedRow.AddChild(_seed);
        seedRow.AddChild(rebuild);
        box.AddChild(seedRow);

        _view = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var name in new[] { "地表", "高程", "坡度", "适宜性" })
            _view.AddItem(name);
        _view.ItemSelected += i => _terrain?.SetView((TerrainView)i);
        box.AddChild(_view);

        _forest = Toggle(box, "森林", true, on => _terrain?.ShowForest(on));
        _water = Toggle(box, "河面", true, on => _terrain?.ShowWater(on));
        foreach (var (layer, name) in new[]
        {
            (CityLayer.Site, "场地限制"), (CityLayer.Layout, "广场与城墙"), (CityLayer.Roads, "道路"),
            (CityLayer.Parcels, "地块"), (CityLayer.Buildings, "建筑"),
        })
        {
            Toggle(box, name, _city.IsVisible(layer), on => _city.SetVisible(layer, on));
        }

        var reset = Ui.Button("复位视角 (Home)");
        reset.Pressed += () => _camera.ResetView();
        box.AddChild(reset);
        _probe = Ui.Text("", 12, Ui.Muted);
        box.AddChild(_probe);
        box.AddChild(Ui.Text("滚轮缩放 · WASD/中键平移 · 右键旋转", 11, Ui.Muted));
        ui.AddChild(new MapCompass { Camera = _camera, Position = new Vector2(280, 16) });
    }

    private void Dock(Control panel, bool right)
    {
        var ui = GetChildren().OfType<CanvasLayer>().First();
        ui.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(right ? Control.LayoutPreset.RightWide : Control.LayoutPreset.LeftWide);
        panel.OffsetLeft = -380;
        panel.OffsetRight = -16;
        panel.OffsetTop = 16;
        panel.OffsetBottom = -16;
    }

    private static CheckBox Toggle(Container parent, string text, bool on, Action<bool> changed)
    {
        var check = new CheckBox { Text = text, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None };
        check.Toggled += value => changed(value);
        parent.AddChild(check);
        return check;
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Home })
            _camera.ResetView();
    }

    public override void _Process(double delta)
    {
        if (_pipeline == null)
            return;
        var map = _pipeline.Map;
        var mouse = GetViewport().GetMousePosition();
        var origin = _camera.ProjectRayOrigin(mouse);
        var direction = _camera.ProjectRayNormal(mouse);
        // Orthographic ray: intersect the height field by fixed-point refinement on the ground height.
        float y = 15;
        var hit = Vector2.Zero;
        for (int i = 0; i < 4 && Mathf.Abs(direction.Y) > 1e-4f; i++)
        {
            var p = origin + direction * ((y - origin.Y) / direction.Y);
            hit = new Vector2(p.X, p.Z);
            if (!map.Contains(hit))
            {
                _probe.Text = "";
                return;
            }

            y = map.HeightAt(hit);
        }

        var sample = map.Sample(hit);
        var site = _pipeline.Get<Site>();
        int cell = site?.CellAt(hit) ?? -1;
        string block = cell < 0 ? "" : site!.Block[cell] switch
        {
            SiteBlock.None => "可建设",
            SiteBlock.Water => "水域",
            SiteBlock.Bank => "河岸退让",
            SiteBlock.Slope => "坡度过大",
            _ => "森林",
        };
        _probe.Text = $"({hit.X:0}, {hit.Y:0})  高程 {sample.Height:0.0} m  坡度 {sample.SlopeDegrees:0.0}°  {block}";
    }

    private async Task Capture(string directory)
    {
        var path = ProjectSettings.GlobalizePath($"res://{directory}");
        Directory.CreateDirectory(path);
        var layout = _pipeline.Get<TownLayout>();
        var core = layout?.Core ?? Vector2.Zero;
        var views = new (string Name, Action Setup)[]
        {
            ("overview", () => _camera.ResetView()),
            ("top", () => _camera.TopView()),
            ("town", () => Frame(core, 0.6f, -0.5f, 620)),
            ("street", () => Frame(core, 0.5f, 0.9f, 180)),
        };
        foreach (var (name, setup) in views)
        {
            setup();
            for (int i = 0; i < 4; i++)
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(path, name + ".png"));
        }

        GD.Print($"CAPTURED {path}");
    }

    private void Frame(Vector2 center, float pitch, float yaw, float size)
    {
        _camera.Focus = new Vector3(center.X, _pipeline.Map.HeightAt(center), center.Y);
        _camera.Pitch = pitch;
        _camera.Yaw = yaw;
        _camera.Size = size;
        _camera.UpdateTransform();
    }
}
