using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class Main
{
    private readonly ScenePlacementNode _placementNode=new();private ScenePlacementPreview _placementPreview=null!;private PanelContainer _placementPanel=null!;
    private OptionButton _placementLayer=null!,_placementScheme=null!;private SpinBox _placementDensity=null!,_placementYards=null!,_placementBudget=null!,_placementSeed=null!;
    private CheckButton _placementFences=null!,_placementVegetation=null!;private Button _placementRun=null!,_placementCancel=null!;private Label _placementState=null!,_placementSummary=null!;
    private readonly List<SpinBox> _placementFields=new();private CancellationTokenSource? _placementCancellation;private string? _selectedPlacement;
    private ScenePlacementOptions PlacementSettings()=>new((SceneScheme)_placementScheme.Selected,(float)_placementDensity.Value,(float)_placementYards.Value,_placementFences.ButtonPressed,_placementVegetation.ButtonPressed,(int)_placementBudget.Value,(int)_placementSeed.Value);
    public ScenePlacementSet? CurrentPlacement=>_placementNode.IsCurrent(CurrentBuildings,_buildingNode.ResultRevision,PlacementSettings())?_placementNode.LastResult:null;
    private void BuildPlacementInterface(Control root){
        _placementPreview=new ScenePlacementPreview{Name="ScenePlacementPreview"};AddChild(_placementPreview);var selector=(OptionButton)root.GetNode("PcgNodeSelector");selector.AddItem("06 场景布置");
        _placementPanel=new PanelContainer{Name="PlacementPanel",Visible=false};root.AddChild(_placementPanel);_placementPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);_placementPanel.OffsetLeft=-344;_placementPanel.OffsetRight=-24;_placementPanel.OffsetTop=66;_placementPanel.OffsetBottom=-129;_placementPanel.AddThemeStyleboxOverride("panel",Box(new Color(.075f,.12f,.1f,.96f),12,18));selector.ItemSelected+=i=>_placementPanel.Visible=i==PlacementStage;
        var scroll=new ScrollContainer{HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};_placementPanel.AddChild(scroll);var col=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};scroll.AddChild(col);col.AddThemeConstantOverride("separation",6);col.AddChild(Text("场景布置",24,Ink));col.AddChild(Text("输入：有效建筑及同版道路、地块",12,Muted));
        var actions=new HBoxContainer();col.AddChild(actions);_placementRun=Button("独立生成场景");_placementCancel=Button("取消");_placementCancel.Disabled=true;actions.AddChild(_placementRun);actions.AddChild(_placementCancel);_placementRun.Pressed+=async()=>await RunPlacementAsync();_placementCancel.Pressed+=()=>_placementCancellation?.Cancel();
        _placementState=Text("未运行",12,Accent);_placementState.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_placementState);_placementSummary=Text("生成地面、建筑和有入口的附属物",12,Muted);_placementSummary.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_placementSummary);
        _placementLayer=new OptionButton();foreach(var title in new[]{"隐藏场景","场景总览","分类与来源","通行与占地","问题与落地"})_placementLayer.AddItem(title);col.AddChild(_placementLayer);_placementLayer.ItemSelected+=_=>ShowPlacement();
        _placementScheme=new OptionButton();_placementScheme.AddItem("内置土木方案");_placementScheme.AddItem("内置灰模方案");col.AddChild(_placementScheme);_placementScheme.ItemSelected+=_=>InvalidatePlacement();
        SpinBox Field(string title,double value,double min,double max,double step){var row=new HBoxContainer();col.AddChild(row);var label=Text(title,12,Muted);label.CustomMinimumSize=new(125,0);row.AddChild(label);var f=new SpinBox{MinValue=min,MaxValue=max,Step=step,Value=value,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};row.AddChild(f);_placementFields.Add(f);f.ValueChanged+=_=>InvalidatePlacement();return f;}
        _placementDensity=Field("公共与临街密度",.4,0,1,.05);_placementYards=Field("院落设施密度",.6,0,1,.05);_placementBudget=Field("附属物总预算",1200,0,4000,50);_placementSeed=Field("布置种子",73129,0,int.MaxValue,1);
        _placementFences=new CheckButton{Text="生成带开口的围栏",ButtonPressed=true};col.AddChild(_placementFences);_placementFences.Toggled+=_=>InvalidatePlacement();_placementVegetation=new CheckButton{Text="自然植被避让城镇",ButtonPressed=true};col.AddChild(_placementVegetation);_placementVegetation.Toggled+=_=>InvalidatePlacement();
        var views=new HBoxContainer();col.AddChild(views);foreach(var item in new[]{("全城","town"),("市场近景","market"),("院落近景","yard")}){var b=Button(item.Item1);views.AddChild(b);b.Pressed+=()=>FocusPlacement(item.Item2);}
        col.AddChild(Text("点击建筑或附属物查看来源\n城防以规划占地为准",12,Muted));BuildConstructionInterface(col);
    }
    private void InvalidatePlacement(){_placementNode.Invalidate();if(_placementState!=null)RefreshPlacement();}
    private void HidePlacement(){if(_placementLayer==null)return;bool shown=_placementPreview.Visible;_placementLayer.Select(0);_placementPreview.Display(_placementNode.LastResult,0);if(shown&&_map!=null)_map.SetVegetationMask(null);}
    private void SetPlacementBusy(bool busy){SetConstructionBusy(busy);if(_placementRun==null)return;foreach(var f in _placementFields)f.Editable=!busy;_placementScheme.Disabled=busy;_placementFences.Disabled=busy;_placementVegetation.Disabled=busy;_placementRun.Disabled=busy;_placementCancel.Disabled=true;}
    private void RefreshPlacement(){if(_placementState==null)return;_placementState.Text=_placementNode.State switch{SampleNodeState.Valid=>CurrentPlacement!=null?"有效":"已过期",SampleNodeState.Stale=>"已过期",SampleNodeState.Running=>"生成中…",SampleNodeState.Cancelled=>"已取消",SampleNodeState.Failed=>"失败："+_placementNode.Error,_=>"未运行"};if(CurrentBuildings==null)_placementState.Text+=" · 请先生成有效建筑";if(CurrentPlacement==null&&_placementNode.LastResult!=null)_placementState.Text+="\n保留旧场景（不可作为当前结果）";
        if(_placementNode.LastResult is {} d)_placementSummary.Text=$"建筑 {d.BuildingPlacements.Count} · 附属物 {d.Props.Count}\n地面区域 {d.GroundPatches.Count} · 围栏 {d.Props.Count(p=>p.Kind==ScenePropKind.Fence)}\n跳过/问题 {d.Issues.Count} · 尝试 {d.CandidateAttempts}\n"+(_placementPreview.Visible?$"自然树木避让 {_map.FilteredTreeCount} 棵\n":"")+string.Join(" · ",d.Props.GroupBy(p=>p.Kind).Select(g=>$"{PlacementName(g.Key)} {g.Count()}"));
    }
    private async Task RunPlacementAsync(){if(_busy)return;var buildings=CurrentBuildings;var env=SampledEnvironment;var roads=CurrentRoads;var parcels=CurrentParcels;if(buildings==null||env==null||roads==null||parcels==null){_placementState.Text="请先生成有效的道路、地块与建筑";return;}
        _busy=true;_placementCancellation=new();SetSamplingBusy(true);SetLayoutBusy(true);SetRoadBusy(true);SetParcelBusy(true);SetBuildingBusy(true);SetPlacementBusy(true);_sampleCancel.Disabled=true;_layoutCancel.Disabled=true;_roadCancel.Disabled=true;_parcelCancel.Disabled=true;_buildingCancel.Disabled=true;_placementCancel.Disabled=false;_selectedPlacement=null;
        try{var result=await _placementNode.RunAsync(env,roads,parcels,buildings,_sampleNode.ResultRevision,_roadNode.ResultRevision,_parcelNode.ResultRevision,_buildingNode.ResultRevision,PlacementSettings(),new(_placementCancellation.Token,async p=>{_placementState.Text=$"{p.Message} {p.Fraction:P0}";await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}));if(_placementLayer.Selected==0)_placementLayer.Select(1);ShowPlacement();GD.Print($"PLACEMENT_READY buildings={result.BuildingPlacements.Count} props={result.Props.Count} ground={result.GroundPatches.Count} revision={_placementNode.ResultRevision}");}
        catch(OperationCanceledException){GD.Print("PLACEMENT_CANCELLED");}catch(Exception ex){GD.PrintErr("PLACEMENT_FAILED: "+ex);_placementState.Text=ex.Message;}
        finally{_placementCancellation.Dispose();_placementCancellation=null;_busy=false;SetSamplingBusy(false);SetLayoutBusy(false);SetRoadBusy(false);SetParcelBusy(false);SetBuildingBusy(false);SetPlacementBusy(false);RefreshPlacement();}
    }
    private void ShowPlacement(){var d=_placementNode.LastResult;if(_placementLayer.Selected==0){HidePlacement();return;}if(d==null)return;
        HideBuildings();HideParcels();HideRoads();SetSampleLayer(EnvironmentPreviewLayer.Hidden);_layoutLayer.Select(0);_layoutPreview.Display(d.Roads.Settlement,0,false);_townPreview.Visible=false;
        _placementPreview.Display(d,_placementLayer.Selected,_selectedPlacement);_map.SetVegetationMask(d.Options.AvoidVegetation?d.VegetationMask:null);_trees.SetPressedNoSignal(_placementLayer.Selected<=2);_map.ShowForest(_trees.ButtonPressed);_view.Select(0);_map.SetView(TerrainView.Landscape);
        _legend.Text=_placementLayer.Selected switch{2=>"分类与来源 / 建筑、棚屋、摊棚、储物、井、长凳、围栏 · 点击追溯来源",3=>"通行与占地 / 青：保护通道 · 浅蓝：附属物入口 · 橙：附属物占地",4=>"问题与落地 / 橙叉：跳过位置 · 绿线：植被避让 · 附属物轮廓按高差分色",_=>"城镇场景 / 定型道路、建筑、院落设施与自然环境 · 城墙为平面占位"};RefreshPlacement();
    }
    private void FocusPlacement(string kind){var d=_placementNode.LastResult;if(d==null)return;if(kind=="town"){FocusSettlement();_camera.Pitch=1.05f;_camera.Yaw=-.2f;_camera.UpdateTransform();return;}
        var prop=kind=="market"?d.Props.FirstOrDefault(p=>p.ZoneId=="market"):d.Props.Where(p=>p.Kind==ScenePropKind.Shed).OrderBy(p=>p.Footprint.Center.DistanceSquaredTo(d.Roads.Settlement.Core)).FirstOrDefault()??d.Props.FirstOrDefault(p=>p.ParcelId.HasValue);
        var point=prop?.Footprint.Center??d.Roads.Settlement.Core;d.Terrain.TryHeightAt(point,out float h);_camera.Focus=new(point.X,h+1,point.Y);_camera.Size=kind=="market"?85:65;_camera.Pitch=.95f;_camera.Yaw=-.35f;_camera.UpdateTransform();
    }
    private static string PlacementName(ScenePropKind kind)=>kind switch{ScenePropKind.Storage=>"储物",ScenePropKind.Shed=>"棚屋",ScenePropKind.Stall=>"摊棚",ScenePropKind.Bench=>"长凳",ScenePropKind.Well=>"井",_=>"围栏"};
    private string PlacementInfo(string? id){var d=_placementNode.LastResult;if(d==null)return "";if(d.Props.FirstOrDefault(p=>p.Id==id) is {} p)return $"{PlacementName(p.Kind)} · {p.Id}\n来源 {p.ZoneId} · {p.Recipe}\n占地 {p.Footprint.Area:0.0} m² · 高 {p.Height:0.0} m · 地面差 {p.MaxGround-p.MinGround:0.00} m";
        if(d.BuildingPlacements.FirstOrDefault(b=>b.Id==id) is {} b)return $"{b.Id} {b.Mass.Program}\n来源 {(b.Mass.ParcelId.HasValue?$"P{b.Mass.ParcelId}":b.Mass.PrecinctId)} · {b.Recipe}";return "场景 / 将光标移到建筑或附属物上";}
    private string? PickPlacement(Vector2 screen)=>_placementPreview.Pick(_camera.ProjectRayOrigin(screen),_camera.ProjectRayNormal(screen),PickSettlementGround(screen));
    private bool UpdatePlacementProbe(Vector2 p){if(_placementLayer==null||_placementLayer.Selected==0)return false;var id=PickPlacement(GetViewport().GetMousePosition());var issue=_placementLayer.Selected==4?_placementNode.LastResult?.Issues.OrderBy(i=>i.Position.DistanceSquaredTo(p)).FirstOrDefault():null;_probe.Text=issue!=null&&issue.Position.DistanceTo(p)<5?$"{issue.Source} · {issue.Reason}":(CurrentPlacement==null?"旧场景 · ":"")+PlacementInfo(id).Replace('\n',' ');return true;}
    private bool HandlePlacementClick(InputEvent ev){if(_busy||_placementPanel==null||!_placementPanel.Visible||_placementLayer.Selected==0||ev is not InputEventMouseButton{Pressed:true,ButtonIndex:MouseButton.Left} mouse)return false;_selectedPlacement=PickPlacement(mouse.Position);ShowPlacement();_placementState.Text=(CurrentPlacement==null?"旧结果 · ":"")+PlacementInfo(_selectedPlacement);GetViewport().SetInputAsHandled();return true;}
}
