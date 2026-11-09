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
/// map, the town graph, the city view and the atmosphere. Stages are the workflow (terrain, then the graph's node
/// groups); each stage page shows the visualization that suits it, and with "联动视图" on, selecting a stage
/// switches the scene to that view.
/// </summary>
public partial class Main
{
    private const string TerrainStage = "地形";

    private SandboxLayout? _ui;
    private List<Stage> _stages = null!;
    private Label _mapInfo = null!;
    private readonly Setting<int> _seed = new(0);
    private readonly Setting<int> _stage = new(0);
    private readonly Setting<bool> _auto = new(true), _linkView = new(true);
    private readonly Debounce _debounce = new() { Name = "Debounce" };

    /// <summary>
    /// What each stage shows when selected (with "联动视图") or on "显示到此阶段": a ground coloring (null keeps a
    /// sampled field if one is showing, else the first field) and the city layers to show; all others are hidden.
    /// </summary>
    private static readonly Dictionary<string, (int? Ground, CityLayer[] Layers)> Views = new()
    {
        [TerrainStage] = (0, Array.Empty<CityLayer>()),
        ["场地采样"] = (null, new[] { CityLayer.Contours }),
        ["场地"] = (0, new[] { CityLayer.Site }),
        ["城镇布局"] = (0, new[] { CityLayer.Layout }),
        ["路网"] = (0, new[] { CityLayer.Layout, CityLayer.Roads }),
        ["街区与地块"] = (0, new[] { CityLayer.Layout, CityLayer.Roads, CityLayer.Blocks, CityLayer.Parcels }),
        ["建筑"] = (0, new[] { CityLayer.Layout, CityLayer.Roads, CityLayer.Buildings }),
    };

    private static readonly (CityLayer Layer, string Name)[] LayerNames =
    {
        (CityLayer.Contours, "等高线"), (CityLayer.Site, "场地限制"), (CityLayer.Layout, "广场与城墙"), (CityLayer.Roads, "道路"),
        (CityLayer.Blocks, "街区"), (CityLayer.Parcels, "地块"), (CityLayer.Buildings, "建筑"),
    };

    private static string[] GroundNames =>
        new[] { "地表", "地形适宜性" }.Concat(FieldStyles.All.Select(s => "采样 · " + s.Name)).ToArray();

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
        foreach (var stage in _stages)
            stage.Visualization = ins => StageVisualization(ins, stage.Title);
        _stages[0].Parameters = TerrainParameters;
        _stages[0].Caption = () => $"种子 {_map.Settings.Seed}";

        BuildToolbar(_ui.Toolbar);
        _ui.Rail.Bind(_graph, _stages, _stage);

        var page = new StagePage();
        page.Bind(_graph, _stages, _stage, header => header.Toggle("联动视图", _linkView, "选中阶段时自动切换到适合它的显示方式"));
        page.Edited += ScheduleRun;
        page.RunRequested += () => _ = _graph.Run();
        _ui.Rail.NodeClicked += page.Reveal;
        _ui.AddTab("阶段", page);
        _ui.AddTab("视图", Scrolling(ViewPage()));
        _ui.AddTab("环境", Scrolling(EnvironmentPage()));

        _ui.Compass.Camera = _camera;
        _ui.Compass.MetersPerPixel = _camera.MetersPerPixel;
        _ui.Compass.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        _ui.Status.Hint = "滚轮缩放 · WASD/中键平移 · 右键/QE 旋转 · H 隐藏界面";

        _stage.Changed += index =>
        {
            if (_linkView.Value)
                ShowStageView(_stages[index].Title);
        };
        _stage.Value = _stages.Count - 1;
        ShowStageView(_stages[^1].Title);
        BuildShortcuts();
    }

    private void BuildToolbar(Toolbar bar)
    {
        bar.AddToggle("阶段", "显示 / 隐藏左侧的阶段列表", _ui!.ShowRail);
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
            keys.Map(Key.Key1 + i, () => _stage.Value = index);
        }

        keys.Map(Key.F5, () => _ = _graph.Run());
        keys.Map(Key.Escape, _graph.Cancel);
        keys.Map(Key.H, () => _ui!.Visible = !_ui.Visible);
        keys.Map(Key.T, _camera.TopView);
        keys.Map(Key.F, () => FocusOnTown());
    }

    private void ScheduleRun()
    {
        if (_auto.Value)
            _debounce.Schedule(() => _ = _graph.Run(), 0.35);
    }

    /// <summary>Applies the stage's view: its ground coloring and exactly its city layers.</summary>
    private void ShowStageView(string stage)
    {
        if (!Views.TryGetValue(stage, out var view))
            return;
        _ground.Value = view.Ground ?? (_ground.Value >= 2 ? _ground.Value : 2);
        foreach (var (layer, setting) in _layers)
            setting.Value = view.Layers.Contains(layer);
    }

    /// <summary>The visualization section of a stage page: the view button, then the toggles that matter there.</summary>
    private void StageVisualization(Inspector ins, string stage)
    {
        if (Views.ContainsKey(stage))
            ins.Buttons(("显示到此阶段", "切换到适合这个阶段的地面着色和图层", () => ShowStageView(stage)));
        void Layer(CityLayer layer) => ins.Toggle(LayerNames.First(l => l.Layer == layer).Name, _layers[layer]);
        switch (stage)
        {
            case TerrainStage:
                ins.Toggle("地形适宜性着色", () => _ground.Value == 1, on => _ground.Value = on ? 1 : 0);
                _ground.Changed += _ => ins.Refresh();
                ins.Toggle("森林", _forest);
                ins.Toggle("河面", _water);
                ins.Toggle("地被与岩石", _cover);
                break;
            case "场地采样":
                var fields = new[] { "（不着色）" }.Concat(FieldStyles.All.Select(s => s.Name)).ToArray();
                ins.Choice("地面着色", fields, () => _ground.Value >= 2 ? _ground.Value - 1 : 0, i => _ground.Value = i == 0 ? 0 : i + 1,
                    "把一个采样场的值画在地面上");
                _ground.Changed += _ => ins.Refresh();
                Layer(CityLayer.Contours);
                ins.Slider("等高距 m", 1, 10, 0.5, () => _city.ContourInterval, v =>
                {
                    _city.ContourInterval = (float)v;
                    UpdateLegend();
                }, 2);
                break;
            case "场地":
                Layer(CityLayer.Site);
                ins.Toggle("地形适宜性着色", () => _ground.Value == 1, on => _ground.Value = on ? 1 : 0);
                _ground.Changed += _ => ins.Refresh();
                break;
            case "城镇布局":
                Layer(CityLayer.Layout);
                Layer(CityLayer.Site);
                break;
            case "路网":
                Layer(CityLayer.Roads);
                Layer(CityLayer.Layout);
                break;
            case "街区与地块":
                Layer(CityLayer.Blocks);
                Layer(CityLayer.Parcels);
                Layer(CityLayer.Roads);
                break;
            case "建筑":
                Layer(CityLayer.Buildings);
                Layer(CityLayer.Parcels);
                Layer(CityLayer.Roads);
                ins.Toggle("森林", _forest);
                break;
        }
    }

    /// <summary>
    /// The terrain stage's settings: seed and the map's shape. They feed map generation (the whole graph depends on
    /// the map), so a change rebuilds the map: after a short pause with "自动" on, otherwise on "重建地图".
    /// </summary>
    private void TerrainParameters(Inspector ins)
    {
        ins.Number("种子", 0, 999999, () => _seed.Value, v => _seed.Value = v, dice: true, tooltip: "地图种子");
        _seed.Changed += _ => ins.Refresh();
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
                pending.Text = "地图参数已修改，点“重建地图”生效";
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
        ins.Buttons(("重建地图", "按当前种子和参数重新生成地形和城镇", () => RebuildMap(_seed.Value)));
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

    /// <summary>Every display option in one place, whatever the stage.</summary>
    private Inspector ViewPage()
    {
        var page = new Inspector();
        var ground = page.Section("地面");
        ground.Choice("着色", GroundNames, _ground, "地表：手绘风格的地面；其余把分析结果画在地面上");
        var layers = page.Section("城市图层");
        foreach (var (layer, name) in LayerNames)
            layers.Toggle(name, _layers[layer]);
        layers.Slider("等高距 m", 1, 10, 0.5, () => _city.ContourInterval, v =>
        {
            _city.ContourInterval = (float)v;
            UpdateLegend();
        }, 2);
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
