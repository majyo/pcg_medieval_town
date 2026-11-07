using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class Main
{
    private const int GradingStage=3, ParcelStage=4, BuildingStage=5, PlacementStage=6;
    private readonly RoadGradingNode _gradingNode=new();
    private RoadGradingPreview _gradingPreview=null!;
    private PanelContainer _gradingPanel=null!;
    private readonly List<SpinBox> _gradingFields=new();
    private SpinBox _gradingSmoothing=null!,_gradingChange=null!,_gradingCrossfall=null!,_gradingJunction=null!,_gradingPlaza=null!,_gradingCut=null!,_gradingFill=null!;
    private SpinBox _gradingCutRatio=null!,_gradingFillRatio=null!,_gradingResolution=null!;
    private CheckButton _gradingApply=null!;
    private OptionButton _gradingLayer=null!;
    private Button _gradingRun=null!,_gradingCancel=null!;
    private Label _gradingState=null!,_gradingSummary=null!;
    private ProfileChart _gradingChart=null!;
    private CancellationTokenSource? _gradingCancellation;
    private int _gradingSelectedRoad=-1;
    private NativeTerrainSurface? _terrainSurface;
    private ITerrainSurface TerrainSurface=>_terrainSurface!=null&&ReferenceEquals(_terrainSurface.Map,_data)?_terrainSurface:_terrainSurface=new NativeTerrainSurface(_data);
    private ITerrainSurface DisplayTerrain=>_map?.Graded??TerrainSurface;
    private RoadGradingOptions GradingSettings()=>new((float)_gradingSmoothing.Value,(float)_gradingChange.Value,(float)_gradingCrossfall.Value,(float)_gradingJunction.Value,(float)_gradingPlaza.Value,(float)_gradingCut.Value,(float)_gradingFill.Value,CutRatio:(float)_gradingCutRatio.Value,FillRatio:(float)_gradingFillRatio.Value,Resolution:(float)_gradingResolution.Value);
    public RoadGrading? CurrentGrading=>_data!=null&&_gradingNode.IsCurrent(CurrentRoads,_roadNode.ResultRevision,TerrainSurface,GradingSettings())?_gradingNode.LastResult:null;
    private void BuildGradingInterface(Control root)
    {
        _gradingPreview=new RoadGradingPreview{Name="RoadGradingPreview"};AddChild(_gradingPreview);
        var selector=(OptionButton)root.GetNode("PcgNodeSelector");selector.AddItem("03B 道路竖向");
        _gradingPanel=new PanelContainer{Name="GradingPanel",Visible=false};root.AddChild(_gradingPanel);_gradingPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);_gradingPanel.OffsetLeft=-344;_gradingPanel.OffsetRight=-24;_gradingPanel.OffsetTop=66;_gradingPanel.OffsetBottom=-129;
        _gradingPanel.AddThemeStyleboxOverride("panel",Box(new Color(.075f,.12f,.10f,.96f),12,18));
        selector.ItemSelected+=i=>{_gradingPanel.Visible=i==GradingStage;if(i==GradingStage)ShowGrading();else{_gradingPreview.Display(null,0);}};
        var scroll=new ScrollContainer{HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};_gradingPanel.AddChild(scroll);var col=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};col.AddThemeConstantOverride("separation",6);scroll.AddChild(col);
        col.AddChild(Text("道路竖向",24,Ink));col.AddChild(Text("输入：有效道路 + 原生地形\n输出：修整后地形 → 04 地块 → 05 建筑 → 06 布置",12,Muted));
        SpinBox Field(string title,double value,double min,double max,double step){var row=new HBoxContainer();col.AddChild(row);var caption=Text(title,12,Muted);caption.CustomMinimumSize=new(125,0);row.AddChild(caption);var input=new SpinBox{MinValue=min,MaxValue=max,Step=step,Value=value,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};row.AddChild(input);_gradingFields.Add(input);input.ValueChanged+=_=>InvalidateGrading();return input;}
        var d=new RoadGradingOptions();
        _gradingSmoothing=Field("长程平滑 / m",d.SmoothingScale,0,200,1);_gradingChange=Field("坡度变化 / %·10m",d.GradeChange,.1,50,.1);_gradingCrossfall=Field("横坡 / %",d.Crossfall,0,8,.1);
        _gradingJunction=Field("路口平台坡度 / %",d.JunctionSlope,0,20,.1);_gradingPlaza=Field("广场坡度 / %",d.PlazaSlope,0,20,.1);_gradingCut=Field("最大挖深 / m",d.MaxCut,0,10,.05);_gradingFill=Field("最大填高 / m",d.MaxFill,0,10,.05);
        _gradingCutRatio=Field("挖方坡率 1:",d.CutRatio,.25,10,.05);_gradingFillRatio=Field("填方坡率 1:",d.FillRatio,.25,10,.05);_gradingResolution=Field("修整精度 / m",d.Resolution,.25,2,.05);
        var buttons=new HBoxContainer();col.AddChild(buttons);_gradingRun=Button("独立生成竖向");_gradingCancel=Button("取消");_gradingCancel.Disabled=true;buttons.AddChild(_gradingRun);buttons.AddChild(_gradingCancel);
        _gradingRun.Pressed+=async()=>await RunGradingAsync();_gradingCancel.Pressed+=()=>_gradingCancellation?.Cancel();
        _gradingState=Text("未运行",12,Accent);_gradingState.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_gradingState);
        _gradingLayer=new OptionButton{Name="GradingLayer"};foreach(string name in new[]{"隐藏竖向","设计路面","挖填分布","纵断面","问题","修整后地形"})_gradingLayer.AddItem(name);col.AddChild(_gradingLayer);_gradingLayer.ItemSelected+=_=>ShowGrading();
        _gradingApply=new CheckButton{Name="GradingApply",Text="显示修整后地形（关闭为原地形）",ButtonPressed=true};col.AddChild(_gradingApply);_gradingApply.Toggled+=_=>ShowGrading();
        var views=new HBoxContainer();col.AddChild(views);var cut=Button("最大挖方");views.AddChild(cut);cut.Pressed+=()=>FocusGrading(true);var fill=Button("最大填方");views.AddChild(fill);fill.Pressed+=()=>FocusGrading(false);
        var whole=Button("路网俯视");col.AddChild(whole);whole.Pressed+=FocusSettlement;
        _gradingChart=new ProfileChart{Name="GradingProfileChart",CustomMinimumSize=new(296,200)};col.AddChild(_gradingChart);
        _gradingSummary=Text("生成后显示挖填量、坡度与回退",12,Muted);_gradingSummary.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_gradingSummary);
        col.AddChild(Text("点击道路查看纵断面\n设计路面层：地形遮住处为将来的挖方",12,Muted));
    }
    private void InvalidateGrading(){_gradingNode.Invalidate();InvalidateParcels();if(_gradingState!=null)RefreshGrading();}
    private void RefreshGrading()
    {
        if(_gradingState==null)return;bool current=CurrentGrading!=null;
        _gradingState.Text=_gradingNode.State switch{SampleNodeState.Valid=>current?"有效":"已过期",SampleNodeState.Stale=>"已过期",SampleNodeState.Running=>"生成中…",SampleNodeState.Cancelled=>"已取消",SampleNodeState.Failed=>"失败："+_gradingNode.Error,_=>"未运行"};
        if(CurrentRoads==null)_gradingState.Text+="\n请先生成有效的道路";
        if(!current&&_gradingNode.LastResult!=null)_gradingState.Text+="\n保留旧结果（仅供查看）";
        if(_gradingNode.LastResult is {} g){var s=g.Summary;
            _gradingSummary.Text=$"挖方 {s.CutVolume:0} m³ · 填方 {s.FillVolume:0} m³\n最大挖深 {s.MaxCut:0.00} m · 最大填高 {s.MaxFill:0.00} m\n设计最大纵坡 {s.MaxGrade*100:0.0}% · 坡度变化 {s.MaxGradeChange*100:0.0}%/10m\n"+
                $"短程起伏 {s.GroundRoughness*1000:0.0} → {s.DesignRoughness*1000:0.0} mm（±10 m）\n路口平台 {g.Planes.Count} · 广场坡度 {g.Plaza.Slope*100:0.0}%\n局部放宽 {s.RelaxedProfiles} 条 · 平台受限 {s.ClampedPlanes} 处\n"+
                (g.Graded is {} t?$"边坡挖方 {t.Earthwork.SlopeCut:0} m³ · 填方 {t.Earthwork.SlopeFill:0} m³\n修整面积 {t.Earthwork.ModifiedArea:0} m² · 边坡外延 ≤ {t.Earthwork.BandReach:0.0} m\n挡土墙 {t.RetainingWalls.Count} 处"+(ReferenceEquals(_map.Graded,t)?$" · 精细网格 {_map.GradedTriangleCount/1000.0:0}k 三角形 · 移除树木 {_map.GradedTreeCount}":"")+"\n":"")+
                string.Join("\n",g.Notices.GroupBy(n=>n.Kind).Select(k=>$"{NoticeName(k.Key)} × {k.Count()}"));}
        UpdateGradingChart();
    }
    private static string KindName(GradingCell kind)=>kind switch{GradingCell.Road=>"路基",GradingCell.Shoulder=>"路肩",GradingCell.Band=>"边坡",GradingCell.Lock=>"锁定区",GradingCell.Conflict=>"需挡土墙",_=>"未修整"};
    private static string NoticeName(string kind)=>kind switch{"grade_change"=>"坡度变化放宽","junction"=>"路口平台受限","plaza"=>"广场坡度受限","cut"=>"截面挖深超限","fill"=>"截面填高超限","retaining"=>"需挡土墙","terrain_retry"=>"边坡约束重试",_=>kind};
    private void UpdateGradingChart()
    {
        var g=_gradingNode.LastResult;var p=g?.Profile(_gradingSelectedRoad);
        if(g!=null&&p==null){p=g.Profiles.OrderByDescending(x=>x.Length).FirstOrDefault();_gradingSelectedRoad=p?.RoadId??-1;}
        _gradingChart.Show(p,p==null?"":$"R{p.RoadId} · 纵坡 ≤ {p.MaxGrade*100:0.0}% · 变化 {p.MaxGradeChange*100:0.0}%/10m{(p.GradeChangeRelaxed?" · 局部放宽":"")}");
    }
    private void SetGradingBusy(bool busy){if(_gradingRun==null)return;foreach(var input in _gradingFields)input.Editable=!busy;_gradingRun.Disabled=busy;_gradingCancel.Disabled=!busy;}
    private async Task RunGradingAsync()
    {
        if(_busy)return;var roads=CurrentRoads;if(roads==null){_gradingState.Text="请先生成有效的道路";return;}
        InvalidateParcels();_busy=true;_gradingCancellation=new();SetParcelBusy(true);SetBuildingBusy(true);SetPlacementBusy(true);_parcelCancel.Disabled=true;_buildingCancel.Disabled=true;_placementCancel.Disabled=true;SetSamplingBusy(true);SetLayoutBusy(true);SetRoadBusy(true);SetGradingBusy(true);_sampleCancel.Disabled=true;_layoutCancel.Disabled=true;if(_roadCancel!=null)_roadCancel.Disabled=true;
        try{var result=await _gradingNode.RunAsync(roads,_roadNode.ResultRevision,TerrainSurface,GradingSettings(),new(_gradingCancellation.Token,async p=>{_gradingState.Text=$"{p.Message} {p.Fraction:P0}";await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}));
            if(_gradingLayer.Selected==0)_gradingLayer.Select(2);GD.Print($"GRADING_READY profiles={result.Profiles.Count} cut={result.Summary.CutVolume:0} fill={result.Summary.FillVolume:0} revision={_gradingNode.ResultRevision}");}
        catch(OperationCanceledException){GD.Print("GRADING_CANCELLED");}
        catch(Exception ex){GD.PrintErr("GRADING_FAILED: "+ex.Message);_gradingLayer.Select(4);}
        finally{_gradingCancellation.Dispose();_gradingCancellation=null;_busy=false;SetSamplingBusy(false);SetLayoutBusy(false);SetRoadBusy(false);SetGradingBusy(false);SetParcelBusy(false);SetBuildingBusy(false);SetPlacementBusy(false);RefreshParcels();RefreshRoads();RefreshGrading();if(_gradingPanel.Visible)ShowGrading();}
    }
    private void ShowGrading()
    {
        int layer=_gradingLayer.Selected;var graded=_gradingApply.ButtonPressed?_gradingNode.LastResult?.Graded:null;
        _map.SetGradedTerrain(layer>0?graded:null,heat:layer==2);
        if(layer>0){HidePlacement();HideBuildings();HideParcels();HideRoads();SetSampleLayer(EnvironmentPreviewLayer.Hidden);_layoutLayer.Select(0);_layoutPreview.Display(_layoutNode.LastResult,0,false);_townPreview.Visible=false;_trees.SetPressedNoSignal(layer==5);_map.ShowForest(layer==5);
            _legend.Text=_gradingLayer.Selected switch{2=>$"挖填分布 / 红：挖方 · 蓝：填方 · ±{RoadGradingPreview.Saturation:0.0} m 饱和",3=>"纵断面 / 金：设计 · 白：原地形 · 竖线按挖填着色",4=>"问题 / 红：坡度变化放宽 · 紫：路口平台受限 · 蓝：广场 · 橙：截面超限 · 粗红线：需挡土墙",5=>graded==null?"修整后地形 / 已关闭：显示原地形":"修整后地形 / 路基压实土 · 挖方露土 · 填方压实 · 已移除路上与边坡树木",_=>"设计路面 / 横向平整、纵向长程变化 · 地形遮住处为将来的挖方"};}
        _gradingPreview.Display(_gradingNode.LastResult,_gradingLayer.Selected,_gradingSelectedRoad,_gradingNode.Failure,terrainApplied:graded!=null);RefreshGrading();
    }
    private void FocusGrading(bool cut)
    {
        var g=_gradingNode.LastResult;if(g==null)return;FocusGradingAt(g,cut?g.Summary.MaxCutAt:g.Summary.MaxFillAt,60);
        var surface=g.SurfaceAt(cut?g.Summary.MaxCutAt:g.Summary.MaxFillAt);if(surface!=null){_gradingSelectedRoad=surface.RoadId;}if(_gradingLayer.Selected==0)_gradingLayer.Select(1);ShowGrading();
    }
    // Oblique view across the road at p, so cut and fill read as height differences.
    private void FocusGradingAt(RoadGrading g,Vector2 p,float size)
    {
        var road=g.Roads.Edges.OrderBy(e=>RoadDistance(p,e)).First();var line=new Centerline(road);var t=Vector2.Right;float best=float.PositiveInfinity;
        for(float s=0;s<=line.Length;s+=1){float d=line.PointAt(s).DistanceSquaredTo(p);if(d<best){best=d;t=line.TangentAt(s);}}
        var across=new Vector2(-t.Y,t.X);_camera.Yaw=Mathf.Atan2(across.X,across.Y);_camera.Pitch=.42f;_camera.Focus=new(p.X,g.GroundHeight(p),p.Y);_camera.Size=size;_camera.UpdateTransform();
    }
    private bool UpdateGradingProbe(Vector2 p)
    {
        var g=_gradingNode.LastResult;if(g==null||_gradingPanel==null||!_gradingPanel.Visible||_gradingLayer.Selected==0)return false;
        float ground=g.GroundHeight(p);if(!g.TryDesignHeight(p,out float design)){var t=g.Graded;float delta0=t?.DeltaAt(p)??0;
            _probe.Text=$"道路外 · 原地形 {ground:0.00} m"+(t==null?"":$" · 修整后 {ground+delta0:0.00} m · {(delta0<0?"挖":"填")} {Math.Abs(delta0):0.00} m · {KindName(t.KindAt(p))}");return true;}
        var surface=g.SurfaceAt(p);string owner=g.InPlaza(p)?"广场平面":surface==null?"":surface.NodeId.HasValue&&g.Planes.Any(x=>x.NodeId==surface.NodeId)?$"路口 {surface.NodeId}":$"R{surface.RoadId}";
        float delta=design-ground;_probe.Text=$"{(CurrentGrading==null?"旧竖向 · ":"")}{owner} · 设计 {design:0.00} m · 地形 {ground:0.00} m · {(delta<0?"挖":"填")} {Math.Abs(delta):0.00} m{(g.IsPureSweep(p)?" · 平整截面":"")}";return true;
    }
    private bool HandleGradingClick(InputEvent ev)
    {
        if(_busy||_gradingPanel==null||!_gradingPanel.Visible||_gradingLayer.Selected==0||_gradingNode.LastResult is not {} g||ev is not InputEventMouseButton{Pressed:true,ButtonIndex:MouseButton.Left} b)return false;
        var hit=PickSettlementGround(b.Position);if(hit==null)return false;var surface=g.SurfaceAt(hit.Value);
        int road=surface?.RoadId??g.Roads.Edges.OrderBy(e=>RoadDistance(hit.Value,e)).First().Id;
        _gradingSelectedRoad=road;ShowGrading();GetViewport().SetInputAsHandled();return true;
    }
}
