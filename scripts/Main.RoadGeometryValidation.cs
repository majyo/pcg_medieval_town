using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox;
public partial class Main
{
    private async Task VerifyRoadGeometryInterface(){
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException("ROAD_GEOMETRY_UI_FAILED: "+message);}
        await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();
        Check(CurrentRoads!=null&&CurrentParcels!=null&&CurrentBuildings?.OrdinaryCount>0,"full geometry to buildings chain");
        var env=SampledEnvironment;var layout=CurrentSettlement;var old=CurrentRoads!;long revision=_roadNode.ResultRevision;
        var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(2);selector.EmitSignal(OptionButton.SignalName.ItemSelected,2L);
        for(int layer=1;layer<=4;layer++){
            _roadLayer.Select(layer);_roadLayer.EmitSignal(OptionButton.SignalName.ItemSelected,(long)layer);Check(_roadPreview.Layer==layer,"four layers");
            if(layer!=2){var surface=_roadPreview.GetNode<MeshInstance3D>("FinalRoadSurface");Check(surface.MaterialOverride is StandardMaterial3D{Transparency:BaseMaterial3D.TransparencyEnum.Disabled},"opaque unified surface");}
        }
        _roadCorner.Value=6;Check(CurrentRoads==null&&CurrentParcels==null&&CurrentBuildings==null,"corner setting invalidates all downstream nodes");
        await RunRoadsAsync();Check(CurrentRoads!=null&&ReferenceEquals(env,SampledEnvironment)&&ReferenceEquals(layout,CurrentSettlement),"local geometry rerun reuses upstream");
        Check(CurrentRoads!.Anchors.SequenceEqual(old.Anchors)&&CurrentRoads.Edges.Select(e=>(e.Id,e.From,e.To)).SequenceEqual(old.Edges.Select(e=>(e.Id,e.From,e.To))),"geometric settings preserve graph topology");
        _roadFlare.Value=3;Check(CurrentRoads==null,"junction setting invalidates road output");_roadFlare.Value=2;_roadCorner.Value=3;
        await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();Check(CurrentRoads!=null&&CurrentParcels!=null&&CurrentBuildings!=null&&_roadNode.ResultRevision>revision,"downstream rebuilt against shaped geometry");
        Check(CurrentParcels!.Parcels.All(p=>p.Frontage.All(f=>CurrentRoads!.Geometry.FrontageAt(f.A,f.B)?.RoadId==f.RoadId)),"all plot frontages inherit final boundary sources");
        string dir=ProjectSettings.GlobalizePath("res://artifacts/captures");Directory.CreateDirectory(dir);
        async Task Save(string name){for(int i=0;i<4;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);using var frame=GetViewport().GetTexture().GetImage();Check(frame.SavePng(Path.Combine(dir,name))==Error.Ok,"capture "+name);}
        _roadLayer.Select(1);ShowRoads();FocusSettlement();await Save("roads_final_overview.png");
        FocusRoadJunction();await Save("roads_final_junction.png");
        _roadLayer.Select(3);ShowRoads();await Save("roads_final_boundaries.png");
        var curve=CurrentRoads!.Edges.FirstOrDefault(e=>e.Points.Count>CurrentRoads.Geometry.PlannedEdges.First(p=>p.Id==e.Id).Points.Count);
        if(curve!=null){var before=CurrentRoads.Geometry.PlannedEdges.First(p=>p.Id==curve.Id);var point=before.Points[1];CurrentRoads.Environment.TryHeightAt(point,out float h);_camera.Focus=new(point.X,h,point.Y);_camera.Size=55;_camera.UpdateTransform();_roadLayer.Select(1);ShowRoads();await Save("roads_final_bend.png");}
        selector.Select(BuildingStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)BuildingStage);_buildingLayer.Select(1);ShowBuildings();FocusBuildings("core");await Save("roads_final_buildings.png");
        GD.Print($"ROAD_GEOMETRY_UI_PASS layers=4 geometry_params=ok invalidation=ok frontage=ok full_chain=ok parcels={CurrentParcels!.Parcels.Count} buildings={CurrentBuildings!.Buildings.Count}");
    }
}
