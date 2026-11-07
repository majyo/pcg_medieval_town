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
    private readonly RoadNode _roadNode=new();
    private RoadPreview _roadPreview=null!;
    private PanelContainer _roadPanel=null!;
    private readonly List<SpinBox> _roadFields=new();
    private SpinBox _roadMainWidth=null!,_roadStreetWidth=null!,_roadSpacing=null!,_roadAngle=null!,_roadJitter=null!,_roadGrade=null!,_roadSlope=null!,_roadSeed=null!,_roadCorner=null!,_roadFlare=null!;
    private CheckButton _roadAutoAngle=null!;
    private OptionButton _roadLayer=null!,_roadPattern=null!;
    private Button _roadRun=null!,_roadCancel=null!;
    private Label _roadState=null!,_roadSummary=null!;
    private CancellationTokenSource? _roadCancellation;
    private string? _roadSelectedAnchor;
    private int _roadSelectedEdge=-1;
    private RoadOptions RoadSettings()=>new((float)_roadMainWidth.Value,(float)_roadStreetWidth.Value,(float)_roadSpacing.Value,_roadAutoAngle.ButtonPressed?null:(float)_roadAngle.Value,(float)_roadJitter.Value,(float)_roadGrade.Value,(float)_roadSlope.Value,(int)_roadSeed.Value,PlannedTown:_roadPattern.Selected==1,CornerRadius:(float)_roadCorner.Value,JunctionFlare:(float)_roadFlare.Value);
    public RoadNetwork? CurrentRoads=>_roadNode.IsCurrent(SampledEnvironment,CurrentSettlement,_sampleNode.ResultRevision,_layoutNode.ResultRevision,RoadSettings())?_roadNode.LastResult:null;
    private void BuildRoadInterface(Control root){
        _roadPreview=new RoadPreview{Name="RoadPreview"};AddChild(_roadPreview);
        var selector=(OptionButton)root.GetNode("PcgNodeSelector");selector.AddItem("03 道路生成");
        _roadPanel=new PanelContainer{Name="RoadPanel",Visible=false};root.AddChild(_roadPanel);_roadPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);_roadPanel.OffsetLeft=-344;_roadPanel.OffsetRight=-24;_roadPanel.OffsetTop=66;_roadPanel.OffsetBottom=-129;
        _roadPanel.AddThemeStyleboxOverride("panel",Box(new Color(.075f,.12f,.10f,.96f),12,18));selector.ItemSelected+=i=>_roadPanel.Visible=i==2;
        var scroll=new ScrollContainer{HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};_roadPanel.AddChild(scroll);var col=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};col.AddThemeConstantOverride("separation",6);scroll.AddChild(col);
        col.AddChild(Text("道路生成",24,Ink));col.AddChild(Text("输入：有效环境采样 + 聚落布局",12,Muted));
        SpinBox Field(string title,double value,double min,double max,double step=1){var row=new HBoxContainer();col.AddChild(row);var caption=Text(title,12,Muted);caption.CustomMinimumSize=new(125,0);row.AddChild(caption);var input=new SpinBox{MinValue=min,MaxValue=max,Step=step,Value=value,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};row.AddChild(input);_roadFields.Add(input);input.ValueChanged+=_=>InvalidateRoads();return input;}
        _roadPattern=new OptionButton();_roadPattern.AddItem("不规则商贸城镇");_roadPattern.AddItem("规则规划新城");col.AddChild(_roadPattern);_roadPattern.ItemSelected+=i=>{_roadAutoAngle.Visible=i==1;_roadAngle.GetParent<Control>().Visible=i==1;_roadJitter.GetParent<Control>().Visible=i==1;InvalidateRoads();};
        _roadMainWidth=Field("主路宽度 / m",6,1,100,.5);_roadStreetWidth=Field("次路宽度 / m",3,1,100,.5);_roadSpacing=Field("街巷疏密 / m",80,10,500,5);
        _roadAutoAngle=new CheckButton{Text="自动主方向",ButtonPressed=true};col.AddChild(_roadAutoAngle);_roadAutoAngle.Toggled+=_=>InvalidateRoads();_roadAngle=Field("手动方向 / 度",0,-180,180);
        _roadJitter=Field("不规则程度",.15,0,.25,.01);_roadGrade=Field("最大纵坡 / 度",10,1,45);_roadSlope=Field("地表坡度 / 度",12,1,45);_roadSeed=Field("道路种子",73129,0,int.MaxValue);
        _roadCorner=Field("转角平顺 / m",3,0,12,.5);_roadFlare=Field("路口展开 / m",2,0,8,.5);
        _roadAutoAngle.Visible=false;_roadAngle.GetParent<Control>().Visible=false;_roadJitter.GetParent<Control>().Visible=false;
        var buttons=new HBoxContainer();col.AddChild(buttons);_roadRun=Button("独立生成道路");_roadCancel=Button("取消");_roadCancel.Disabled=true;buttons.AddChild(_roadRun);buttons.AddChild(_roadCancel);_roadRun.Pressed+=async()=>await RunRoadsAsync();_roadCancel.Pressed+=()=>_roadCancellation?.Cancel();
        _roadState=Text("未运行",12,Accent);_roadState.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_roadState);
        _roadLayer=new OptionButton();foreach(string name in new[]{"隐藏道路","路网总览","连接关系","道路占地","坡度与问题"})_roadLayer.AddItem(name);col.AddChild(_roadLayer);_roadLayer.ItemSelected+=_=>ShowRoads();
        var focus=Button("定位路网 / 正俯视");col.AddChild(focus);focus.Pressed+=FocusSettlement;
        var junction=Button("定位路口 / 近景");col.AddChild(junction);junction.Pressed+=FocusRoadJunction;
        _roadSummary=Text("生成后显示主次路长度、连接与诊断",12,Muted);_roadSummary.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_roadSummary);
        col.AddChild(Text("点击入口高亮实际通路\n点击道路查看宽度、长度、坡度",12,Muted));
    }
    private void InvalidateRoads(){_roadNode.Invalidate();InvalidateGrading();if(_roadState!=null)RefreshRoads();}
    private void HideRoads(){if(_roadLayer==null)return;_roadLayer.Select(0);_roadPreview.Display(_roadNode.LastResult,0);}
    private void RefreshRoads(){if(_roadState==null)return;bool current=CurrentRoads!=null;
        _roadState.Text=_roadNode.State switch{SampleNodeState.Valid=>current?"有效":"已过期",SampleNodeState.Stale=>"已过期",SampleNodeState.Running=>"生成中…",SampleNodeState.Cancelled=>"已取消",SampleNodeState.Failed=>"失败："+_roadNode.Error,_=>"未运行"};
        if(CurrentSettlement==null)_roadState.Text+="\n请先生成有效的环境与聚落";
        if(!current&&_roadNode.LastResult!=null)_roadState.Text+="\n保留旧结果（不可用于下游）";
        if(_roadNode.LastResult is {} r)_roadSummary.Text=$"主路 {r.MainLength:0} m · 次路 {r.StreetLength:0} m\n{r.Nodes.Count} 节点 · {r.Edges.Count} 路段\n闭合回路 {r.CycleCount} · 入口 {r.Settlement.Entries.Count} 全部连通\n占地 {r.Geometry.Area.Area:0} m² · 平顺 {r.Geometry.SmoothedCorners} 处\n路口 {r.Geometry.JunctionCount} · 定型回退 {r.Geometry.Notices.Count}\n采样精度 {r.Environment.StepX:0.#} m\n"+string.Join("\n",r.Issues.GroupBy(i=>i.Reason).Select(g=>$"{g.Key} × {g.Count()}"));
    }
    private void SetRoadBusy(bool busy){if(_roadRun==null)return;foreach(var input in _roadFields)input.Editable=!busy;_roadPattern.Disabled=busy;_roadAutoAngle.Disabled=busy;_roadRun.Disabled=busy;_roadCancel.Disabled=!busy;}
    private async Task RunRoadsAsync(){if(_busy)return;var env=SampledEnvironment;var layout=CurrentSettlement;if(env==null||layout==null){_roadState.Text="请先生成有效的环境采样与聚落布局";return;}
        InvalidateGrading();SetBuildingBusy(true);if(_buildingCancel!=null)_buildingCancel.Disabled=true;SetParcelBusy(true);if(_parcelCancel!=null)_parcelCancel.Disabled=true;
        _busy=true;_roadCancellation=new();SetSamplingBusy(true);SetLayoutBusy(true);SetRoadBusy(true);_sampleCancel.Disabled=true;_layoutCancel.Disabled=true;_roadSelectedAnchor=null;_roadSelectedEdge=-1;
        try{var result=await _roadNode.RunAsync(env,layout,_sampleNode.ResultRevision,_layoutNode.ResultRevision,RoadSettings(),new(_roadCancellation.Token,async p=>{_roadState.Text=$"{p.Message} {p.Fraction:P0}";await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}));if(_roadLayer.Selected==0)_roadLayer.Select(1);ShowRoads();GD.Print($"ROADS_READY edges={result.Edges.Count} cycles={result.CycleCount} revision={_roadNode.ResultRevision}");}
        catch(OperationCanceledException){GD.Print("ROADS_CANCELLED");}catch(Exception ex){GD.PrintErr("ROADS_FAILED: "+ex.Message);_roadLayer.Select(4);ShowRoads();}
        finally{_roadCancellation.Dispose();_roadCancellation=null;_busy=false;SetSamplingBusy(false);SetLayoutBusy(false);SetRoadBusy(false);SetParcelBusy(false);SetBuildingBusy(false);RefreshBuildings();RefreshRoads();RefreshParcels();}
    }
    private void ShowRoads(){if(_roadLayer.Selected>0){HidePlacement();HideBuildings();HideParcels();SetSampleLayer(EnvironmentPreviewLayer.Hidden);_layoutLayer.Select(0);_layoutPreview.Display(_roadNode.LastResult?.Settlement??CurrentSettlement,1,false);_trees.SetPressedNoSignal(false);_map.ShowForest(false);
            _legend.Text=_roadLayer.Selected switch{2=>"连接关系 / 定型中心线 · 圆点：真实节点 · 点击入口高亮通路",3=>"定型占地 / 金色：路段 · 紫色：路口 · 浅边：可临街边界 · 紫边：路口预留",4=>"坡度与问题 / 绿→红：纵坡升高 · 橙圈：定型回退 · 红虚线：被拒候选",_=>"定型道路 / 浅金：主路 · 棕色：次路 · 占地与地块共用同一边界"};}
        if(_roadLayer.Selected>0)_townPreview.Display(_roadNode.LastResult?.Settlement??CurrentSettlement,_roadNode.LastResult);
        _roadPreview.Display(_roadNode.LastResult,_roadLayer.Selected,_roadNode.FailureIssues,CurrentSettlement?.Environment,_roadSelectedAnchor,_roadSelectedEdge);
    }
    private static float RoadDistance(Vector2 p,RoadEdge edge){float best=float.PositiveInfinity;for(int i=1;i<edge.Points.Count;i++){var a=edge.Points[i-1];var v=edge.Points[i]-a;var q=a+v*Math.Clamp((p-a).Dot(v)/v.LengthSquared(),0,1);best=Math.Min(best,p.DistanceTo(q));}return best;}
    private void FocusRoadJunction(){var r=_roadNode.LastResult;if(r==null)return;var node=r.Nodes.Where(n=>r.Edges.Count(e=>e.From==n.Id||e.To==n.Id)>=3).OrderBy(n=>n.Position.DistanceSquaredTo(r.Settlement.Core)).FirstOrDefault()??r.Nodes[0];_camera.TopView();_camera.Focus=new(node.Position.X,node.Height,node.Position.Y);_camera.Size=100;_camera.UpdateTransform();}
    private bool UpdateRoadProbe(Vector2 p){var r=_roadNode.LastResult;if(r==null||_roadLayer.Selected==0)return false;var edge=r.Edges.OrderBy(e=>RoadDistance(p,e)).FirstOrDefault();if(edge==null)return false;var notice=_roadLayer.Selected==4?r.Geometry.Notices.OrderBy(n=>n.Position.DistanceSquaredTo(p)).FirstOrDefault():null;if(notice!=null&&notice.Position.DistanceTo(p)<4){_probe.Text=$"R{notice.RoadId} · {notice.Reason}";return true;}_probe.Text=$"{(CurrentRoads==null?"旧道路":"道路")} · {(r.Contains(p)?"道路占地内":"道路占地外")} · 最近 R{edge.Id} {(edge.Main?"主路":"次路")} 宽 {edge.Width:0.#}m 长 {edge.Length:0.#}m 纵坡 {edge.MaxGrade:0.0}° · 节点 {edge.From}→{edge.To}";return true;}
    private bool HandleRoadClick(InputEvent ev){if(_busy||_roadPanel==null||!_roadPanel.Visible||_roadLayer.Selected==0||_roadNode.LastResult is not {} r||ev is not InputEventMouseButton{Pressed:true,ButtonIndex:MouseButton.Left} b)return false;var hit=PickSettlementGround(b.Position);if(hit==null)return false;var p=hit.Value;
        var entry=r.Settlement.Entries.OrderBy(e=>p.DistanceSquaredTo(e.Position)).First();if(p.DistanceTo(entry.Position)<20){_roadSelectedAnchor=entry.Id;_roadSelectedEdge=-1;_roadState.Text=$"{entry.Id} → 核心：{r.PathTo(entry.Id).Length} 条路段";}
        else{var road=r.Edges.OrderBy(e=>RoadDistance(p,e)).First();_roadSelectedEdge=road.Id;_roadSelectedAnchor=null;_roadState.Text=$"R{road.Id} · {(road.Main?"主路":"次路")}\n宽 {road.Width} m · 长 {road.Length:0.0} m\n最大纵坡 {road.MaxGrade:0.0}° · 节点 {road.From}→{road.To}";}if(CurrentRoads==null)_roadState.Text="旧结果 · "+_roadState.Text;ShowRoads();GetViewport().SetInputAsHandled();return true;
    }
}
