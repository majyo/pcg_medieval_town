using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;

namespace PcgCitySandbox;

public partial class Main : Node3D
{
	public TemperateMapData EnvironmentData => _data;
	private MapSettings _settings = null!;
	private TemperateMapData _data = null!;
	private MapRenderer _map = null!;
	private MapCamera _camera = null!;
	private Label _stats = null!, _legend = null!, _probe = null!, _status = null!;
	private SpinBox _seed = null!;
	private OptionButton _view = null!;
    private OptionButton _terrainQuality = null!;
	private CheckButton _trees = null!, _river = null!;
	private bool _busy;
	private double _probeTimer;
	private static readonly Color Ink = new("e6ede0"), Muted = new("a8b6a8"), Accent = new("cbdca0");

	public override async void _Ready()
	{
		try
		{
			_settings = GD.Load<MapSettings>("res://configs/temperate_plain.tres");
			string[] args = OS.GetCmdlineUserArgs();
            if(args.Contains("--validate-terrain-visual")){TerrainVisualValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-placement")){await ScenePlacementValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-buildings")){await BuildingValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-regional")){await RegionalValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-medieval")){await MedievalValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-parcels")){await ParcelValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-roads")){await RoadValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-road-bed")){await RoadConstructionValidation.NavigationOnly(_settings,true);GetTree().Quit();return;}
            if(args.Contains("--validate-road-navigation")){await RoadConstructionValidation.NavigationOnly(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-road-construction")){await RoadConstructionValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-road-sweep")){await RoadSweepValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-grading-limits")){await GradingLimitsValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-road-grading")){await RoadGradingValidation.Run(_settings);GetTree().Quit();return;}
            if(args.Contains("--validate-settlement")){await SettlementValidation.Run(_settings);GetTree().Quit();return;}
			if (args.Contains("--validate-environment"))
			{
				await EnvironmentSamplingValidation.Run(_settings); GetTree().Quit(); return;
			}
			if (args.Contains("--sample-environment"))
			{
				EnvironmentSamplingValidation.RunStandalone(_settings, GD.Load<EnvironmentSampleSettings>("res://configs/environment_sample.tres").Snapshot());
				GetTree().Quit(); return;
			}
			if (args.Contains("--validate-map"))
			{
				MapValidation.Run(_settings);
				GetTree().Quit(); return;
			}
			BuildLighting();
			_camera = new MapCamera { Name = "MapCamera", MapSize = _settings.SizeMeters }; AddChild(_camera);
			BuildInterface(); Generate();
            var verifyExport=args.FirstOrDefault(a=>a.StartsWith("--validate-town-export="));if(verifyExport!=null){await VerifyExportedTown(verifyExport.Split('=',2)[1]);GetTree().Quit();return;}
            if(args.Contains("--export-town")){await ExportTown();GetTree().Quit();return;}
            if (args.Contains("--validate-city-visual") || args.Contains("--ui-city-visual")) { await VerifyCityVisuals(args.Contains("--ui-city-visual")); GetTree().Quit(); return; }
            if (args.Contains("--preview-city"))
            {
                await RunSamplingAsync(); await RunSettlementAsync(); await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync(); await RunBuildingsAsync(); await RunPlacementAsync();
                if (CurrentPlacement == null) throw new InvalidOperationException("City preview requires a valid generation chain");
                _citySelector.Select(PlacementStage); _citySelector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)PlacementStage); _placementLayer.Select(1); ShowPlacement(); FocusPlacement("town"); UpdateCityPresentation(true);
                GD.Print("CITY_DEMO_READY"); return;
            }
            string? cityCapture = args.FirstOrDefault(a => a.StartsWith("--capture-city="));
            if (cityCapture != null) { await CaptureCityViews(cityCapture["--capture-city=".Length..], args.Contains("--benchmark-city")); GetTree().Quit(); return; }
            if (args.Contains("--ui-terrain-visual")) { await VerifyTerrainVisualInterface(); GetTree().Quit(); return; }
            string? terrainCapture = args.FirstOrDefault(a => a.StartsWith("--capture-terrain="));
            if (terrainCapture != null) { await CaptureTerrainVisuals(terrainCapture["--capture-terrain=".Length..], args.Contains("--benchmark-terrain")); GetTree().Quit(); return; }
            if(args.Contains("--ui-placement")){await VerifyPlacementInterface();GetTree().Quit();return;}
            if(args.Contains("--capture-placement")){await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();await RunPlacementAsync();if(CurrentPlacement==null)throw new InvalidOperationException("场景截图需要有效布置结果");await CapturePlacementViews();GetTree().Quit();return;}
            if(args.Contains("--ui-road-geometry")){await VerifyRoadGeometryInterface();GetTree().Quit();return;}
            if(args.Contains("--ui-road-grading")){await VerifyGradingInterface();GetTree().Quit();return;}
            if(args.Contains("--ui-buildings")){await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await VerifyBuildingInterface();GetTree().Quit();return;}
            if(args.Contains("--capture-buildings")){await CaptureBuildings();GetTree().Quit();return;}
            if(args.Contains("--capture-landscape")){await CaptureLandscape();GetTree().Quit();return;}
			string? capture = args.FirstOrDefault(a => a.StartsWith("--capture-dir="));
			if (args.Contains("--ui-smoke")) await VerifyInterface();
			if (capture != null) { await CaptureViews(capture.Substring("--capture-dir=".Length)); GetTree().Quit(); }
			else if (args.Contains("--ui-smoke")) GetTree().Quit();
		}
		catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
	}

	private void BuildLighting()
	{
		AddChild(new WorldEnvironment
		{
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("d6dfdc"),
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = new Color("d3dfe4"), AmbientLightEnergy = 0.38f,
				TonemapMode = Godot.Environment.ToneMapper.Linear
			}
		});
		AddChild(new DirectionalLight3D
		{
			Name = "AfternoonSun", RotationDegrees = new Vector3(-35, -38, 0),
			LightColor = new Color("fff7ed"), LightEnergy = 0.78f, ShadowEnabled = true,
			DirectionalShadowMaxDistance = 4500, ShadowBias = 0.035f,
			DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits
		});
	}

	private void Generate()
	{
		HidePlacement();
		var timer = System.Diagnostics.Stopwatch.StartNew();
		_settings.Seed = (int)_seed.Value;
		var data = new TemperateMapData(_settings);
		if (_map != null) { RemoveChild(_map); _map.QueueFree(); }
		_data = data;
		BindSamplingEnvironment();
		_map = new MapRenderer { Name = "TemperatePlain" }; AddChild(_map); _map.Build(data);
        if (_terrainQuality != null) _map.SetVisualQuality((TerrainVisualQuality)_terrainQuality.Selected);
		_map.SetView((TerrainView)_view.Selected);
		_map.ShowForest(_trees.ButtonPressed); _map.ShowWater(_river.ButtonPressed);
		_stats.Text = $"区域面积     {Mathf.Pow(_settings.SizeMeters / 1000, 2):0.0} km²\n" +
			$"地形采样     {_data.Spacing:0.00} m\n" +
			$"高程范围     {_data.MinHeight:0.0} — {_data.MaxHeight:0.0} m\n" +
			$"河道长度     {_data.RiverLength / 1000:0.00} km\n" +
			$"林地区域     {_data.ForestFraction:P0}\n" +
			$"简模树木     {_data.Trees.Count:N0} 株\n" +
			$"开阔缓坡     {_data.OpenGentleFraction:P0}";
		_status.Text = $"SEED  {_settings.Seed}     ·     生成 {timer.Elapsed.TotalSeconds:0.00} s";
		GD.Print($"MAP_READY seed={_settings.Seed} trees={_data.Trees.Count} vertices={_data.Heights.Length} height={_data.MinHeight:F2}..{_data.MaxHeight:F2} elapsed={timer.Elapsed.TotalSeconds:F2}s");
	}

	private void BuildInterface()
	{
		var layer = new CanvasLayer { Name = "Interface" }; AddChild(layer);
		var root = new Control { Name = "MapUI", MouseFilter = Control.MouseFilterEnum.Ignore };
		root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); layer.AddChild(root);
		var theme = new Theme { DefaultFont = new SystemFont { FontNames = new[] { "Microsoft YaHei", "Noto Sans CJK SC" } }, DefaultFontSize = 15 };
		theme.SetColor("font_color", "Label", Ink); theme.SetColor("font_color", "Button", Ink);
		theme.SetStylebox("normal", "Button", Box(new Color("35483f"), 7, 10));
		theme.SetStylebox("hover", "Button", Box(new Color("49604e"), 7, 10));
		theme.SetStylebox("pressed", "Button", Box(new Color("5c7155"), 7, 10));
		theme.SetStylebox("focus", "Button", new StyleBoxEmpty());
		root.Theme = theme;

		var panel = new PanelContainer { Name = "TerrainPanel", Position = new Vector2(24, 24), CustomMinimumSize = new Vector2(302, 0) };
		panel.AddThemeStyleboxOverride("panel", Box(new Color(0.075f, 0.12f, 0.10f, 0.94f), 12, 22)); root.AddChild(panel);
		var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 12); panel.AddChild(col);
		col.AddChild(Text("REGIONAL LANDSCAPE  /  01", 12, Accent));
		col.AddChild(Text("温带河谷平原", 27, Ink));
		col.AddChild(Text("长程缓坡 · 曲流 · 斑块森林", 14, Muted));
		col.AddChild(new HSeparator());
		_stats = Text("", 15, Ink); _stats.AddThemeConstantOverride("line_spacing", 10); col.AddChild(_stats);
		col.AddChild(new HSeparator());
		col.AddChild(Text("观察图层", 12, Muted));
		_view = new OptionButton { Name = "TerrainLayer", CustomMinimumSize = new Vector2(0, 38) };
		foreach (string label in new[] { "自然景观", "高程分布", "坡度分布", "建设适宜性" }) _view.AddItem(label);
		_view.ItemSelected += index => ApplyView((TerrainView)index); col.AddChild(_view);
        var visualRow = new HBoxContainer(); col.AddChild(visualRow); visualRow.AddChild(Text("地表表现", 13, Muted));
        _terrainQuality = new OptionButton { Name = "TerrainQuality", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (string label in new[] { "简洁", "标准", "细节" }) _terrainQuality.AddItem(label);
        _terrainQuality.Select((int)GD.Load<TerrainVisualSettings>("res://configs/terrain_visual.tres").Quality);
        _terrainQuality.ItemSelected += index => _map?.SetVisualQuality((TerrainVisualQuality)index); visualRow.AddChild(_terrainQuality);
		var toggles = new HBoxContainer(); col.AddChild(toggles);
		_trees = new CheckButton { Text = "森林", ButtonPressed = true, FocusMode = Control.FocusModeEnum.None };
		_river = new CheckButton { Text = "河面", ButtonPressed = true, FocusMode = Control.FocusModeEnum.None };
		_trees.Toggled += value => _map?.ShowForest(value); _river.Toggled += value => _map?.ShowWater(value);
		toggles.AddChild(_trees); toggles.AddChild(_river);
		col.AddChild(Text("地图种子", 12, Muted));
		var seedRow = new HBoxContainer(); col.AddChild(seedRow);
		_seed = new SpinBox { Name = "Seed", MinValue = 0, MaxValue = int.MaxValue, Step = 1, Value = _settings.Seed, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 36) };
		seedRow.AddChild(_seed);
		var rebuild = Button("重建地图"); rebuild.Name = "RebuildButton"; seedRow.AddChild(rebuild);
		rebuild.Pressed += async () =>
		{
			if (_busy) return;
			_busy = true; rebuild.Disabled = true; _status.Text = "正在生成地形与森林…";
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			try { Generate(); }
			catch (Exception ex) { _status.Text = "生成失败，请查看日志"; GD.PushError(ex.ToString()); }
			finally { _busy = false; rebuild.Disabled = false; _seed.GetLineEdit().ReleaseFocus(); }
		};
		var views = new HBoxContainer(); col.AddChild(views);
		var reset = Button("复位视角"); reset.Name = "ResetButton"; reset.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		var top = Button("正俯视"); top.Name = "TopButton"; top.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		views.AddChild(reset); views.AddChild(top); reset.Pressed += () => _camera.ResetView(); top.Pressed += () => _camera.TopView();
		_status = Text("", 11, Muted); col.AddChild(_status);

		var footer = new PanelContainer { Name = "Footer" }; root.AddChild(footer);
		footer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
		footer.OffsetLeft = 24; footer.OffsetRight = -24; footer.OffsetTop = -105; footer.OffsetBottom = -24;
		footer.AddThemeStyleboxOverride("panel", Box(new Color(0.075f, 0.12f, 0.10f, 0.92f), 10, 16));
		var footerCol = new VBoxContainer(); footer.AddChild(footerCol);
		_legend = Text("", 14, Accent); footerCol.AddChild(_legend);
		_probe = Text("将鼠标移到地形上，可读取位置与环境数据。", 12, Ink); footerCol.AddChild(_probe);
		footerCol.AddChild(Text("W A S D / 中键拖动  平移     ·     滚轮  缩放     ·     右键拖动 / Q E  旋转     ·     Home  复位", 12, Muted));
		UpdateLegend(TerrainView.Landscape);

		var compass = new MapCompass { Camera = _camera, MouseFilter = Control.MouseFilterEnum.Ignore };
		root.AddChild(compass); compass.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
		compass.OffsetLeft = -468; compass.OffsetRight = -358; compass.OffsetTop = 25; compass.OffsetBottom = 170;
		BuildSamplingInterface(root);
		BuildSettlementInterface(root);
        BuildRoadInterface(root);
        BuildGradingInterface(root);
        BuildParcelInterface(root);
        BuildBuildingInterface(root);
        BuildPlacementInterface(root);
        BuildCityInterface(root);
	}

	private void ApplyView(TerrainView view)
	{
		if (_sampleLayer != null) SetSampleLayer(EnvironmentPreviewLayer.Hidden);
		_view.Select((int)view); _map.SetView(view);
		bool visible = view == TerrainView.Landscape;
		_trees.SetPressedNoSignal(visible); _map.ShowForest(visible); UpdateLegend(view);
	}

	private void UpdateLegend(TerrainView view)
	{
		_legend.Text = view switch
		{
			TerrainView.Elevation => "高程  /  蓝绿：低处 → 浅黄：高处    ·    真实米制高程，无垂直夸张",
			TerrainView.Slope => "坡度  /  绿色：0° → 黄色：8° → 红色：20°及以上",
			TerrainView.Suitability => "建设适宜性  /  棕色：低 → 绿色：高    ·    综合坡度、河岸退让与林地清理成本",
			_ => $"环境底图  /  草甸平原 · 河岸滩地 · 混合林    |    地图 {_settings.SizeMeters / 1000:0.#} × {_settings.SizeMeters / 1000:0.#} km"
		};
	}

	private static Label Text(string text, int size, Color color)
	{
		var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
		label.AddThemeFontSizeOverride("font_size", size); label.AddThemeColorOverride("font_color", color); return label;
	}

	private static Button Button(string text) => new() { Text = text, CustomMinimumSize = new Vector2(0, 36), FocusMode = Control.FocusModeEnum.None };
	private static StyleBoxFlat Box(Color color, int radius, int padding) => new()
	{
		BgColor = color, CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
		CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
		ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding
	};

	public override void _UnhandledKeyInput(InputEvent e)
	{
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } && _cityPresentation?.Visible == true) FocusCitySelection();
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F9 }) { var ui = GetNode<CanvasLayer>("Interface"); ui.Visible = !ui.Visible; }
        if (e is InputEventKey { Pressed: true, Keycode: Key.Home }) _camera.ResetView();
    }

	public override void _Process(double delta)
	{
		if (_data == null || _camera == null || _probe == null) return;
        _cityTimer += delta; if (_cityTimer > .1) { _cityTimer = 0; UpdateCityPresentation(); }
		_probeTimer += delta; if (_probeTimer < 0.12) return; _probeTimer = 0;
		Vector2 mouse = GetViewport().GetMousePosition();
        if (UpdateCityProbe(mouse)) return;
		Vector3 origin = _camera.ProjectRayOrigin(mouse), ray = _camera.ProjectRayNormal(mouse);
		float previous = 0;
		for (float distance = 0; distance <= 6500; distance += 30)
		{
			Vector3 p = origin + ray * distance; Vector2 plane = new(p.X, p.Z);
			if (_data.Contains(plane) && p.Y <= DisplayTerrain.HeightAt(plane))
			{
				float lo = previous, hi = distance;
				for (int n = 0; n < 10; n++)
				{
					float mid = (lo + hi) * 0.5f; Vector3 q = origin + ray * mid;
					if (q.Y > DisplayTerrain.HeightAt(new Vector2(q.X, q.Z))) lo = mid; else hi = mid;
				}
				p = origin + ray * hi; plane = new Vector2(p.X, p.Z);
				if (!_data.Contains(plane)) break;
				if (UpdateGradingProbe(plane)) return;
				if (UpdatePlacementProbe(plane)) return;
                if (UpdateBuildingProbe(plane)) return;
                if (UpdateParcelProbe(plane)) return;
                if (UpdateRoadProbe(plane)) return;
                if (UpdateSettlementProbe(plane)) return;
				if (UpdateSamplingProbe(plane)) return;
				var s = _data.Sample(plane);
				_probe.Text = $"X {p.X,6:0} m   Z {p.Z,6:0} m     高程 {s.Height:0.0} m     坡度 {s.SlopeDegrees:0.0}°     " +
					$"{(s.IsWater ? "水域" : s.Forest > 0.24f ? "林地" : "草甸")}     适宜性 {s.Suitability:P0}";
				return;
			}
			previous = distance;
		}
		_probe.Text = "将鼠标移到地形上，可读取位置与环境数据。";
	}

	private async Task CaptureViews(string directory)
	{
		Directory.CreateDirectory(directory);
		async Task Save(string name)
		{
			for (int i = 0; i < 8; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			using var image = GetViewport().GetTexture().GetImage();
			string path = Path.Combine(directory, name + ".png");
			if (image.SavePng(path) != Error.Ok) throw new IOException($"Cannot save {path}");
			GD.Print($"CAPTURE {path}");
		}
		await Save("overview");
		_camera.Focus = new Vector3(-250, 15, -260); _camera.Size = 800; _camera.Pitch = 0.60f;
		await Save("river_forest_detail");
		_camera.TopView(); ApplyView(TerrainView.Slope); await Save("slope");
		ApplyView(TerrainView.Suitability); await Save("suitability");
		_river.SetPressedNoSignal(false); _map.ShowWater(false); ApplyView(TerrainView.Elevation); await Save("elevation_riverbed");
		await RunSamplingAsync();
		if (SampledEnvironment == null) throw new InvalidOperationException("Sampling capture requires a current result.");
		_camera.TopView(); _camera.Focus = new Vector3(0, 15, 0); _camera.Size = 2800;
		SetSampleLayer(EnvironmentPreviewLayer.Candidates); await Save("sampling_candidates");
		SetSampleLayer(EnvironmentPreviewLayer.Slope); await Save("sampling_slope");
		SetSampleLayer(EnvironmentPreviewLayer.Water); await Save("sampling_water");
		_camera.Focus = new Vector3(-160, 15, -200); _camera.Size = 650;
		_sampleGrid.SetPressedNoSignal(true); _samplePreview.SetGrid(true);
		SetSampleLayer(EnvironmentPreviewLayer.Restrictions); await Save("sampling_grid_detail");
		GD.Print("VISUAL_SMOKE_PASS views=9 forest_toggle=ok water_toggle=ok sampling=ok");
	}

	// Exercise Godot's input dispatch and actual GUI callbacks without an OS input-injection dependency.
	private async Task VerifyInterface()
	{
		void Check(bool condition, string message)
		{ if (!condition) throw new InvalidOperationException("UI_SMOKE_FAILED: " + message); }
		async Task Frame()
		{ await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		void Send(InputEvent e) => GetViewport().PushInput(e, true);
		void Mouse(Vector2 p, MouseButton button, bool pressed) => Send(new InputEventMouseButton
		{ Position = p, GlobalPosition = p, ButtonIndex = button, Pressed = pressed });
		async Task Click(Control c)
		{
			Vector2 p = c.GetGlobalRect().GetCenter();
			Send(new InputEventMouseMotion { Position = p, GlobalPosition = p });
			Mouse(p, MouseButton.Left, true); await Frame(); Mouse(p, MouseButton.Left, false); await Frame();
		}
		await Frame();
		await Click((Control)FindChild("TopButton", true, false)); Check(_camera.Pitch > 1.5f, "top view button");
		await Click((Control)FindChild("ResetButton", true, false)); Check(Mathf.IsEqualApprox(_camera.Pitch, 0.83f), "reset button");
		Vector2 mapPoint = new(700, 400);
		float size = _camera.Size;
		Mouse(mapPoint, MouseButton.WheelUp, true); Mouse(mapPoint, MouseButton.WheelUp, false); await Frame();
		Check(_camera.Size < size, "wheel zoom");
		float yaw = _camera.Yaw;
		Mouse(mapPoint, MouseButton.Right, true);
		Send(new InputEventMouseMotion { Position = mapPoint + new Vector2(80, 30), Relative = new Vector2(80, 30), ButtonMask = MouseButtonMask.Right });
		Mouse(mapPoint, MouseButton.Right, false); await Frame();
		Check(Mathf.Abs(_camera.Yaw - yaw) > 0.1f, "right drag orbit");
		Vector3 focus = _camera.Focus;
		Mouse(mapPoint, MouseButton.Middle, true);
		Send(new InputEventMouseMotion { Position = mapPoint + new Vector2(60, 20), Relative = new Vector2(60, 20), ButtonMask = MouseButtonMask.Middle });
		Mouse(mapPoint, MouseButton.Middle, false); await Frame();
		Check(_camera.Focus.DistanceTo(focus) > 10, "middle drag pan");
		await Click(_trees); Check(!_trees.ButtonPressed && !_map.GetNode<Node3D>("Forests").Visible, "forest visibility button");
		await Click(_river); Check(!_river.ButtonPressed && !_map.GetNode<Node3D>("River").Visible, "river visibility button");
		_view.Select(3); _view.EmitSignal(OptionButton.SignalName.ItemSelected, 3L); await Frame();
		Check(_legend.Text.StartsWith("建设适宜性"), "layer selection callback");
		int originalSeed = _settings.Seed; float originalHeight = _data.Heights[0];
		_seed.Value = 7; await Click((Control)FindChild("RebuildButton", true, false));
		Check(_data.Settings.Seed == 7 && _data.Heights[0] != originalHeight, "seed rebuild callback");
		_seed.Value = originalSeed; await Click((Control)FindChild("RebuildButton", true, false));
		Check(_data.Heights[0] == originalHeight, "restore original seed");
		ApplyView(TerrainView.Landscape); _river.SetPressedNoSignal(true); _map.ShowWater(true);
		_camera.TopView(); Send(new InputEventKey { Keycode = Key.Home, PhysicalKeycode = Key.Home, Pressed = true });
		Send(new InputEventKey { Keycode = Key.Home, PhysicalKeycode = Key.Home, Pressed = false }); await Frame();
		Check(Mathf.IsEqualApprox(_camera.Pitch, 0.83f), "Home shortcut");
		await VerifySamplingInterface();
        await VerifySettlementInterface();
        await VerifyRoadInterface();
        await RunGradingAsync();await VerifyParcelInterface();
        await VerifyBuildingInterface();
		GD.Print("UI_SMOKE_PASS top=ok reset=ok zoom=ok orbit=ok pan=ok toggles=ok layers=ok rebuild=ok home=ok");
	}
}
