using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class Main
{
    private async Task VerifyPlacementInterface(){
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException("PLACEMENT_UI_FAILED: "+message);}
        await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();
        var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(PlacementStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)PlacementStage);Check(_placementPanel.Visible&&!_buildingPanel.Visible,"node selection");Check(PlacementSettings()==new ScenePlacementOptions(),"default settings");
        for(int i=0;i<2;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        var buildings=CurrentBuildings;long upstream=_buildingNode.ResultRevision;var trees=_data.Trees.ToArray();var pos=_placementRun.GetGlobalRect().GetCenter();
        GetViewport().PushInput(new InputEventMouseMotion{Position=pos,GlobalPosition=pos},true);GetViewport().PushInput(new InputEventMouseButton{Position=pos,GlobalPosition=pos,Pressed=true,ButtonIndex=MouseButton.Left},true);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);GetViewport().PushInput(new InputEventMouseButton{Position=pos,GlobalPosition=pos,Pressed=false,ButtonIndex=MouseButton.Left},true);
        int frames=0;while(_busy&&frames++<12000)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Check(CurrentPlacement!=null&&_placementPreview.Visible,"independent scene generation: "+_placementState.Text);Check(ReferenceEquals(buildings,CurrentBuildings)&&upstream==_buildingNode.ResultRevision,"upstream reused");
        Check(_map.FilteredTreeCount>0&&trees.SequenceEqual(_data.Trees),"filtered render buffer and untouched source trees");
        long revision=_placementNode.ResultRevision;
        for(int layer=1;layer<=4;layer++){_placementLayer.Select(layer);_placementLayer.EmitSignal(OptionButton.SignalName.ItemSelected,(long)layer);Check(_placementPreview.Layer==layer&&_placementPreview.GetChildCount()==1,"four layers with one generated root");if(layer!=3)Check(_placementPreview.GetNode<MeshInstance3D>("GeneratedTown/Props").MaterialOverride is StandardMaterial3D{NoDepthTest:false},"solid props depth tested");}
        _placementLayer.Select(1);ShowPlacement();_cityMode.Select(0);_cityMode.EmitSignal(OptionButton.SignalName.ItemSelected,0L);FocusPlacement("market");var target=CurrentPlacement!.Props.First(p=>p.ZoneId=="market");
        var mesh=_placementPreview.Cache!.Props;int face=Enumerable.Range(0,mesh.Positions.Count/3).Select(i=>i*3).First(i=>mesh.Owners[i]==target.Id&&mesh.Normals[i].Y>.7f);var center=(mesh.Positions[face]+mesh.Positions[face+1]+mesh.Positions[face+2])/3;var screen=_camera.UnprojectPosition(center);
        GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,Pressed=true,ButtonIndex=MouseButton.Left},true);GetViewport().PushInput(new InputEventMouseButton{Position=screen,GlobalPosition=screen,Pressed=false,ButtonIndex=MouseButton.Left},true);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);Check(_selectedPlacement==target.Id&&_placementNode.ResultRevision==revision,"actual object picking without regeneration");
        _placementYards.Value=.7;Check(CurrentPlacement==null&&_placementState.Text.Contains("旧"),"parameter invalidation");await RunPlacementAsync();Check(CurrentPlacement!=null&&_buildingNode.ResultRevision==upstream,"local rerun");
        var task=RunPlacementAsync();Check(_buildingRun.Disabled&&_roadRun.Disabled,"upstream controls locked");_placementCancel.EmitSignal(Godot.Button.SignalName.Pressed);await task;Check(_placementNode.State==SampleNodeState.Cancelled&&CurrentPlacement==null,"cancel");
        _placementFences.ButtonPressed=false;_placementVegetation.ButtonPressed=false;_placementScheme.Select(1);_placementScheme.EmitSignal(OptionButton.SignalName.ItemSelected,1L);await RunPlacementAsync();Check(CurrentPlacement!=null&&CurrentPlacement.Props.All(p=>p.Kind!=ScenePropKind.Fence)&&_map.FilteredTreeCount==0,"resource and optional controls");
        _placementYards.Value=.6;_placementFences.ButtonPressed=true;_placementVegetation.ButtonPressed=true;_placementScheme.Select(0);_placementScheme.EmitSignal(OptionButton.SignalName.ItemSelected,0L);
        _buildingHeight.Value=1.1;Check(CurrentPlacement==null,"building invalidation");_buildingHeight.Value=1;await RunBuildingsAsync();await RunPlacementAsync();Check(CurrentPlacement!=null,"building-to-scene chain");
        _roadCorner.Value=3.5;Check(CurrentPlacement==null,"road invalidation reaches scene");_roadCorner.Value=3;await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();await RunPlacementAsync();Check(CurrentPlacement!=null,"road to scene full chain");
        var current=CurrentPlacement;HidePlacement();Check(_map.FilteredTreeCount==0,"hiding scene restores natural vegetation");_placementLayer.Select(1);ShowPlacement();Check(_map.FilteredTreeCount>0&&ReferenceEquals(current,CurrentPlacement),"show restores mask without generation");
        await CapturePlacementViews();
        Check(trees.SequenceEqual(_data.Trees),"source tree data unchanged through reruns");Generate();Check(CurrentPlacement==null&&!_placementPreview.Visible,"map replacement invalidates and hides old scene");
        GD.Print("PLACEMENT_UI_PASS layers=4 picking=ok cancel=ok rerun=ok vegetation_restore=ok upstream_stale=ok full_chain=ok captures=6");
    }
    private async Task CapturePlacementViews(){
        void Check(bool ok,string message){if(!ok)throw new IOException(message);}
        _selectedPlacement=null;var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(PlacementStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)PlacementStage);string dir=ProjectSettings.GlobalizePath("res://artifacts/captures");Directory.CreateDirectory(dir);
        async Task Save(string name){for(int i=0;i<5;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);using var image=GetViewport().GetTexture().GetImage();Check(image.SavePng(Path.Combine(dir,name))==Error.Ok,"screenshot");}
        _placementLayer.Select(1);ShowPlacement();FocusPlacement("town");await Save("placement_overview.png");FocusPlacement("market");await Save("placement_market.png");FocusPlacement("yard");await Save("placement_yard.png");
        _placementLayer.Select(2);ShowPlacement();await Save("placement_sources.png");var focus=_camera.Focus;_placementLayer.Select(3);ShowPlacement();_camera.TopView();_camera.Focus=focus;_camera.Size=75;_camera.UpdateTransform();await Save("placement_access.png");_placementLayer.Select(4);ShowPlacement();await Save("placement_issues.png");
        GD.Print("PLACEMENT_CAPTURE_PASS views=6");
    }
}
