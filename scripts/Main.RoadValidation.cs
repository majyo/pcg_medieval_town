using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class Main
{
    private async Task VerifyRoadInterface(){
        void Check(bool b,string m){if(!b)throw new InvalidOperationException("ROAD_UI_FAILED: "+m);}
        var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(2);selector.EmitSignal(OptionButton.SignalName.ItemSelected,2L);
        Check(_roadPanel.Visible&&!_layoutPanel.Visible,"node selector");Check(RoadSettings()==new RoadOptions(),"defaults");
        var env=SampledEnvironment;var layout=CurrentSettlement;long er=_sampleNode.ResultRevision,lr=_layoutNode.ResultRevision;
        _roadRun.EmitSignal(Godot.Button.SignalName.Pressed);int frames=0;while(_busy&&frames++<8000)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Check(!_busy&&CurrentRoads!=null&&_roadPreview.Visible,"independent run button");Check(ReferenceEquals(env,SampledEnvironment)&&ReferenceEquals(layout,CurrentSettlement)&&er==_sampleNode.ResultRevision&&lr==_layoutNode.ResultRevision,"upstream reuse");
        long rev=_roadNode.ResultRevision;FocusSettlement();
        string dir=ProjectSettings.GlobalizePath("res://artifacts/captures");Directory.CreateDirectory(dir);
        for(int layer=1;layer<=4;layer++){
            _roadLayer.Select(layer);_roadLayer.EmitSignal(OptionButton.SignalName.ItemSelected,(long)layer);Check(_roadPreview.Layer==layer,"four layer callbacks");
            for(int i=0;i<4;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);using var image=GetViewport().GetTexture().GetImage();Check(image.SavePng(Path.Combine(dir,$"roads_{layer}.png"))==Error.Ok,"screenshot");
        }
        var entry=CurrentRoads!.Settlement.Entries[0];var screen=_camera.UnprojectPosition(new Vector3(entry.Position.X,entry.Height,entry.Position.Y));
        GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,Pressed=true,ButtonIndex=MouseButton.Left},true);GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,Pressed=false,ButtonIndex=MouseButton.Left},true);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);Check(_roadSelectedAnchor==entry.Id&&_roadNode.ResultRevision==rev,"entry click path highlight no generation");
        _roadSpacing.Value=90;Check(CurrentRoads==null&&_roadState.Text.Contains("旧结果"),"parameter invalidation");await RunRoadsAsync();Check(CurrentRoads!=null,"local rerun");
        var task=RunRoadsAsync();_roadCancel.EmitSignal(Godot.Button.SignalName.Pressed);await task;Check(_roadNode.State==SampleNodeState.Cancelled&&CurrentRoads==null,"cancellation");
        _roadSpacing.Value=80;await RunRoadsAsync();_layoutRadius.Value=320;Check(CurrentRoads==null,"layout invalidation");_layoutRadius.Value=300;await RunSettlementAsync();await RunRoadsAsync();Check(CurrentRoads!=null,"layout then roads");
        _sampleSpacing.Value=20;Check(CurrentRoads==null,"sampling invalidation");_sampleSpacing.Value=10;await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();Check(CurrentRoads!=null,"full dependency chain");
        Generate();Check(CurrentRoads==null,"map rebuild invalidation");await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();Check(CurrentRoads!=null,"map through road chain");
        HideRoads();_layoutPreview.Display(_layoutNode.LastResult,0,false);selector.Select(0);selector.EmitSignal(OptionButton.SignalName.ItemSelected,0L);_camera.ResetView();_trees.SetPressedNoSignal(true);_map.ShowForest(true);
        GD.Print("ROAD_UI_PASS layers=4 selection=ok reuse=ok rerun=ok cancel=ok upstream_stale=ok full_chain=ok");
    }
}
