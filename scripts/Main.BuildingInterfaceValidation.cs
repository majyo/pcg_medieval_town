using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox;
public partial class Main
{
    private async Task VerifyBuildingInterface(){
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException("BUILDING_UI_FAILED: "+message);}
        var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(BuildingStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)BuildingStage);
        Check(_buildingPanel.Visible&&!_parcelPanel.Visible,"node selection");Check(BuildingSettings()==new PcgCitySandbox.Pcg.BuildingOptions(),"default parameters");
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        var parcels=CurrentParcels;long upstream=_parcelNode.ResultRevision;
        var pos=_buildingRun.GetGlobalRect().GetCenter();GetViewport().PushInput(new InputEventMouseMotion{Position=pos,GlobalPosition=pos},true);
        GetViewport().PushInput(new InputEventMouseButton{Position=pos,GlobalPosition=pos,ButtonIndex=MouseButton.Left,Pressed=true},true);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        GetViewport().PushInput(new InputEventMouseButton{Position=pos,GlobalPosition=pos,ButtonIndex=MouseButton.Left,Pressed=false},true);
        int frames=0;while(_busy&&frames++<8000)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Check(!_busy&&CurrentBuildings?.OrdinaryCount>0&&_buildingPreview.Visible,$"run input and preview: {_buildingNode.State} {_buildingState.Text}");Check(ReferenceEquals(parcels,CurrentParcels)&&upstream==_parcelNode.ResultRevision,"upstream reused");
        long revision=_buildingNode.ResultRevision;
        for(int layer=1;layer<=4;layer++){
            _buildingLayer.Select(layer);_buildingLayer.EmitSignal(OptionButton.SignalName.ItemSelected,(long)layer);
            Check(_buildingPreview.Layer==layer,"four layer callbacks");
            if(layer!=3){var body=_buildingPreview.GetNode<MeshInstance3D>("BuildingMasses");Check(body.MaterialOverride is StandardMaterial3D{NoDepthTest:false},"opaque depth-tested massing");}
        }
        _buildingLayer.Select(1);ShowBuildings();_cityMode.Select(0);_cityMode.EmitSignal(OptionButton.SignalName.ItemSelected,0L);FocusBuildings("core");
        var target=CurrentBuildings!.Buildings.Where(b=>b.ParcelId.HasValue&&b.InsideCore).OrderBy(b=>b.Footprint.Center.DistanceSquaredTo(CurrentSettlement!.Core)).First();
        var mesh=_buildingPreview.MeshData!;int face=Enumerable.Range(0,mesh.Positions.Count/3).Select(i=>i*3).First(i=>mesh.Owners[i]==target.Id&&mesh.Parts[i]=="roof");
        var center=(mesh.Positions[face]+mesh.Positions[face+1]+mesh.Positions[face+2])/3;var screen=_camera.UnprojectPosition(center);
        GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,ButtonIndex=MouseButton.Left,Pressed=true},true);GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,ButtonIndex=MouseButton.Left,Pressed=false},true);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Check(_selectedBuilding==target.Id&&_buildingNode.ResultRevision==revision,"actual roof picking without regeneration");
        _buildingHeight.Value=1.1;Check(CurrentBuildings==null&&_buildingState.Text.Contains("旧结果"),"parameter invalidation");await RunBuildingsAsync();Check(CurrentBuildings!=null&&_parcelNode.ResultRevision==upstream,"local rerun");
        var task=RunBuildingsAsync();_buildingCancel.EmitSignal(Godot.Button.SignalName.Pressed);await task;Check(_buildingNode.State==PcgCitySandbox.Pcg.SampleNodeState.Cancelled&&CurrentBuildings==null,"cancel");
        _buildingHeight.Value=1;_buildingParty.ButtonPressed=false;_buildingInstitutions.ButtonPressed=false;await RunBuildingsAsync();Check(CurrentBuildings!=null&&CurrentBuildings.SharedWalls.Count==0&&CurrentBuildings.Buildings.All(b=>b.ParcelId.HasValue),"optional controls");
        _buildingParty.ButtonPressed=true;_buildingInstitutions.ButtonPressed=true;await RunBuildingsAsync();
        _parcelWidth.Value=16;Check(CurrentBuildings==null,"parcel invalidation");_parcelWidth.Value=8;await RunParcelsAsync();await RunBuildingsAsync();Check(CurrentBuildings!=null,"parcel chain");
        _roadSpacing.Value=90;Check(CurrentBuildings==null,"road invalidation");_roadSpacing.Value=80;await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();Check(CurrentBuildings!=null,"road chain");
        _layoutRadius.Value=320;Check(CurrentBuildings==null,"layout invalidation");_layoutRadius.Value=300;await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();Check(CurrentBuildings!=null,"layout chain");
        _sampleSpacing.Value=20;Check(CurrentBuildings==null,"sampling invalidation");_sampleSpacing.Value=10;await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();Check(CurrentBuildings!=null,"sampling chain");
        Generate();Check(CurrentBuildings==null&&_buildingState.Text.Contains("旧结果"),"map rebuild invalidation");await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();Check(CurrentBuildings!=null,"entire map through building workflow");
        await CaptureBuildings();HideBuildings();HideParcels();HideRoads();_layoutPreview.Display(_layoutNode.LastResult,0,false);_townPreview.Visible=false;selector.Select(0);selector.EmitSignal(OptionButton.SignalName.ItemSelected,0L);_camera.ResetView();_trees.SetPressedNoSignal(true);_map.ShowForest(true);
        GD.Print("BUILDING_UI_PASS layers=4 pick=ok local_rerun=ok cancel=ok options=ok upstream_stale=ok full_chain=ok");
    }
}
