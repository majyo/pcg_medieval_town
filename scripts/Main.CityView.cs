using Godot;
using System;
using System.Linq;
using PcgCitySandbox.Pcg;

namespace PcgCitySandbox;

public partial class Main
{
    private CityPresentation _cityPresentation = null!;
    private Node3D _legacyPreviews = null!;
    private OptionButton _cityMode = null!, _cityTheme = null!, _cityWalls = null!, _citySelector = null!;
    private CheckButton _cityRoof = null!, _cityDetails = null!, _cityLabels = null!, _cityTerrain = null!;
    private CheckButton _cityShow = null!;
    private PanelContainer _cityToolbar = null!, _cityInspector = null!, _terrainPanel = null!;
    private Label _cityStatus = null!, _cityInfo = null!;
    private string? _citySelection;
    private object? _citySelectionSource;
    private bool _cityHidden;
    private string? _cityIssueText;
    private Vector2 _cityIssuePosition;
    private double _cityTimer;
    private void BuildCityInterface(Control root)
    {
        _legacyPreviews = new Node3D { Name = "NodeAnalysisPreviews" }; AddChild(_legacyPreviews);
        foreach (var preview in new Node3D[] { _samplePreview, _layoutPreview, _townPreview, _roadPreview, _parcelPreview, _buildingPreview, _placementPreview, _gradingPreview }) preview.Reparent(_legacyPreviews);
        _cityPresentation = new CityPresentation { Name = "CityPresentation", Settings = GD.Load<CityVisualSettings>("res://configs/city_visual.tres") }; AddChild(_cityPresentation); _cityPresentation.Visible = false;
        _citySelector = (OptionButton)root.GetNode("PcgNodeSelector");
        _terrainPanel = (PanelContainer)root.GetNode("TerrainPanel");
        var content = _terrainPanel.GetChild<Control>(0); _terrainPanel.RemoveChild(content);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; _terrainPanel.AddChild(scroll); scroll.AddChild(content); content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _terrainPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.LeftWide); _terrainPanel.OffsetLeft = 24; _terrainPanel.OffsetRight = 326; _terrainPanel.OffsetTop = 24; _terrainPanel.OffsetBottom = -129;
        _cityToolbar = new PanelContainer { Name = "CityToolbar", Visible = false }; root.AddChild(_cityToolbar);
        _cityToolbar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide); _cityToolbar.OffsetLeft = 24; _cityToolbar.OffsetRight = -370; _cityToolbar.OffsetTop = 24;
        _cityToolbar.AddThemeStyleboxOverride("panel", Box(new Color(.075f,.12f,.1f,.96f), 9, 10));
        var rows = new VBoxContainer(); rows.AddThemeConstantOverride("separation", 5); _cityToolbar.AddChild(rows);
        var primary = new HBoxContainer(); rows.AddChild(primary);
        _cityMode = new OptionButton { Name = "CityMode", CustomMinimumSize = new(126, 32) };
        foreach (var text in new[] { "简模展示", "规划分析", "问题检查" }) _cityMode.AddItem(text); primary.AddChild(_cityMode);
        _cityTheme = new OptionButton { Name = "CityTheme", CustomMinimumSize = new(132, 32) };
        foreach (var text in new[] { "功能与用途", "道路与路口", "地块与后院", "占地与通行", "剩余空地", "防御范围" }) _cityTheme.AddItem(text); primary.AddChild(_cityTheme);
        _cityShow = new CheckButton { Name = "CityShow", Text = "结果", ButtonPressed = true }; primary.AddChild(_cityShow);
        _cityDetails = new CheckButton { Name = "CityDetails", Text = "细节", ButtonPressed = _cityPresentation.Settings.Details }; primary.AddChild(_cityDetails);
        _cityLabels = new CheckButton { Name = "CityLabelsToggle", Text = "标注", ButtonPressed = _cityPresentation.Settings.Labels }; primary.AddChild(_cityLabels);
        _cityTerrain = new CheckButton { Name = "CityTerrainToggle", Text = "底图参数" }; primary.AddChild(_cityTerrain);
        var secondary = new HBoxContainer(); rows.AddChild(secondary);
        _cityRoof = new CheckButton { Name = "CityRoofs", Text = "屋顶", ButtonPressed = true }; secondary.AddChild(_cityRoof);
        _cityWalls = new OptionButton { Name = "CityWalls", CustomMinimumSize = new(126, 32) }; foreach (var text in new[] { $"城墙 · 示意 {_cityPresentation.Settings.WallHeight:0.#}m", "低墙查看", "隐藏城墙" }) _cityWalls.AddItem(text); secondary.AddChild(_cityWalls);
        var focus = Button("聚焦选择"); focus.Name = "FocusCitySelection"; secondary.AddChild(focus); focus.Pressed += FocusCitySelection;
        var whole = Button("全城"); secondary.AddChild(whole); whole.Pressed += () => { if (_cityPresentation.Context != null) FocusCity(_cityPresentation.Context.Layout.Region?.ViewBounds ?? new Rect2(_cityPresentation.Context.Layout.Core - Vector2.One * 300, Vector2.One * 600), 1); };
        _cityStatus = Text("先运行当前节点", 12, Muted); _cityStatus.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; _cityStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart; secondary.AddChild(_cityStatus);
        _cityInspector = new PanelContainer { Name = "CitySelection", Position = new(24, 130), CustomMinimumSize = new(280, 0), Visible = false }; root.AddChild(_cityInspector);
        _cityInspector.AddThemeStyleboxOverride("panel", Box(new Color(.075f,.12f,.1f,.95f), 9, 14));
        _cityInfo = Text("", 13, Ink); _cityInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart; _cityInfo.CustomMinimumSize = new(260, 0); _cityInspector.AddChild(_cityInfo);
        _cityMode.ItemSelected += _ => UpdateCityPresentation(true); _cityTheme.ItemSelected += _ => UpdateCityPresentation(true); _cityWalls.ItemSelected += _ => UpdateCityPresentation(true);
        foreach (var check in new[] { _cityRoof, _cityDetails, _cityLabels, _cityTerrain }) check.Toggled += _ => UpdateCityPresentation(true);
        _cityShow.Toggled += visible => { _cityHidden = !visible; UpdateCityPresentation(true); };
        _citySelector.ItemSelected += _ => { _cityHidden = !_cityShow.ButtonPressed; UpdateCityPresentation(true); };
        _layoutLayer.ItemSelected += layer => CityLegacyLayer(1, (int)layer);
        _roadLayer.ItemSelected += layer => CityLegacyLayer(2, (int)layer);
        _parcelLayer.ItemSelected += layer => CityLegacyLayer(ParcelStage, (int)layer);
        _buildingLayer.ItemSelected += layer => CityLegacyLayer(BuildingStage, (int)layer);
        _placementLayer.ItemSelected += layer => CityLegacyLayer(PlacementStage, (int)layer);
        // The shared toolbar replaces these per-node view menus; callbacks remain for existing automation.
        foreach (var menu in new[] { _layoutLayer, _roadLayer, _parcelLayer, _buildingLayer, _placementLayer }) menu.Visible = false;
        foreach (var compass in root.GetChildren().OfType<MapCompass>()) { compass.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight); compass.OffsetLeft = -468; compass.OffsetRight = -358; compass.OffsetTop = -258; compass.OffsetBottom = -130; }
    }
    private void CityLegacyLayer(int node, int layer)
    {
        if (_citySelector.Selected != node) return; _cityHidden = layer == 0;
        _cityShow.SetPressedNoSignal(layer > 0);
        _cityMode.Select(layer <= 1 ? 0 : layer == 4 && node is not 1 and not ParcelStage ? 2 : 1);
        _cityTheme.Select(node == 1 ? layer == 3 ? 1 : layer == 4 ? 5 : 0 : node == 2 ? 1 : node == ParcelStage ? layer == 4 ? 4 : 2 : layer == 3 ? 3 : 0); UpdateCityPresentation(true);
    }
    private CityContext? CityContextForNode()
    {
        int stage = _citySelector.Selected; if (stage < 1 || _data == null) return null;
        ScenePlacementSet? placement = stage >= PlacementStage ? _placementNode.LastResult : null;
        BuildingSet? buildings = placement?.Buildings ?? (stage >= BuildingStage ? _buildingNode.LastResult : null);
        ParcelLayout? parcels = buildings?.Parcels ?? (stage >= ParcelStage ? _parcelNode.LastResult : null);
        RoadNetwork? roads = parcels?.Roads ?? (stage >= 2 ? _roadNode.LastResult : null);
        SettlementLayout? layout = roads?.Settlement ?? _layoutNode.LastResult;
        if (layout == null || layout.Environment.SourceRevision != _environmentRevision) return null;
        object source = (object?)placement ?? buildings ?? (object?)parcels ?? roads ?? (object)layout;
        bool stale = placement != null ? !ReferenceEquals(placement, CurrentPlacement) : buildings != null ? !ReferenceEquals(buildings, CurrentBuildings) : parcels != null ? !ReferenceEquals(parcels, CurrentParcels) : roads != null ? !ReferenceEquals(roads, CurrentRoads) : !ReferenceEquals(layout, CurrentSettlement);
        return new(_data, layout, roads, parcels, buildings, placement, source, stale, parcels?.Grading);
    }
    private void UpdateCityPresentation(bool force = false)
    {
        if (_presentationOverride || _cityPresentation == null || _data == null) return;
        bool city = _citySelector.Selected > 0 && _citySelector.Selected != GradingStage;
        _cityToolbar.Visible = city; _terrainPanel.Visible = !city || _cityTerrain.ButtonPressed;
        _terrainPanel.OffsetTop = city ? 124 : 24; _legacyPreviews.Visible = !city;
        _cityTheme.Disabled = _cityMode.Selected != 1;
        if (!city) { if(_citySelector.Selected!=GradingStage)_map.SetGradedTerrain(null); _cityPresentation.Visible = false; _cityInspector.Visible = false; _map.SetPlanningContext(false); _map.ShowForest(_trees.ButtonPressed); return; }
        if (_cityHidden) { _cityPresentation.Visible = false; _cityInspector.Visible = false; _cityStatus.Text = "结果隐藏"; _map.SetPlanningContext(false); _map.SetVegetationMask(null); _map.ShowForest(_trees.ButtonPressed); return; }
        if (_busy && !force) { _cityStatus.Text = "生成中 · 上一次结果"; return; }
        var context = CityContextForNode();
        _map.SetGradedTerrain(context?.Grading?.Graded);
        _cityPresentation.Display(context);
        _cityPresentation.SetState((CityViewMode)_cityMode.Selected, (CityAnalysisTheme)_cityTheme.Selected, _cityRoof.ButtonPressed, _cityWalls.Selected, _cityDetails.ButtonPressed, _cityLabels.ButtonPressed);
        _map.SetPlanningContext(_cityMode.Selected != 0);
        _map.ShowForest(_trees.ButtonPressed && _cityMode.Selected == 0);
        _cityStatus.Text = context == null ? "等待生成结果" : context.Stale ? "旧结果 · 请重跑节点" : $"有效 · 建筑 {context.Buildings?.Buildings.Count ?? 0}";
        if (context != null)
        {
            _map.SetVegetationMask(context.Placement?.Options.AvoidVegetation == true ? context.Placement.VegetationMask : null);
            bool compatible = _citySelectionSource == null || ReferenceEquals(_citySelectionSource, context.Buildings) || ReferenceEquals(_citySelectionSource, context.Placement) || ReferenceEquals(_citySelectionSource, context.Parcels) || ReferenceEquals(_citySelectionSource, context.Roads) || ReferenceEquals(_citySelectionSource, context.Layout);
            if (!compatible) { _citySelection = null; _citySelectionSource = null; }
            _cityPresentation.Select(_citySelection);
            _legend.Text = _cityMode.Selected switch { 1 => "规划分析 / 灰绿：住宅 · 赭黄：商住 · 灰紫：作坊 · 蓝灰：公共用途 · 青：通行", 2 => $"问题检查 / 橙叉：现有回退或跳过位置 · {context.Stale switch { true => "旧结果", false => "当前结果" }}", _ => "城市沙盘 / 点选查看来源 · F 聚焦 · 屋顶隐藏后为占地示意 · 城墙高度为示意值" };
        }
        _cityInspector.Visible = _citySelection != null && context != null && !_cityTerrain.ButtonPressed;
        if (_cityInspector.Visible) _cityInfo.Text = CitySelectionInfo(_citySelection);
    }
    private string CitySelectionInfo(string? id)
    {
        var c = _cityPresentation.Context; if (c == null || id == null) return "";
        string status = c.Stale ? "旧结果 · " : "";
        if (id == "issue") return $"{status}问题位置\n{_cityIssueText}\nX {_cityIssuePosition.X:0.0} · Z {_cityIssuePosition.Y:0.0}";
        var b = c.Buildings?.Buildings.FirstOrDefault(b => $"B{b.Id}" == id);
        if (b != null) return $"{status}B{b.Id} · {b.Program}\n{(b.OpenHall ? "通高大厅" : $"{b.Floors} 层")} · 墙高 {b.WallHeight:0.0} m\n总高 {b.WallHeight + b.RoofHeight:0.0} m · 占地 {b.Footprint.Area:0.0} m²\n来源 {(b.ParcelId.HasValue ? $"地块 P{b.ParcelId}" : b.PrecinctId)}\n片区 {b.DistrictId}\n门口 {b.Door.X:0.0}, {b.Door.Y:0.0}\n基础高差 {b.BaseY - b.MinGround:0.00} m";
        var prop = c.Placement?.Props.FirstOrDefault(p => p.Id == id); if (prop != null) return $"{status}{PlacementName(prop.Kind)} · {prop.Id}\n来源 {prop.ZoneId}\n占地 {prop.Footprint.Area:0.0} m² · 高 {prop.Height:0.0} m";
        var gate = c.Layout.Town?.Gates.FirstOrDefault(g => g.Id == id); if (gate != null) return $"{status}城门 {gate.Id}\n参数开口 {gate.Width:0.0} m\n关联入口 {gate.EntryId}\n城墙示意高度 {_cityPresentation.Settings.WallHeight:0.#} m";
        if(id=="plaza")return $"{status}核心广场 · 石铺\n设计坡度 {(c.Grading?.Plaza.Slope??0)*100:0.0}%";
        if (id == "wall") return $"{status}城防范围\n示意高度 {_cityPresentation.Settings.WallHeight:0.#} m\n占地沿用原始墙体与城门数据";
        var parcel = c.Parcels?.Parcels.FirstOrDefault(p => $"P{p.Id}" == id);
        if (parcel != null) return $"{status}地块 P{parcel.Id} · {UseName(parcel.Use)}\n街区 B{parcel.BlockId} · 片区 {parcel.DistrictId}\n面积 {parcel.Shape.Area:0.0} m²\n建筑带 {parcel.Buildable.Area:0.0} m²\n后院 {parcel.Yard?.Area ?? 0:0.0} m²\n临街 {string.Join(", ", parcel.Frontage.Select(f => $"R{f.RoadId}").Distinct())}";
        var road = c.Roads?.Edges.FirstOrDefault(r => $"R{r.Id}" == id);
        if (road != null) return $"{status}R{road.Id} · {(road.Main ? "主路" : "街巷")}\n参数宽度 {road.Width:0.0} m\n长度 {road.Length:0.0} m\n原地形纵坡 {road.MaxGrade:0.0}°\n设计纵坡 {(c.Grading?.Profile(road.Id)?.MaxGrade??0)*100:0.0}%\n节点 {road.From} → {road.To}";
        if (id.StartsWith("rest:") && c.Parcels != null && int.TryParse(id[5..], out int rest) && rest < c.Parcels.Remainders.Count) return $"{status}剩余空地 · 街区 {c.Parcels.Remainders[rest].BlockId}\n{c.Parcels.Remainders[rest].Reason}\n面积 {c.Parcels.Remainders[rest].Shape.Area:0.0} m²";
        return status + id;
    }
    private void FocusCity(Rect2 bounds, float pitch = .65f)
    {
        var p = bounds.GetCenter(); _camera.Focus = new(p.X, DisplayTerrain.HeightAt(p) + 4, p.Y);
        _camera.Size = Math.Clamp(Math.Max(bounds.Size.X, bounds.Size.Y) * 1.7f, 25, 3300); _camera.Pitch = pitch; _camera.UpdateTransform();
    }
    private void FocusCitySelection()
    {
        var c = _cityPresentation.Context; if (c == null) return;
        if (_citySelection == "issue") { FocusCity(new Rect2(_cityIssuePosition - Vector2.One * 12, Vector2.One * 24)); return; }
        var b = c.Buildings?.Buildings.FirstOrDefault(b => $"B{b.Id}" == _citySelection);
        if (b != null) { _camera.Yaw = Mathf.Atan2(-b.Inward.X, -b.Inward.Y); FocusCity(b.RoofProjection.Bounds); return; }
        var prop = c.Placement?.Props.FirstOrDefault(p => p.Id == _citySelection); if (prop != null) { FocusCity(prop.Footprint.Bounds); return; }
        var gate = c.Layout.Town?.Gates.FirstOrDefault(g => g.Id == _citySelection); if (gate != null) { _camera.Yaw = Mathf.Atan2(gate.Outward.X, gate.Outward.Y); FocusCity(gate.Passage.Bounds); return; }
        if(_citySelection=="plaza")FocusCity(c.Layout.Plaza);
        if (_citySelection == "wall") FocusCity(c.Layout.Town!.Interior.Bounds);
        var parcel = c.Parcels?.Parcels.FirstOrDefault(p => $"P{p.Id}" == _citySelection); if (parcel != null) FocusCity(parcel.Shape.Bounds);
        var road = c.Roads?.Edges.FirstOrDefault(r => $"R{r.Id}" == _citySelection); if (road != null) { var bounds = new Rect2(road.Points[0], Vector2.Zero); foreach (var p in road.Points) bounds = bounds.Expand(p); FocusCity(bounds.Grow(8)); }
    }
    private string? PickCityObject(Vector2 screen)
    {
        var c = _cityPresentation.Context; if (c == null) return null; var p = PickSettlementGround(screen);
        if (_cityMode.Selected == 2 && p.HasValue)
        {
            var hit = _cityPresentation.Issues().Select(n => (Issue:n, Distance:_camera.UnprojectPosition(new Vector3(n.Position.X, DisplayTerrain.HeightAt(n.Position)+.065f, n.Position.Y)).DistanceTo(screen))).OrderBy(n => n.Distance).FirstOrDefault();
            if (hit.Issue.Text != null && hit.Distance < 8)
            { _cityIssueText = hit.Issue.Text; _cityIssuePosition = hit.Issue.Position; return "issue"; }
        }
        if (p.HasValue && (_citySelector.Selected == ParcelStage || _cityMode.Selected == 1 && _cityTheme.Selected is 2 or 4))
        {
            if (_cityTheme.Selected == 4 && c.Parcels != null) for (int i = 0; i < c.Parcels.Remainders.Count; i++) if (c.Parcels.Remainders[i].Shape.Contains(p.Value)) return $"rest:{i}";
            var parcel = c.Parcels?.Parcels.FirstOrDefault(a => a.Shape.Contains(p.Value)); return parcel != null ? $"P{parcel.Id}" : null;
        }
        if (p.HasValue && _citySelector.Selected == 2 && c.Roads != null)
        {
            var entry = c.Layout.Entries.FirstOrDefault(e => e.Position.DistanceTo(p.Value) < 12); if (!string.IsNullOrEmpty(entry.Id)) return entry.Id;
            var surface = c.Roads.Geometry.Surfaces.FirstOrDefault(s => s.Shape.Contains(p.Value)); return surface != null ? $"R{surface.RoadId}" : null;
        }
        return _cityPresentation.Pick(_camera.ProjectRayOrigin(screen), _camera.ProjectRayNormal(screen), p);
    }
    private bool HandleCityClick(InputEvent ev)
    {
        if (_cityPresentation == null || !_cityPresentation.Visible || _busy || _citySelector.Selected < 2 || ev is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse) return false;
        var c = _cityPresentation.Context!; var ground = PickSettlementGround(mouse.Position);
        _citySelection = PickCityObject(mouse.Position);
        _citySelectionSource = _citySelection == "issue" ? c.Source : _citySelection?.StartsWith("P") == true || _citySelection?.StartsWith("rest:") == true ? c.Parcels : _citySelection?.StartsWith("R") == true || _citySelection?.StartsWith("E") == true ? c.Roads : _citySelection?.StartsWith("B") == true ? c.Buildings : _citySelection == "wall" || _citySelection?.StartsWith("G") == true ? c.Layout : c.Placement;
        if (_citySelector.Selected == ParcelStage) _selectedParcel = _citySelection?.StartsWith("P") == true && int.TryParse(_citySelection[1..], out int parcelId) ? parcelId : -1;
        if (_citySelector.Selected == 2) { _roadSelectedAnchor = _citySelection?.StartsWith("E") == true ? _citySelection : null; _roadSelectedEdge = _citySelection?.StartsWith("R") == true && int.TryParse(_citySelection[1..], out int roadId) ? roadId : -1; }
        if (_citySelector.Selected == BuildingStage) _selectedBuilding = _citySelection?.StartsWith("B") == true && int.TryParse(_citySelection[1..], out int id) ? id : -1;
        if (_citySelector.Selected == PlacementStage) _selectedPlacement = _citySelection;
        _cityPresentation.Select(_citySelection); UpdateCityPresentation(true); GetViewport().SetInputAsHandled(); return true;
    }
    private bool UpdateCityProbe(Vector2 screen)
    {
        if (_cityPresentation == null || !_cityPresentation.Visible || _citySelector.Selected < 2) return false;
        var id = PickCityObject(screen);
        _cityPresentation.Hover(id); _probe.Text = id == null ? "城市沙盘 / 点击建筑、设施或城门查看来源" : CitySelectionInfo(id).Replace('\n', ' '); return true;
    }
}
