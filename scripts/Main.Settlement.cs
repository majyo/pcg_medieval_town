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
    private MedievalPreview _townPreview=null!;
    private CheckButton _wallEnabled=null!,_civicEnabled=null!;
    private SpinBox _coreRadius=null!,_suburbLength=null!,_suburbWidth=null!,_gateCount=null!;
    private SpinBox _wallInset=null!,_wallWidth=null!,_wallBuffer=null!,_gateWidth=null!;
    private readonly SettlementLayoutNode _layoutNode=new();
    private SettlementPreview _layoutPreview=null!;
    private PanelContainer _layoutPanel=null!;
    private readonly List<SpinBox> _layoutFields=new();
    private SpinBox _layoutX=null!,_layoutZ=null!,_layoutRadius=null!,_coreX=null!,_coreZ=null!,_entryCount=null!,_plazaSize=null!,_minimumArea=null!,_layoutSeed=null!;
    private CheckButton _manualCore=null!,_layoutIntents=null!;
    private LineEdit _manualEntries=null!;
    private OptionButton _layoutLayer=null!,_layoutPick=null!;
    private Label _layoutState=null!,_layoutSummary=null!;
    private Button _layoutRun=null!,_layoutCancel=null!;
    private CancellationTokenSource? _layoutCancellation;
    private Vector2? _hoverGround;
    private SettlementOptions LayoutOptions()=>new(new((float)_layoutX.Value,(float)_layoutZ.Value),(float)_layoutRadius.Value,
        _manualCore.ButtonPressed?new Vector2((float)_coreX.Value,(float)_coreZ.Value):null,(int)_entryCount.Value,(float)_plazaSize.Value,_minimumArea.Value,(int)_layoutSeed.Value,_manualEntries.Text,_wallEnabled.ButtonPressed,(float)_wallInset.Value,(float)_wallWidth.Value,(float)_wallBuffer.Value,(float)_gateWidth.Value,_civicEnabled.ButtonPressed,CoreRadius:(float)_coreRadius.Value,SuburbLength:(float)_suburbLength.Value,SuburbWidth:(float)_suburbWidth.Value,GateCount:(int)_gateCount.Value);
    public SettlementLayout? CurrentSettlement=>_layoutNode.IsCurrent(SampledEnvironment,_sampleNode.ResultRevision,LayoutOptions())?_layoutNode.LastResult:null;
    private void BuildSettlementInterface(Control root)
    {
        _townPreview=new MedievalPreview{Name="TownPlanningPreview"};AddChild(_townPreview);
        _layoutPreview=new SettlementPreview{Name="SettlementPreview"};AddChild(_layoutPreview);
        var selector=new OptionButton{Name="PcgNodeSelector",Position=new Vector2(-344,24),CustomMinimumSize=new Vector2(320,34)};
        root.AddChild(selector);selector.SetAnchorsPreset(Control.LayoutPreset.TopRight);selector.OffsetLeft=-344;selector.OffsetRight=-24;
        selector.AddItem("01 环境采样");selector.AddItem("02 城镇区域布局");
        var sampling=(PanelContainer)root.GetNode("SamplingPanel");sampling.OffsetTop=66;
        _layoutPanel=new PanelContainer{Name="SettlementPanel",Visible=false};root.AddChild(_layoutPanel);
        _layoutPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);_layoutPanel.OffsetLeft=-344;_layoutPanel.OffsetRight=-24;_layoutPanel.OffsetTop=66;_layoutPanel.OffsetBottom=-129;
        _layoutPanel.AddThemeStyleboxOverride("panel",Box(new Color(.075f,.12f,.10f,.96f),12,18));
        selector.ItemSelected+=v=>{sampling.Visible=v==0;_layoutPanel.Visible=v==1;};
        var scroll=new ScrollContainer{HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};_layoutPanel.AddChild(scroll);
        var col=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};scroll.AddChild(col);col.AddThemeConstantOverride("separation",5);
        col.AddChild(Text("城镇区域布局",24,Ink));col.AddChild(Text("输入：当前有效的环境采样结果",12,Muted));
        SpinBox Field(string name,double value,double min,double max,double step=1){var row=new HBoxContainer();col.AddChild(row);var label=Text(name,12,Muted);label.CustomMinimumSize=new(125,0);row.AddChild(label);var s=new SpinBox{MinValue=min,MaxValue=max,Step=step,Value=value,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};row.AddChild(s);_layoutFields.Add(s);s.ValueChanged+=_=>InvalidateSettlement();return s;}
        _layoutX=Field("规划中心 X",0,-10000,10000);_layoutZ=Field("规划中心 Z",0,-10000,10000);_layoutRadius=Field("核心选址范围 / m",300,10,10000);
        _coreRadius=Field("核心区尺度 / m",210,90,300);_suburbLength=Field("城外街带长度 / m",480,150,1500);_suburbWidth=Field("城外街带宽 / m",140,50,300);_gateCount=Field("城门数量",2,1,4);
        _wallEnabled=new CheckButton{Text="城墙占位布局",ButtonPressed=true};col.AddChild(_wallEnabled);_wallEnabled.Toggled+=_=>InvalidateSettlement();
        _wallInset=Field("城墙退让 / m",22,8,100);_wallWidth=Field("墙体宽度 / m",4,1,12);_wallBuffer=Field("墙侧缓冲 / m",4,0,20);_gateWidth=Field("城门开口 / m",22,8,40);
        _civicEnabled=new CheckButton{Text="城内教堂与城外修道院",ButtonPressed=true};col.AddChild(_civicEnabled);_civicEnabled.Toggled+=_=>InvalidateSettlement();
        _manualCore=new CheckButton{Text="手动主核心"};col.AddChild(_manualCore);_manualCore.Toggled+=_=>InvalidateSettlement();
        _coreX=Field("主核心 X",0,-10000,10000);_coreZ=Field("主核心 Z",0,-10000,10000);
        _entryCount=Field("区域通路入口数",3,1,4);_plazaSize=Field("广场边长 / m",40,1,1000);_minimumArea=Field("最小面积 / m²",20000,100,10000000,100);_layoutSeed=Field("布局种子",73129,0,int.MaxValue);
        col.AddChild(Text("手动入口 X,Z;X,Z（空白为自动）",12,Muted));
        _manualEntries=new LineEdit{PlaceholderText="通过地图吸附选择，或输入坐标"};col.AddChild(_manualEntries);_manualEntries.TextChanged+=_=>InvalidateSettlement();
        _layoutPick=new OptionButton();foreach(var name in new[]{"查看 / 选择对象","指定规划中心","指定主核心","添加手动入口（需已有布局）"})_layoutPick.AddItem(name);col.AddChild(_layoutPick);
        var clear=Button("清空手动入口");col.AddChild(clear);clear.Pressed+=()=>{if(_busy)return;_manualEntries.Text="";InvalidateSettlement();};
        var rowButtons=new HBoxContainer();col.AddChild(rowButtons);_layoutRun=Button("独立生成布局");_layoutCancel=Button("取消");_layoutCancel.Disabled=true;rowButtons.AddChild(_layoutRun);rowButtons.AddChild(_layoutCancel);
        _layoutRun.Pressed+=async()=>await RunSettlementAsync();_layoutCancel.Pressed+=()=>_layoutCancellation?.Cancel();
        _layoutState=Text("未运行",12,Accent);_layoutState.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_layoutState);
        _layoutLayer=new OptionButton();foreach(var name in new[]{"隐藏布局","总体分布","城内 / 城外规划","区域交通联系","防御范围"})_layoutLayer.AddItem(name);col.AddChild(_layoutLayer);_layoutLayer.ItemSelected+=_=>ShowSettlement();
        _layoutIntents=new CheckButton{Text="显示连接意图（非道路）"};col.AddChild(_layoutIntents);_layoutIntents.Toggled+=_=>ShowSettlement();
        var focus=Button("定位布局 / 正俯视");col.AddChild(focus);focus.Pressed+=FocusSettlement;var study=Button("查看整个研究范围");col.AddChild(study);study.Pressed+=FocusStudy;
        _layoutSummary=Text("运行后显示面积、入口和诊断",12,Muted);_layoutSummary.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_layoutSummary);
        col.MoveChild(rowButtons,2);col.MoveChild(_layoutState,3);col.MoveChild(_layoutSummary,4);col.MoveChild(_layoutLayer,5);
    }
    private void FocusSettlement(){_camera.TopView();var bounds=_layoutNode.LastResult?.Region?.ViewBounds;var center=bounds?.GetCenter()??new Vector2((float)_layoutX.Value,(float)_layoutZ.Value);_camera.Focus=new(center.X,15,center.Y);_camera.Size=bounds.HasValue?Math.Max(bounds.Value.Size.X*1.9f,bounds.Value.Size.Y*1.4f):(float)_layoutRadius.Value*4.5f;_camera.UpdateTransform();}
    private void FocusStudy(){_camera.TopView();_camera.Size=_settings.SizeMeters*1.65f;_camera.UpdateTransform();}
    private void InvalidateSettlement(){_layoutNode.Invalidate();InvalidateRoads();if(_layoutState!=null)RefreshSettlement();}
    private void RefreshSettlement(){if(_layoutState==null)return;bool valid=CurrentSettlement!=null;
        _layoutState.Text=_layoutNode.State switch{SampleNodeState.Valid=>valid?"有效":"已过期",SampleNodeState.Running=>"生成中…",SampleNodeState.Stale=>"已过期",SampleNodeState.Failed=>"失败："+_layoutNode.Error,SampleNodeState.Cancelled=>"已取消",_=>"未运行"};
        if(SampledEnvironment==null)_layoutState.Text+=" · 请先运行环境采样";
        if(!valid&&_layoutNode.LastResult!=null)_layoutState.Text+="\n当前显示旧结果";
        if(_layoutNode.LastResult is {} d)_layoutSummary.Text=$"实际区域 {d.Area/10000:0.00} 公顷\n广场预留 {d.ReservedArea:0} m²\n剩余候选 {(d.Area-d.ReservedArea)/10000:0.00} 公顷\n入口 {d.Entries.Count} · 城门 {d.Town?.Gates.Count??0} · 院区 {d.Town?.Precincts.Count??0} · 问题 {d.Warnings.Count}\n网格 {d.Environment.StepX:0.#} × {d.Environment.StepZ:0.#} m\n"+string.Join("\n",d.Entries.Select(b=>$"{b.Id}: {b.Position.X:0.#}, {b.Position.Y:0.#}"))+"\n"+string.Join("\n",d.Warnings);
        if(_layoutNode.LastResult?.Region is {} region){double core=region.Districts.Where(x=>x.Role==TownDistrictRole.FortifiedCore).Sum(x=>x.Shape.Area);_layoutSummary.Text=$"研究范围 {region.StudyArea.Size.X:0} × {region.StudyArea.Size.Y:0} m\n城内规划 {core/10000:0.00} 公顷\n城外规划 {(region.DevelopmentArea.Area-core)/10000:0.00} 公顷\n片区 {region.Districts.Count} · 田园 {region.Landscape.Count}\n区域入口 {_layoutNode.LastResult.Entries.Count} · 城门 {_layoutNode.LastResult.Town?.Gates.Count??0}";}
    }
    private void SetLayoutBusy(bool busy){foreach(var s in _layoutFields)s.Editable=!busy;_wallEnabled.Disabled=busy;_civicEnabled.Disabled=busy;_manualCore.Disabled=busy;_manualEntries.Editable=!busy;_layoutPick.Disabled=busy;_layoutRun.Disabled=busy;_layoutCancel.Disabled=!busy;}
    private async Task RunSettlementAsync(){if(_busy)return;var env=SampledEnvironment;if(env==null){_layoutState.Text="请先运行有效的环境采样";return;}
        InvalidateRoads();SetBuildingBusy(true);if(_buildingCancel!=null)_buildingCancel.Disabled=true;SetParcelBusy(true);if(_parcelCancel!=null)_parcelCancel.Disabled=true;SetRoadBusy(true);if(_roadCancel!=null)_roadCancel.Disabled=true;
        _busy=true;_layoutCancellation=new();SetSamplingBusy(true);_sampleCancel.Disabled=true;SetLayoutBusy(true);
        try{var result=await _layoutNode.RunAsync(env,_sampleNode.ResultRevision,LayoutOptions(),new PcgContext(_layoutCancellation.Token,async p=>{_layoutState.Text=$"{p.Message} {p.Fraction:P0}";await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}));
            if(_layoutLayer.Selected==0)_layoutLayer.Select(1);ShowSettlement();GD.Print($"SETTLEMENT_READY area={result.Area} entries={result.Entries.Count} revision={_layoutNode.ResultRevision}");
        }catch(OperationCanceledException){GD.Print("SETTLEMENT_CANCELLED");}catch(Exception ex){GD.PrintErr("SETTLEMENT_FAILED: "+ex.Message);}
        finally{_layoutCancellation.Dispose();_layoutCancellation=null;_busy=false;SetSamplingBusy(false);SetLayoutBusy(false);SetRoadBusy(false);SetParcelBusy(false);SetBuildingBusy(false);RefreshBuildings();RefreshParcels();RefreshSettlement();RefreshRoads();}
    }
    private void ShowSettlement(){if(_layoutLayer.Selected>0){HidePlacement();HideBuildings();HideParcels();HideRoads();SetSampleLayer(EnvironmentPreviewLayer.Hidden);_trees.SetPressedNoSignal(false);_map.ShowForest(false);_legend.Text=_layoutLayer.Selected switch{2=>"区域用地 / 金：城内核心 · 绿：城外建设片区 · 规划面积不等于实际建设面积",3=>"区域交通 / 蓝线为待细化通路 · E 为对外入口 · G 为城门，两者独立",4=>"防御范围 / 城墙只保护核心，城外聚落仍保持可见",_=>"城镇区域 / 设防核心 + 城门外街带 + 城外机构聚落 + 片区间田园"};}_layoutPreview.Display(_layoutNode.LastResult,_layoutLayer.Selected,_layoutIntents.ButtonPressed);_townPreview.Display(_layoutNode.LastResult);}
    private static string LayoutStateText(SettlementCellState s)=>s switch{SettlementCellState.Included=>"纳入区域",SettlementCellState.Disconnected=>"未纳入：不连通",SettlementCellState.Restricted=>"环境受限",SettlementCellState.Unknown=>"信息未知",SettlementCellState.Edge=>"规划边缘截断",_=>"规划范围外"};
    private bool UpdateSettlementProbe(Vector2 p){_hoverGround=p;var d=_layoutNode.LastResult;if(d==null||_layoutLayer.Selected==0)return false;if(d.Region is {} region){var district=region.DistrictAt(p);var field=region.Landscape.FirstOrDefault(f=>f.Shape.Contains(p));_probe.Text=$"{(CurrentSettlement==null?"旧布局":"区域")} X {p.X:0} Z {p.Y:0} · {district?.Name??field?.Use??"研究范围内未开发地 / 环境限制"} · {(d.Town?.Interior.Contains(p)==true?"核心范围":"核心之外")}";return true;}if(d.TryCell(p,out var c))_probe.Text=$"{(CurrentSettlement==null?"旧布局":"布局")} X {p.X:0} Z {p.Y:0} | {LayoutStateText(c.State)} · {ReasonText(c.Reasons)} | 评分 {(c.Score<0?"不可选":c.Score.ToString("0.000"))} · 范围平缓 {c.Flatness:0.00} 接坡风险 {c.GradingRisk:0.000} m 空间 {c.LocalSpace:0.00} 中心 {c.Centrality:0.00} 植被 {c.VegetationPreference:0.00}";else _probe.Text="布局范围外";return true;}
    private Vector2? PickSettlementGround(Vector2 screen){
        var origin=_camera.ProjectRayOrigin(screen);var ray=_camera.ProjectRayNormal(screen);
        for(float t=0;t<6500;t+=20){var q=origin+ray*t;var xz=new Vector2(q.X,q.Z);if(!_data.Contains(xz)||q.Y>DisplayTerrain.HeightAt(xz))continue;
            float lo=Math.Max(0,t-20),hi=t;for(int i=0;i<12;i++){float mid=(lo+hi)/2;var v=origin+ray*mid;var p=new Vector2(v.X,v.Z);if(!_data.Contains(p)||v.Y>DisplayTerrain.HeightAt(p))lo=mid;else hi=mid;}var hit=origin+ray*hi;return new Vector2(hit.X,hit.Z);
        }return null;
    }
    public override void _UnhandledInput(InputEvent ev){if(HandleGradingClick(ev))return;if(HandleCityClick(ev))return;if(HandlePlacementClick(ev))return;if(HandleBuildingClick(ev))return;if(HandleParcelClick(ev))return;if(HandleRoadClick(ev))return;if(_busy||!_layoutPanel.Visible||ev is not InputEventMouseButton{Pressed:true,ButtonIndex:MouseButton.Left} )return;var ground=PickSettlementGround(((InputEventMouseButton)ev).Position);if(ground==null)return;var p=ground.Value;
        if(_layoutPick.Selected==1){_layoutX.Value=p.X;_layoutZ.Value=p.Y;}
        else if(_layoutPick.Selected==2){_manualCore.ButtonPressed=true;_coreX.Value=p.X;_coreZ.Value=p.Y;}
        else if(_layoutPick.Selected==3&&_layoutNode.LastResult is {} d&&ReferenceEquals(d.Environment,SampledEnvironment)){
            if(d.Options with{ManualEntries=_manualEntries.Text}!=LayoutOptions()){_layoutState.Text="布局参数已改变，请先生成再选择入口";return;}
            var candidates=d.EntryCandidates.OrderBy(b=>((b.A+b.B)/2).DistanceSquaredTo(p)).ToArray();if(candidates.Length==0)return;var q=(candidates[0].A+candidates[0].B)/2;
            if(q.DistanceTo(p)>Math.Max(30,d.Environment.StepX*2)){_layoutState.Text="请点击合法的外轮廓附近";return;}
            _manualEntries.Text+=(_manualEntries.Text.Length>0?";":"")+FormattableString.Invariant($"{q.X},{q.Y}");InvalidateSettlement();
        }else if(_layoutNode.LastResult is {} data){var closest=data.Entries.OrderBy(b=>b.Position.DistanceSquaredTo(p)).First();_layoutState.Text=p.DistanceTo(data.Core)<p.DistanceTo(closest.Position)?$"主核心 {data.Core} · 高程 {data.CoreHeight:0.0}":$"{closest.Id} {closest.Position} · 朝外 {closest.Edge.Outward}";}
        GetViewport().SetInputAsHandled();
    }
}
