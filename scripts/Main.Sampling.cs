using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;

namespace PcgCitySandbox;

public partial class Main
{
    private readonly EnvironmentSampleNode _sampleNode = new();
    private TemperateEnvironmentSource? _sampleSource;
    private long _environmentRevision;
    private EnvironmentSamplePreview _samplePreview = null!;
    private SpinBox _regionX = null!, _regionZ = null!, _regionWidth = null!, _regionDepth = null!, _sampleSpacing = null!, _sampleSlope = null!;
    private readonly List<SpinBox> _sampleInputs = new();
    private CheckButton _sampleVegetation = null!, _sampleGrid = null!;
    private Button _sampleRun = null!, _sampleCancel = null!, _sampleBounds = null!;
    private OptionButton _sampleLayer = null!;
    private OptionButton _sampleReasonFilter = null!;
    private Label _sampleState = null!, _sampleSummary = null!, _sampleSourceLabel = null!;
    private CancellationTokenSource? _sampleCancellation;
    private bool _settingSampleBounds;

    public CityEnvironmentData? SampledEnvironment => _sampleSource != null && _sampleNode.IsCurrent(_sampleSource, SampleOptions()) ? _sampleNode.LastResult : null;

    private EnvironmentSampleOptions SampleOptions() => new(
        new Rect2((float)_regionX.Value - (float)_regionWidth.Value / 2, (float)_regionZ.Value - (float)_regionDepth.Value / 2,
            (float)_regionWidth.Value, (float)_regionDepth.Value), (float)_sampleSpacing.Value, (float)_sampleSlope.Value, _sampleVegetation.ButtonPressed);

    private void BuildSamplingInterface(Control root)
    {
        var settings = GD.Load<EnvironmentSampleSettings>("res://configs/environment_sample.tres");
        _samplePreview = new EnvironmentSamplePreview { Name = "EnvironmentSamplingPreview" }; AddChild(_samplePreview);
        var panel = new PanelContainer { Name = "SamplingPanel", CustomMinimumSize = new Vector2(320, 0) };
        root.AddChild(panel); panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);
        panel.OffsetLeft = -344; panel.OffsetRight = -24; panel.OffsetTop = 24; panel.OffsetBottom = -129;
        panel.AddThemeStyleboxOverride("panel", Box(new Color(0.075f, 0.12f, 0.10f, 0.96f), 12, 18));
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, Name = "SamplingScroll" }; panel.AddChild(scroll);
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 6); scroll.AddChild(col);
        col.AddChild(Text("CITY PCG  /  NODE 01", 12, Accent));
        col.AddChild(Text("环境采样", 25, Ink));
        _sampleSourceLabel = Text("输入：等待测试地图", 12, Muted); col.AddChild(_sampleSourceLabel);
        col.AddChild(new HSeparator());
        SpinBox Field(string label, double min, double max, double value, double step = 1)
        {
            var row = new HBoxContainer(); col.AddChild(row);
            var caption = Text(label, 13, Muted); caption.CustomMinimumSize = new Vector2(128, 0); row.AddChild(caption);
            var spin = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(120, 30) };
            row.AddChild(spin); _sampleInputs.Add(spin); spin.ValueChanged += _ => SamplingParametersChanged(); return spin;
        }
        var center = settings.Bounds.GetCenter();
        _regionX = Field("中心 X / 米", -10000, 10000, center.X); _regionX.Name = "SampleCenterX";
        _regionZ = Field("中心 Z / 米", -10000, 10000, center.Y);
        _regionWidth = Field("范围宽 / 米", 1, 10000, settings.Bounds.Size.X);
        _regionDepth = Field("范围深 / 米", 1, 10000, settings.Bounds.Size.Y);
        _sampleSpacing = Field("采样间距 / 米", 0.5, 500, settings.TargetSpacing, 0.5); _sampleSpacing.Name = "SampleSpacing";
        _sampleSlope = Field("坡度上限 / 度", 0, 90, settings.MaxGroundSlope, 0.5);
        _sampleBounds = Button("取当前地图范围"); col.AddChild(_sampleBounds);
        _sampleBounds.Pressed += SetSampleBoundsToMap;
        _sampleVegetation = new CheckButton { Text = "读取植被密度", ButtonPressed = settings.ReadVegetation, FocusMode = Control.FocusModeEnum.None };
        col.AddChild(_sampleVegetation); _sampleVegetation.Toggled += _ => SamplingParametersChanged();
        var buttons = new HBoxContainer(); col.AddChild(buttons);
        _sampleRun = Button("独立运行采样"); _sampleRun.Name = "RunSampling"; _sampleRun.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _sampleCancel = Button("取消"); _sampleCancel.Disabled = true;
        buttons.AddChild(_sampleRun); buttons.AddChild(_sampleCancel);
        _sampleRun.Pressed += async () => await RunSamplingAsync();
        _sampleCancel.Pressed += () => _sampleCancellation?.Cancel();
        _sampleState = Text("未运行", 13, Accent); _sampleState.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(_sampleState);
        col.AddChild(new HSeparator());
        col.AddChild(Text("采样结果预览", 12, Muted));
        _sampleLayer = new OptionButton { CustomMinimumSize = new Vector2(0, 34), Name = "SamplingLayer" };
        foreach (string name in new[] { "隐藏结果", "基础建设状态", "采样高程", "最大采样坡度", "水域", "植被密度", "限制原因" }) _sampleLayer.AddItem(name);
        col.AddChild(_sampleLayer); _sampleLayer.ItemSelected += value => SetSampleLayer((EnvironmentPreviewLayer)value);
        _sampleReasonFilter = new OptionButton { Visible = false, CustomMinimumSize = new Vector2(0, 30) };
        foreach (string name in new[] { "全部限制原因", "仅水域", "仅坡度超限", "仅禁建区", "仅固定障碍", "仅缺失数据" }) _sampleReasonFilter.AddItem(name);
        _sampleReasonFilter.ItemSelected += value => _samplePreview.SetRestrictionFilter((int)value switch
        {
            1 => EnvironmentRestriction.Water, 2 => EnvironmentRestriction.TooSteep, 3 => EnvironmentRestriction.NoBuildZone,
            4 => EnvironmentRestriction.Occupied, 5 => EnvironmentRestriction.UnknownLayer, _ => EnvironmentRestriction.None
        });
        col.AddChild(_sampleReasonFilter);
        _sampleGrid = new CheckButton { Text = "显示采样网格", FocusMode = Control.FocusModeEnum.None };
        col.AddChild(_sampleGrid); _sampleGrid.Toggled += v => _samplePreview.SetGrid(v);
        _sampleSummary = Text("运行后显示覆盖、面积与查询统计", 11, Muted);
        _sampleSummary.AddThemeConstantOverride("line_spacing", 4); col.AddChild(_sampleSummary);
        col.AddChild(Text("绿：候选  ·  红：受限  ·  灰：未知", 12, Accent));
    }

    private void BindSamplingEnvironment()
    {
        // This test fixture explicitly has no authored forbidden zones or fixed obstacles.
        _sampleSource = new TemperateEnvironmentSource(_data, "temperate_test_map", ++_environmentRevision,
            Array.Empty<ConstraintPolygon>(), Array.Empty<ConstraintPolygon>());
        _sampleNode.Invalidate(); InvalidateSettlement(); RefreshSamplingState();
    }

    private void SetSampleBoundsToMap()
    {
        if (_sampleSource == null || _busy) return;
        _settingSampleBounds = true;
        var b = _sampleSource.Bounds; _regionX.Value = b.GetCenter().X; _regionZ.Value = b.GetCenter().Y;
        _regionWidth.Value = b.Size.X; _regionDepth.Value = b.Size.Y;
        _settingSampleBounds = false; SamplingParametersChanged();
    }

    private void SamplingParametersChanged()
    {
        if (_settingSampleBounds || _sampleState == null) return;
        _sampleNode.Invalidate(); InvalidateSettlement(); RefreshSamplingState();
    }

    private void RefreshSamplingState()
    {
        if (_sampleSource != null) _sampleSourceLabel.Text = $"输入：测试地图 v{_sampleSource.Revision}  ·  只读";
        bool valid = _sampleSource != null && _sampleNode.IsCurrent(_sampleSource, SampleOptions());
        string state = _sampleNode.State switch
        {
            SampleNodeState.Valid => "有效", SampleNodeState.Stale => "已过期 · 请重新采样", SampleNodeState.Running => "采样中…",
            SampleNodeState.Failed => "失败 · " + _sampleNode.Error, SampleNodeState.Cancelled => "已取消", _ => "未运行"
        };
        _sampleState.Text = state + (_sampleNode.LastResult != null && !valid ? "\n当前显示旧结果" : "");
        var d = _sampleNode.LastResult;
        if (d == null) return;
        var s = d.Summary; double area = d.CellArea / 1_000_000;
        _sampleSummary.Text = $"{d.CellsX} × {d.CellsZ} 单元  ·  {d.StepX:0.##} × {d.StepZ:0.##} m\n" +
            $"覆盖 {(double)s.CompleteCells / (d.CellsX * d.CellsZ):P1}  ·  来源 v{d.SourceRevision}\n" +
            $"候选 {s.CandidateCells * area:0.000}  /  受限 {s.BlockedCells * area:0.000} km²\n" +
            $"未知 {s.UnknownCells * area:0.000} km²\n" +
            $"查询 {s.SourceQueryCount:N0} 次  ·  {s.ElapsedSeconds:0.00} s";
    }

    private async Task RunSamplingAsync()
    {
        if (_busy || _sampleSource == null) return;
        InvalidateSettlement();
        _busy = true; _sampleCancellation = new CancellationTokenSource();
        SetSamplingBusy(true); SetBuildingBusy(true);if(_buildingCancel!=null)_buildingCancel.Disabled=true;SetParcelBusy(true); if(_parcelCancel!=null)_parcelCancel.Disabled=true; SetRoadBusy(true); if(_roadCancel!=null)_roadCancel.Disabled=true; SetLayoutBusy(true); _layoutCancel.Disabled=true;
        try
        {
            var result = await _sampleNode.RunAsync(_sampleSource, SampleOptions(), new PcgContext(_sampleCancellation.Token, async p =>
            {
                _sampleState.Text = $"{p.Message}  {p.Fraction:P0}";
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }));
            _samplePreview.SetResult(result);
            SetSampleLayer(_sampleLayer.Selected == 0 ? EnvironmentPreviewLayer.Candidates : (EnvironmentPreviewLayer)_sampleLayer.Selected);
            GD.Print($"ENVIRONMENT_SAMPLE_READY revision={_sampleNode.ResultRevision} source={result.SourceRevision} cells={result.CellsX}x{result.CellsZ} queries={result.Summary.SourceQueryCount} elapsed={result.Summary.ElapsedSeconds:F3}s");
        }
        catch (OperationCanceledException) { GD.Print("ENVIRONMENT_SAMPLE_CANCELLED"); }
        catch (Exception ex) { GD.PrintErr("ENVIRONMENT_SAMPLE_FAILED: " + ex.Message); }
        finally
        {
            _sampleCancellation.Dispose(); _sampleCancellation = null; _busy = false;
            SetSamplingBusy(false); SetLayoutBusy(false); SetRoadBusy(false); SetParcelBusy(false);SetBuildingBusy(false);RefreshBuildings(); RefreshParcels(); RefreshRoads(); RefreshSamplingState(); RefreshSettlement();
        }
    }

    private void SetSamplingBusy(bool running)
    {
        SetPlacementBusy(running);SetGradingBusy(running);if(_gradingCancel!=null)_gradingCancel.Disabled=true;
        foreach (var input in _sampleInputs) input.Editable = !running;
        _sampleVegetation.Disabled = running; _sampleBounds.Disabled = running; _sampleRun.Disabled = running;
        _sampleCancel.Disabled = !running; _seed.Editable = !running;
        if (FindChild("RebuildButton", true, false) is Button rebuild) rebuild.Disabled = running;
    }

    private void SetSampleLayer(EnvironmentPreviewLayer layer)
    {
        if(layer!=EnvironmentPreviewLayer.Hidden){HidePlacement();HideBuildings();HideRoads();HideParcels();if(_townPreview!=null)_townPreview.Visible=false;}
        if(layer!=EnvironmentPreviewLayer.Hidden && _layoutPreview!=null){_layoutLayer.Select(0);_layoutPreview.Display(_layoutNode.LastResult,0,false);}
        _sampleLayer.Select((int)layer); _samplePreview.SetLayer(layer);
        _sampleReasonFilter.Visible = layer == EnvironmentPreviewLayer.Restrictions;
        if (layer != EnvironmentPreviewLayer.Hidden && _sampleNode.LastResult != null)
        {
            _trees.SetPressedNoSignal(false); _map.ShowForest(false);
            _legend.Text = layer switch
            {
                EnvironmentPreviewLayer.Candidates => "环境采样 / 绿色：候选 · 红色：受限 · 灰色：未知    |    宏观采样，非精确建设许可",
                EnvironmentPreviewLayer.Water => "环境采样 / 蓝色：检测到水域 · 米色：采样点为陆地 · 灰色：未知",
                EnvironmentPreviewLayer.Vegetation => "环境采样 / 浅黄：稀疏 → 深绿：密集 · 灰色：未知",
                EnvironmentPreviewLayer.Restrictions => "限制原因 / 蓝：水域 · 红：坡度 · 橙：禁建 · 紫：障碍 · 灰：未知",
                EnvironmentPreviewLayer.Elevation => "采样高程 / 蓝：低 → 黄：高    |    显示采样数据，不修改已有地形",
                _ => "最大采样坡度 / 绿：平缓 → 黄：12° → 红：30°    |    每单元四角及中心汇总"
            };
        }
        else UpdateLegend((TerrainView)_view.Selected);
    }

    private bool UpdateSamplingProbe(Vector2 p)
    {
        var data = _sampleNode.LastResult;
        if (data == null || _sampleLayer.Selected == 0) return false;
        string prefix = SampledEnvironment != null ? "采样" : "旧采样";
        if (!data.TryGetCell(p, out var cell)) { _probe.Text = prefix + " / 光标在采样范围外"; return true; }
        string height = data.TryHeightAt(p, out float h) ? $"{h:0.0} m" : "未知";
        string slope = cell.MaxSlope.HasValue ? $"{cell.MaxSlope:0.0}°" : "未知";
        string water = cell.Water.HasValue ? cell.Water.Value ? "水域" : "陆地" : "水域未知";
        string forest = cell.Vegetation.HasValue ? $"{cell.Vegetation:P0}" : "未知";
        string state = cell.State switch { BuildCandidateState.Candidate => "候选", BuildCandidateState.Blocked => "受限", _ => "未知" };
        int x = Math.Min((int)((p.X - data.Bounds.Position.X) / data.StepX), data.CellsX - 1);
        int z = Math.Min((int)((p.Y - data.Bounds.Position.Y) / data.StepZ), data.CellsZ - 1);
        _probe.Text = $"{prefix} [{x},{z}]  X {p.X:0}  Z {p.Y:0}  |  高程 {height}  坡度 {slope}  {water}  植被 {forest}  |  {state} · {ReasonText(cell.Reasons)}";
        return true;
    }

    private static string ReasonText(EnvironmentRestriction reasons)
    {
        if (reasons == EnvironmentRestriction.None) return "未发现限制";
        var names = new List<string>();
        foreach (var (flag, name) in new[] { (EnvironmentRestriction.NoGroundData, "无地面"), (EnvironmentRestriction.UnknownLayer, "缺少数据"),
            (EnvironmentRestriction.Water, "水域"), (EnvironmentRestriction.TooSteep, "坡度超限"), (EnvironmentRestriction.NoBuildZone, "禁建"), (EnvironmentRestriction.Occupied, "固定障碍") })
            if (reasons.HasFlag(flag)) names.Add(name);
        return string.Join(" / ", names);
    }

    private async Task VerifySamplingInterface()
    {
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("SAMPLING_UI_FAILED: " + message); }
        async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        async Task Click(Control control)
        {
            Vector2 p = control.GetGlobalRect().GetCenter();
            GetViewport().PushInput(new InputEventMouseMotion { Position = p, GlobalPosition = p }, true);
            GetViewport().PushInput(new InputEventMouseButton { Position = p, GlobalPosition = p, ButtonIndex = MouseButton.Left, Pressed = true }, true);
            await Frame();
            GetViewport().PushInput(new InputEventMouseButton { Position = p, GlobalPosition = p, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            await Frame();
        }
        async Task Finish()
        {
            int frames = 0;
            while (_busy && frames++ < 3000) await Frame();
            Check(!_busy, "sampling completion timeout");
        }
        var map = _data; long sourceRevision = _environmentRevision;
        Check(_sampleSpacing.Value == 10 && _regionWidth.Value == 2000 && _regionDepth.Value == 2000, "camera input must not change sample settings");
        await Click(_sampleRun); await Finish();
        Check(SampledEnvironment?.CellsX == 200 && _samplePreview.Visible, "run button and default preview");
        Check(ReferenceEquals(map, _data) && sourceRevision == _environmentRevision, "sampling did not regenerate map");
        long outputRevision = _sampleNode.ResultRevision;
        _sampleLayer.Select((int)EnvironmentPreviewLayer.Water);
        _sampleLayer.EmitSignal(OptionButton.SignalName.ItemSelected, (long)EnvironmentPreviewLayer.Water);
        await Click(_sampleGrid);
        Check(_samplePreview.Layer == EnvironmentPreviewLayer.Water && _samplePreview.ShowGrid, "layer and grid callbacks");
        Check(_sampleNode.ResultRevision == outputRevision, "display change does not sample");
        SetSampleLayer(EnvironmentPreviewLayer.Restrictions);
        _sampleReasonFilter.Select(2); _sampleReasonFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 2L);
        Check(_samplePreview.RestrictionFilter == EnvironmentRestriction.TooSteep && _sampleNode.ResultRevision == outputRevision, "reason filter does not resample");
        _sampleReasonFilter.Select(0); _sampleReasonFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
        SetSampleLayer(EnvironmentPreviewLayer.Candidates);
        _sampleSpacing.Value = 20;
        Check(SampledEnvironment == null && _sampleState.Text.Contains("过期"), "editing parameters invalidates output");
        await Click(_sampleRun); await Finish();
        Check(SampledEnvironment?.CellsX == 100, "new resolution");
        await Click(_sampleRun); await Click(_sampleCancel); await Finish();
        Check(_sampleNode.State == SampleNodeState.Cancelled && SampledEnvironment == null, "interactive cancellation keeps stale preview");
        _sampleSpacing.Value = 10; await Click(_sampleRun); await Finish();
        Check(SampledEnvironment != null, "rerun after cancellation");
        var prior = _sampleNode.LastResult;
        await Click((Control)FindChild("RebuildButton", true, false)); await Finish();
        Check(_environmentRevision == sourceRevision + 1 && SampledEnvironment == null && ReferenceEquals(prior, _sampleNode.LastResult), "map rebuild invalidates and preserves old preview");
        Check(_sampleState.Text.Contains("旧结果"), "stale preview is labeled");
        await Click(_sampleRun); await Finish();
        Check(SampledEnvironment?.SourceRevision == _environmentRevision, "new map version sampled");
        SetSampleLayer(EnvironmentPreviewLayer.Hidden); _sampleGrid.SetPressedNoSignal(false); _samplePreview.SetGrid(false);
        _trees.SetPressedNoSignal(true); _map.ShowForest(true);
        GD.Print("SAMPLING_UI_PASS run=ok controls=ok cancel=ok stale=ok map_isolation=ok");
    }
}
