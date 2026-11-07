using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class Main
{
    private async Task VerifyParcelInterface(){
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException("PARCEL_UI_FAILED: "+message);}
        var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(ParcelStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)ParcelStage);Check(_parcelPanel.Visible&&!_roadPanel.Visible,"node selector");Check(ParcelSettings()==new ParcelOptions(),"default parameters");
        var roads=CurrentRoads;var layout=CurrentSettlement;var env=SampledEnvironment;long rr=_roadNode.ResultRevision;
        _parcelRun.EmitSignal(Godot.Button.SignalName.Pressed);int frames=0;while(_busy&&frames++<10000)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Check(!_busy&&CurrentParcels?.Parcels.Count>10&&_parcelPreview.Visible,"independent generation and preview");Check(ReferenceEquals(roads,CurrentRoads)&&ReferenceEquals(layout,CurrentSettlement)&&ReferenceEquals(env,SampledEnvironment)&&rr==_roadNode.ResultRevision,"upstream reuse");
        Check(CurrentParcels!.OutsideDevelopedArea>CurrentParcels.InsideDevelopedArea,"actual development outside walls");
        long rev=_parcelNode.ResultRevision;FocusSettlement();var dir=ProjectSettings.GlobalizePath("res://artifacts/captures");Directory.CreateDirectory(dir);
        for(int layer=1;layer<=4;layer++){_parcelLayer.Select(layer);_parcelLayer.EmitSignal(OptionButton.SignalName.ItemSelected,(long)layer);Check(_parcelPreview.Layer==layer,"four layers");for(int i=0;i<4;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);using var image=GetViewport().GetTexture().GetImage();Check(image.SavePng(Path.Combine(dir,$"parcels_{layer}.png"))==Error.Ok,"render capture");}
        _parcelLayer.Select(2);ShowParcels();var first=CurrentParcels!.Parcels[0];var t=first.Shape.Triangles;int ti=Enumerable.Range(0,t.Count/3).OrderByDescending(i=>Math.Abs((t[i*3+1]-t[i*3]).Cross(t[i*3+2]-t[i*3]))).First()*3;var p=(t[ti]+t[ti+1]+t[ti+2])/3;CurrentParcels.Terrain.TryHeightAt(p,out float h);var screen=_camera.UnprojectPosition(new Vector3(p.X,h,p.Y));
        GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,ButtonIndex=MouseButton.Left,Pressed=true},true);GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,ButtonIndex=MouseButton.Left,Pressed=false},true);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Check(_selectedParcel==first.Id&&_parcelNode.ResultRevision==rev,"map selection without generation");
        _parcelWidth.Value=16;Check(CurrentParcels==null&&_parcelState.Text.Contains("旧结果"),"parameter invalidation");await RunParcelsAsync();Check(CurrentParcels!=null,"local rerun");
        var task=RunParcelsAsync();_parcelCancel.EmitSignal(Godot.Button.SignalName.Pressed);await task;Check(_parcelNode.State==SampleNodeState.Cancelled&&CurrentParcels==null,"cancel without publishing");_parcelWidth.Value=8;await RunParcelsAsync();
        _roadSpacing.Value=90;Check(CurrentParcels==null,"road invalidation");_roadSpacing.Value=80;await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();Check(CurrentParcels!=null,"roads to parcels");
        _layoutRadius.Value=320;Check(CurrentParcels==null,"settlement invalidation");_layoutRadius.Value=300;await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();Check(CurrentParcels!=null,"settlement chain");
        _sampleSpacing.Value=20;Check(CurrentParcels==null,"sampling invalidation");_sampleSpacing.Value=10;await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();Check(CurrentParcels!=null,"sampling chain");
        Generate();Check(CurrentParcels==null,"map invalidation");await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();Check(CurrentParcels!=null,"entire map to parcels workflow");
        HideParcels();HideRoads();_layoutPreview.Display(_layoutNode.LastResult,0,false);selector.Select(0);selector.EmitSignal(OptionButton.SignalName.ItemSelected,0L);_camera.ResetView();_trees.SetPressedNoSignal(true);_map.ShowForest(true);
        GD.Print("PARCEL_UI_PASS layers=4 selection=ok reuse=ok rerun=ok cancel=ok full_chain=ok");
    }
}
