using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.City;
using PcgCitySandbox.Rendering;
using PcgCitySandbox.Ui;

namespace PcgCitySandbox;

/// <summary>
/// The interface half of the composition root: lays out the generic widgets from <c>ui/</c> and binds them to the
/// map, the town graph, the city view and the atmosphere. The node graph along the bottom drives what the scene
/// shows: click a node and only its outputs are drawn; click a stage header for all outputs of that stage; click
/// empty space for the whole town as the "视图" page sets it.
/// </summary>
public partial class Main
{
    private const string TerrainStage = "地形";

    private SandboxLayout? _ui;
    private StagePage _page = null!;
    private List<Stage> _stages = null!;
    private Label _mapInfo = null!;
    private readonly Setting<int> _seed = new(0);
    private readonly Setting<int> _stage = new(0);
    private readonly Setting<bool> _auto = new(true);
    private readonly Debounce _debounce = new() { Name = "Debounce" };

    private static readonly (CityLayer Layer, string Name)[] LayerNames =
    {
        (CityLayer.Contours, "等高线"), (CityLayer.Region, "区域格局"), (CityLayer.Layout, "市场、教堂、城墙与城门"), (CityLayer.Roads, "道路"),
        (CityLayer.Blocks, "街区"), (CityLayer.Parcels, "地块"), (CityLayer.Buildings, "建筑"),
    };

    /// <summary>Menu items in <see cref="_ground"/> order: landscape, then every ground style.</summary>
    private static string[] GroundNames =>
        new[] { "地表" }.Concat(FieldStyles.All.Select(s => $"{s.Group} · {s.Name}")).ToArray();

    private void BuildInterface()
    {
        AddChild(_debounce);
        _seed.Value = _settings.Seed;
        var canvas = new CanvasLayer { Name = "UI" };
        AddChild(canvas);
        _ui = new SandboxLayout();
        canvas.AddChild(_ui);

        _stages = new List<Stage> { new(TerrainStage, Array.Empty<int>()) };
        _stages.AddRange(Stage.FromGraph(_graph));
        _stages[0].Parameters = TerrainParameters;
        _stages[0].Caption = () => $"种子 {_map.Settings.Seed}";

        BuildToolbar(_ui.Toolbar);
        _ui.Graph.Bind(_graph, _stages, _focus);

        _page = new StagePage();
        _page.Bind(_graph, _stages, _stage);
        _page.Edited += ScheduleRun;
        _page.RunRequested += () => _ = _graph.Run();
        _page.FocusRequested += node => _focus.Value = new GraphFocus(StageOf(node), node);
        _page.KeyClicked += ShowKey;
        _ui.AddTab("阶段", _page);
        _ui.AddTab("视图", Scrolling(ViewPage()));
        _ui.AddTab("环境", Scrolling(EnvironmentPage()));

        _ui.Compass.Camera = _camera;
        _ui.Compass.MetersPerPixel = _camera.MetersPerPixel;
        _ui.Compass.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        _ui.Status.Hint = "点节点图里的节点只看它的输出，点空白处看完整城镇 · 滚轮缩放 · WASD/中键平移 · 右键/QE 旋转 · H 隐藏界面";

        _focus.Changed += focus =>
        {
            if (!focus.IsNone)
                _stage.Value = focus.Stage;
            if (focus.Node >= 0)
                _page.Reveal(focus.Node);
            _page.SetFocused(focus.Node);
        };
        _stage.Value = _stages.Count - 1;
        BuildShortcuts();
    }

    private int StageOf(int node) => _stages.FindIndex(s => s.Nodes.Contains(node));

    /// <summary>
    /// Shows a key clicked on a node card: an output as that node wrote it, an input as the node it came from wrote it
    /// (focusing that node), painted on the ground when it can be.
    /// </summary>
    private void ShowKey(int node, IKey key, bool input)
    {
        int writer = input ? _graph.Sources(node).GetValueOrDefault(key, -1) : node;
        if (writer < 0)
            return;
        _focus.Value = new GraphFocus(StageOf(writer), writer);
        _focusGround = key;
        RefreshView();
    }

    private void BuildToolbar(Toolbar bar)
    {
        bar.AddToggle("节点图", "显示 / 隐藏底部的节点图 (G)", _ui!.ShowGraph);
        bar.AddTitle("中世纪城镇沙盒");
        bar.AddSeparator();
        bar.AddNumber("种子", _seed, 0, 999999, "地图种子；回车后按它重建地图");
        bar.AddButton("随机", "换一个随机种子并重建地图", () => _seed.Value = (int)GD.RandRange(0, 999999));
        bar.AddButton("重建地图", "按当前种子和地图参数重新生成地形和城镇", () => RebuildMap(_seed.Value));
        _seed.Changed += seed => RebuildMap(seed);
        bar.AddSeparator();
        bar.AddButton("生成", "运行所有未生效的节点 (F5)", () => _ = _graph.Run(), accent: true);
        bar.AddButton("取消", "停止正在运行的生成 (Esc)", _graph.Cancel);
        bar.AddToggle("自动", "改动参数后自动重新生成", _auto);
        var progress = new GraphProgress();
        progress.Bind(_graph);
        bar.Add(progress);
        bar.AddSpacer();
        bar.AddButton("全图", "复位视角 (Home)", _camera.ResetView);
        bar.AddButton("俯视", "从正上方看全图 (T)", _camera.TopView);
        bar.AddButton("城镇", "聚焦到城镇 (F)", () => FocusOnTown());
        bar.AddSeparator();
        bar.AddToggle("面板", "显示 / 隐藏右侧的参数面板", _ui.ShowInspector);
    }

    private void BuildShortcuts()
    {
        var keys = new Shortcuts { Name = "Shortcuts" };
        AddChild(keys);
        for (int i = 0; i < Math.Min(_stages.Count, 9); i++)
        {
            int index = i;
            keys.Map(Key.Key1 + i, () => _focus.Value = new GraphFocus(index, -1));
        }

        keys.Map(Key.F5, () => _ = _graph.Run());
        keys.Map(Key.Escape, () =>
        {
            if (_graph.IsRunning)
                _graph.Cancel();
            else
                _focus.Value = GraphFocus.None;
        });
        keys.Map(Key.G, () => _ui!.ShowGraph.Value = !_ui.ShowGraph.Value);
        keys.Map(Key.H, () => _ui!.Visible = !_ui.Visible);
        keys.Map(Key.T, _camera.TopView);
        keys.Map(Key.F, () => FocusOnTown());
    }

    private void ScheduleRun()
    {
        if (_auto.Value)
            _debounce.Schedule(() => _ = _graph.Run(), 0.35);
    }

    private void ContourIntervalSlider(Inspector ins) =>
        ins.Slider("等高距 m", 1, 10, 0.5, () => _city.ContourInterval, v =>
        {
            _city.ContourInterval = (float)v;
            UpdateLegend();
        }, 2);

    /// <summary>
    /// The terrain stage's settings: the map's shape. They feed map generation (the whole graph depends on the map),
    /// so a change rebuilds the map: after a short pause with "自动" on, otherwise on "重建地图" in the toolbar.
    /// </summary>
    private void TerrainParameters(Inspector ins)
    {
        var pending = ins.Note("", UiStyle.Warning);
        pending.Visible = false;
        void Changed()
        {
            if (_auto.Value)
            {
                _debounce.Schedule(() => RebuildMap(_seed.Value), 0.6);
            }
            else
            {
                pending.Text = "地图参数已修改，点顶栏的“重建地图”生效";
                pending.Visible = true;
            }
        }

        MapShown += () => pending.Visible = false;
        void Map(string label, double min, double max, double step, Func<float> get, Action<float> set, string tooltip)
        {
            float initial = get();
            ins.Slider(label, min, max, step, () => get(), v =>
            {
                set((float)v);
                Changed();
            }, initial, tooltip);
        }

        Map("地形起伏 m", 5, 50, 1, () => _settings.ReliefMeters, v => _settings.ReliefMeters = v, "区域地貌的高差");
        Map("河宽 m", 20, 100, 1, () => _settings.RiverWidthMeters, v => _settings.RiverWidthMeters = v, "河道宽度");
        Map("森林密度", 0, 1, 0.05, () => _settings.ForestDensity, v => _settings.ForestDensity = v, "林地覆盖的多少");
        Map("树木间距 m", 10, 30, 1, () => _settings.TreeSpacingMeters, v => _settings.TreeSpacingMeters = v, "林中树木的平均间距");
        _mapInfo = ins.Note("");
    }

    /// <summary>Raised after a new map is shown.</summary>
    private event Action? MapShown;

    /// <summary>Called after a new map is shown.</summary>
    private void MapChanged()
    {
        if (_ui == null)
            return;
        _mapInfo.Text = $"高程 {_map.MinHeight:0}–{_map.MaxHeight:0} m · 林地 {_map.ForestFraction:P0} · 开阔缓坡 {_map.OpenGentleFraction:P0}";
        MapShown?.Invoke();
    }

    /// <summary>What the scene shows when no node is focused, and the environment and camera in every case.</summary>
    private Inspector ViewPage()
    {
        var page = new Inspector();
        page.Note("在节点图里选中节点或阶段时，画面只显示它的输出；点节点图的空白处（或按 Esc）回到下面的设置。");
        var ground = page.Section("地面");
        ground.Choice("着色", GroundNames, _ground, "地表：手绘风格的地面；其余把分析结果画在地面上");
        var layers = page.Section("城市图层");
        foreach (var (layer, name) in LayerNames)
            layers.Toggle(name, _layers[layer]);
        ContourIntervalSlider(layers);
        var environment = page.Section("环境要素");
        environment.Toggle("森林", _forest);
        environment.Toggle("河面", _water);
        environment.Toggle("地被与岩石", _cover);
        var camera = page.Section("相机");
        float fov = _camera.Fov;
        camera.Slider("视野 °", 20, 75, 1, () => _camera.Fov, v => _camera.Fov = (float)v, fov);
        camera.Buttons(("全图", "Home", _camera.ResetView), ("俯视", "T", _camera.TopView), ("城镇", "F", () => FocusOnTown()));
        return page;
    }

    /// <summary>Lighting, sky, fog, post-processing and the painterly style, with named presets.</summary>
    private Inspector EnvironmentPage()
    {
        var page = new Inspector();
        var settings = _atmosphere.Settings;
        var presets = page.Section("预设");
        var names = AtmosphereSettings.Presets;
        for (int row = 0; row < names.Count; row += 3)
        {
            presets.Buttons(names.Skip(row).Take(3).Select(p => (p.Name, $"切换到“{p.Name}”的光照和空气", (Action)(() =>
            {
                settings.CopyFrom(p.Create());
                _atmosphere.Apply();
                page.Refresh();
            }))).ToArray());
        }

        foreach (var (title, section, open) in new (string, object, bool)[]
        {
            ("太阳", settings.Sun, true), ("天空与环境光", settings.Sky, true), ("雾", settings.Fog, true),
            ("后期", settings.Post, false), ("画风与风", settings.Style, false),
        })
        {
            page.Section(title, open).Params(section, _ => _atmosphere.Apply());
        }

        return page;
    }

    private static ScrollContainer Scrolling(Control content)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var margin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddChild(content);
        scroll.AddChild(margin);
        return scroll;
    }
}
