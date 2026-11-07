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
    private readonly ParcelNode _parcelNode=new();private ParcelPreview _parcelPreview=null!;private PanelContainer _parcelPanel=null!;
    private readonly List<SpinBox> _parcelFields=new();
    private SpinBox _parcelWidth=null!,_parcelDepth=null!,_parcelArea=null!,_parcelFrontage=null!,_parcelRoadSetback=null!,_parcelSideSetback=null!,_parcelCommercial=null!,_parcelProduction=null!,_parcelSeed=null!;
    private Button _parcelRun=null!,_parcelCancel=null!;private OptionButton _parcelLayer=null!;private Label _parcelState=null!,_parcelSummary=null!;private CancellationTokenSource? _parcelCancellation;private int _selectedParcel=-1;
    private ParcelOptions ParcelSettings()=>new((float)_parcelWidth.Value,(float)_parcelDepth.Value,_parcelArea.Value,(float)_parcelFrontage.Value,(float)_parcelRoadSetback.Value,(float)_parcelSideSetback.Value,(float)_parcelCommercial.Value/100,(float)_parcelProduction.Value/100,(int)_parcelSeed.Value);
    public ParcelLayout? CurrentParcels=>_parcelNode.IsCurrent(CurrentGrading,_gradingNode.ResultRevision,ParcelSettings())?_parcelNode.LastResult:null;
    private void BuildParcelInterface(Control root){
        _parcelPreview=new ParcelPreview{Name="ParcelPreview"};AddChild(_parcelPreview);var selector=(OptionButton)root.GetNode("PcgNodeSelector");selector.AddItem("04 街区与地块");
        _parcelPanel=new PanelContainer{Name="ParcelPanel",Visible=false};root.AddChild(_parcelPanel);_parcelPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);_parcelPanel.OffsetLeft=-344;_parcelPanel.OffsetRight=-24;_parcelPanel.OffsetTop=66;_parcelPanel.OffsetBottom=-129;_parcelPanel.AddThemeStyleboxOverride("panel",Box(new Color(.075f,.12f,.1f,.96f),12,18));selector.ItemSelected+=i=>_parcelPanel.Visible=i==ParcelStage;
        var scroll=new ScrollContainer{HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};_parcelPanel.AddChild(scroll);var col=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};col.AddThemeConstantOverride("separation",6);scroll.AddChild(col);col.AddChild(Text("街区与地块",24,Ink));col.AddChild(Text("输入：有效 03B 道路竖向与修整地形",12,Muted));
        SpinBox Field(string name,double value,double min,double max,double step=1){var row=new HBoxContainer();col.AddChild(row);var label=Text(name,12,Muted);label.CustomMinimumSize=new(125,0);row.AddChild(label);var input=new SpinBox{MinValue=min,MaxValue=max,Step=step,Value=value,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};row.AddChild(input);_parcelFields.Add(input);input.ValueChanged+=_=>InvalidateParcels();return input;}
        _parcelWidth=Field("目标面宽 / m",8,4,100);_parcelDepth=Field("宅地最大进深 / m",70,4,200);_parcelArea=Field("最小面积 / m²",60,10,10000,10);_parcelFrontage=Field("最小临街 / m",4,1,100);
        _parcelRoadSetback=Field("道路退界 / m",0,0,50,.5);_parcelSideSetback=Field("侧后退界 / m",0,0,20,.5);_parcelCommercial=Field("商业目标 / %",20,0,100);_parcelProduction=Field("生产目标 / %",10,0,100);_parcelSeed=Field("地块种子",73129,0,int.MaxValue);
        var buttons=new HBoxContainer();col.AddChild(buttons);_parcelRun=Button("独立生成地块");_parcelCancel=Button("取消");_parcelCancel.Disabled=true;buttons.AddChild(_parcelRun);buttons.AddChild(_parcelCancel);_parcelRun.Pressed+=async()=>await RunParcelsAsync();_parcelCancel.Pressed+=()=>_parcelCancellation?.Cancel();
        _parcelState=Text("未运行",12,Accent);_parcelState.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_parcelState);
        _parcelLayer=new OptionButton();foreach(string name in new[]{"隐藏地块","街区边界","地块与用途","可建筑范围 / 退界","剩余空地"})_parcelLayer.AddItem(name);col.AddChild(_parcelLayer);_parcelLayer.ItemSelected+=_=>ShowParcels();var focus=Button("定位街区 / 正俯视");col.AddChild(focus);focus.Pressed+=FocusSettlement;
        _parcelSummary=Text("运行后显示街区、地块和面积",12,Muted);_parcelSummary.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(_parcelSummary);
    }
    private void InvalidateParcels(){_parcelNode.Invalidate();InvalidateBuildings();if(_parcelState!=null)RefreshParcels();}
    private void HideParcels(){if(_parcelLayer==null)return;_parcelLayer.Select(0);_parcelPreview.Display(_parcelNode.LastResult,0);}
    private void SetParcelBusy(bool busy){if(_parcelRun==null)return;foreach(var field in _parcelFields)field.Editable=!busy;_parcelRun.Disabled=busy;_parcelCancel.Disabled=!busy;}
    private static string UseName(ParcelUse use)=>use switch{ParcelUse.Commercial=>"商住",ParcelUse.Production=>"作坊住宅",_=>"住宅"};
    private void RefreshParcels(){if(_parcelState==null)return;_parcelState.Text=_parcelNode.State switch{SampleNodeState.Valid=>CurrentParcels!=null?"有效":"已过期",SampleNodeState.Stale=>"已过期",SampleNodeState.Running=>"生成中…",SampleNodeState.Cancelled=>"已取消",SampleNodeState.Failed=>"失败："+_parcelNode.Error,_=>"未运行"};if(CurrentParcels is {} current&&current.Parcels.Count==0)_parcelState.Text+=" · 没有满足限制的地块，请查看剩余空地";if(CurrentGrading==null)_parcelState.Text+="\n请先生成有效的 03B 道路竖向";if(_parcelNode.LastResult!=null&&CurrentParcels==null)_parcelState.Text+="\n保留旧结果（不可用于下游）";
        if(_parcelNode.LastResult is {} d)_parcelSummary.Text=$"街区 {d.Blocks.Count} · 地块 {d.Parcels.Count}\n城内宅地 {d.InsideDevelopedArea/10000:0.00} 公顷\n城外宅地 {d.OutsideDevelopedArea/10000:0.00} 公顷\n街区土地 {d.AvailableArea/10000:0.00} 公顷\n地块 {d.ParcelArea/10000:0.00} 公顷\n剩余空地 {d.RemainderArea/10000:0.00} 公顷\n"+string.Join("\n",d.Parcels.GroupBy(p=>p.Use).Select(g=>$"{UseName(g.Key)} {g.Count()} 块 · {g.Sum(p=>p.Shape.Area)/Math.Max(1,d.ParcelArea):P0}"))+"\n点击地块查看临街道路与高度";
    }
    private async Task RunParcelsAsync(){if(_busy)return;var env=SampledEnvironment;var layout=CurrentSettlement;var roads=CurrentRoads;var grading=CurrentGrading;if(env==null||layout==null||roads==null||grading==null){_parcelState.Text="请先生成有效的 03B 道路竖向";return;}
        InvalidateBuildings();
        SetBuildingBusy(true);if(_buildingCancel!=null)_buildingCancel.Disabled=true;
        _busy=true;_parcelCancellation=new();SetSamplingBusy(true);SetLayoutBusy(true);SetRoadBusy(true);SetParcelBusy(true);_sampleCancel.Disabled=true;_layoutCancel.Disabled=true;_roadCancel.Disabled=true;_selectedParcel=-1;
        try{var result=await _parcelNode.RunAsync(grading,_gradingNode.ResultRevision,ParcelSettings(),new(_parcelCancellation.Token,async p=>{_parcelState.Text=$"{p.Message} {p.Fraction:P0}";await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}));if(_parcelLayer.Selected==0)_parcelLayer.Select(2);ShowParcels();GD.Print($"PARCELS_READY blocks={result.Blocks.Count} parcels={result.Parcels.Count} revision={_parcelNode.ResultRevision}");}
        catch(OperationCanceledException){GD.Print("PARCELS_CANCELLED");}catch(Exception ex){GD.PrintErr("PARCELS_FAILED: "+ex.Message);}
        finally{_parcelCancellation.Dispose();_parcelCancellation=null;_busy=false;SetSamplingBusy(false);SetLayoutBusy(false);SetRoadBusy(false);SetParcelBusy(false);SetBuildingBusy(false);RefreshParcels();RefreshBuildings();}
    }
    private void ShowParcels(){if(_parcelLayer.Selected>0){HidePlacement();HideBuildings();SetSampleLayer(EnvironmentPreviewLayer.Hidden);_layoutLayer.Select(0);_layoutPreview.Display(_parcelNode.LastResult?.Settlement??CurrentSettlement,0,false);_roadLayer.Select(0);_roadPreview.Display(_parcelNode.LastResult?.Roads,1);_trees.SetPressedNoSignal(false);_map.ShowForest(false);_legend.Text=_parcelLayer.Selected switch{1=>"街区 / 从聚落中扣除道路与广场后的连通土地 · 内部孔洞保留",2=>"地块用途 / 绿：住宅 · 黄：商住 · 紫：作坊住宅 · 点击显示真实临街边",3=>"可建筑范围 / 青绿：临街建筑带 · 轮廓内其余部分为后院/园地",_=>"剩余空地 / 橙：公共院地与未分配土地"};}
        if(_parcelLayer.Selected>0)_townPreview.Display(_parcelNode.LastResult?.Settlement??CurrentSettlement,_parcelNode.LastResult?.Roads);
        _parcelPreview.Display(_parcelNode.LastResult,_parcelLayer.Selected,_selectedParcel);
    }
    private bool UpdateParcelProbe(Vector2 p){if(_parcelLayer==null||_parcelLayer.Selected==0||_parcelNode.LastResult is not {} d)return false;var parcel=d.Parcels.FirstOrDefault(a=>a.Shape.Bounds.HasPoint(p)&&a.Shape.Contains(p));string prefix=CurrentParcels==null?"旧地块":"地块";if(parcel!=null)_probe.Text=$"{prefix} P{parcel.Id} · 街区 B{parcel.BlockId} · {UseName(parcel.Use)} · {parcel.Shape.Area:0} m² · 可建筑 {parcel.Buildable.Area:0} m² · 临街 {parcel.Frontage.Sum(f=>f.Length):0.#} m · 最大坡度 {parcel.MaxSlope:0.#}°";else _probe.Text=$"{prefix} / "+(d.Remainders.FirstOrDefault(r=>r.Shape.Contains(p))?.Reason??"道路、城墙、公共预留或区域外");return true;}
    private bool HandleParcelClick(InputEvent ev){if(_busy||_parcelPanel==null||!_parcelPanel.Visible||_parcelLayer.Selected==0||_parcelNode.LastResult is not {} d||ev is not InputEventMouseButton{Pressed:true,ButtonIndex:MouseButton.Left} b)return false;var hit=PickSettlementGround(b.Position);if(hit==null)return false;var parcel=d.Parcels.FirstOrDefault(p=>p.Shape.Contains(hit.Value));if(parcel!=null){_selectedParcel=parcel.Id;_parcelState.Text=$"{(CurrentParcels==null?"旧结果 · ":"")}P{parcel.Id} / B{parcel.BlockId} · {UseName(parcel.Use)}\n临街道路：{string.Join(", ",parcel.Frontage.Select(f=>$"R{f.RoadId}").Distinct())}\n{(parcel.InsideWalls?"城内":"城外")} · 后院 {parcel.Yard?.Area??0:0} m²\n高程 {parcel.MinHeight:0.#}–{parcel.MaxHeight:0.#} m\n可建筑面积 {parcel.Buildable.Area:0} m²";}else _selectedParcel=-1;ShowParcels();GetViewport().SetInputAsHandled();return true;}
}
