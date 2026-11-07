using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class Main
{
    private async Task VerifyGradingInterface()
    {
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException("ROAD_GRADING_UI_FAILED: "+message);}
        async Task Frames(int n=4){for(int i=0;i<n;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
        await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();
        var roads=CurrentRoads;var parcels=CurrentParcels;Check(roads!=null&&parcels!=null,"chain to parcels");
        var selector=(OptionButton)FindChild("PcgNodeSelector",true,false);selector.Select(GradingStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)GradingStage);await Frames();
        Check(_gradingPanel.Visible&&!_roadPanel.Visible&&!_cityPresentation.Visible&&selector.GetItemText(GradingStage).StartsWith("03B"),"03B panel replaces city presentation");
        await RunGradingAsync();var first=CurrentGrading;Check(first!=null&&ReferenceEquals(first.Roads,roads),"grading runs on current roads");
        Check(CurrentParcels==null&&ReferenceEquals(CurrentRoads,roads),"grading rerun invalidates downstream and reuses roads");
        for(int layer=1;layer<=4;layer++){_gradingLayer.Select(layer);_gradingLayer.EmitSignal(OptionButton.SignalName.ItemSelected,(long)layer);await Frames(2);
            Check(_gradingPreview.Layer==layer&&_gradingPreview.Visible&&_gradingPreview.GetChildCount()>0,$"layer {layer} drawn");
            if(layer==1)Check(_gradingPreview.GetNodeOrNull<MeshInstance3D>("DesignSurface")!=null,"design surface mesh");
            if(layer==2)Check(_map.Graded!=null&&_map.GradedHeat,"cut/fill drawn on the graded terrain");}
        Check(_gradingPreview.MeshData!=null&&_gradingPreview.MeshData.Positions.Count>0,"preview tessellation");
        // Graded terrain: shown only on this page, removable for comparison, crack-free.
        _gradingLayer.Select(5);ShowGrading();await Frames(2);var graded=first!.Graded!;
        Check(ReferenceEquals(_map.Graded,graded)&&_map.GradedMesh!=null&&_map.RefinedCellCount>0&&!_map.GradedHeat,"graded terrain layer");
        var bed=_map.VerifyRoadOcclusion();Check(bed.Samples>0&&bed.Intrusions==0,$"terrain stays below the road ({bed.Intrusions}/{bed.Samples}, worst {bed.Worst*1000:0.0} mm)");
        var boundary=_map.VerifyRefinedBoundary();Check(boundary.Edges>0&&boundary.Mismatches==0,$"refined cells meet the coarse terrain without gaps ({boundary.Mismatches}/{boundary.Edges}, worst {boundary.Worst*1000:0.00} mm)");
        _gradingApply.ButtonPressed=false;await Frames(2);Check(_map.Graded==null&&_map.GradedMesh==null,"toggle restores the original terrain");
        _gradingApply.ButtonPressed=true;await Frames(2);Check(ReferenceEquals(_map.Graded,graded),"toggle reapplies the graded terrain");
        selector.Select(0);selector.EmitSignal(OptionButton.SignalName.ItemSelected,0L);await Frames(2);Check(_map.Graded==null,"01 still previews the original environment");
        selector.Select(GradingStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)GradingStage);await Frames(2);Check(ReferenceEquals(_map.Graded,graded),"returning to 03B shows the graded terrain");
        long refined=_map.RefinedCellCount,triangles=_map.GradedTriangleCount,trees=_map.GradedTreeCount;
        // Viewport click on a road selects its profile.
        _gradingLayer.Select(1);ShowGrading();var target=roads!.Edges.Where(e=>e.Main).OrderByDescending(e=>e.Length).First();var line=new Centerline(target);var mid=line.PointAt(line.Length/2);
        first!.TryDesignHeight(mid,out float h);_camera.TopView();_camera.Focus=new(mid.X,h,mid.Y);_camera.Size=120;_camera.UpdateTransform();await Frames();
        var screen=_camera.UnprojectPosition(new Vector3(mid.X,h,mid.Y));_gradingSelectedRoad=-1;
        GetViewport().PushInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=screen,GlobalPosition=screen});await Frames();
        GetViewport().PushInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=screen,GlobalPosition=screen});await Frames();
        Check(_gradingSelectedRoad==first.SurfaceAt(mid)?.RoadId,$"viewport click selects road profile (got R{_gradingSelectedRoad})");
        // Local rerun and invalidation.
        _gradingSmoothing.Value=30;Check(CurrentGrading==null&&CurrentRoads!=null&&CurrentParcels==null,"grading parameter invalidates downstream");
        await RunGradingAsync();Check(CurrentGrading!=null&&!ReferenceEquals(CurrentGrading,first)&&ReferenceEquals(CurrentGrading.Roads,roads),"local rerun reuses roads");
        _gradingSmoothing.Value=20;await RunGradingAsync();
        _roadCorner.Value=4;Check(CurrentGrading==null&&_gradingNode.State==SampleNodeState.Stale,"road parameter invalidates grading");
        _roadCorner.Value=3;await RunRoadsAsync();Check(CurrentRoads!=null&&CurrentGrading==null,"new roads leave old grading stale");
        await RunGradingAsync();Check(CurrentGrading!=null,"grading rebuilt against new roads");
        await RunParcelsAsync();await RunBuildingsAsync();await RunPlacementAsync();Check(CurrentPlacement!=null,"graded downstream chain");
        selector.Select(PlacementStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)PlacementStage);UpdateCityPresentation(true);await Frames(2);
        Check(ReferenceEquals(_map.Graded,CurrentGrading!.Graded)&&ReferenceEquals(CurrentPlacement!.Terrain,_map.Graded),"06 displays the same graded terrain");
        Check(_cityPresentation.Roads?.Triangles>0,"formal surface assembled");
        selector.Select(GradingStage);selector.EmitSignal(OptionButton.SignalName.ItemSelected,(long)GradingStage);
        // Captures.
        string dir=ProjectSettings.GlobalizePath("res://artifacts/captures");Directory.CreateDirectory(dir);
        async Task Save(string name){await Frames();await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);using var frame=GetViewport().GetTexture().GetImage();Check(frame.SavePng(Path.Combine(dir,name))==Error.Ok,"capture "+name);}
        _gradingLayer.Select(2);ShowGrading();FocusSettlement();await Save("grading_cutfill.png");
        FocusGrading(true);_gradingLayer.Select(1);ShowGrading();await Save("grading_surface_cut.png");
        _gradingLayer.Select(2);ShowGrading();await Save("grading_cutfill_cut.png");
        FocusGrading(false);_gradingLayer.Select(1);ShowGrading();await Save("grading_surface_fill.png");
        var cutRoad=CurrentGrading!.SurfaceAt(CurrentGrading.Summary.MaxCutAt)?.RoadId??target.Id;_gradingSelectedRoad=cutRoad;_gradingLayer.Select(3);ShowGrading();FocusGradingAt(CurrentGrading,CurrentGrading.Summary.MaxCutAt,80);var scroll=_gradingPanel.GetChild<ScrollContainer>(0);scroll.ScrollVertical=(int)_gradingChart.Position.Y-260;await Save("grading_profile.png");scroll.ScrollVertical=0;
        _gradingLayer.Select(4);ShowGrading();FocusSettlement();await Save("grading_issues.png");
        _gradingLayer.Select(5);ShowGrading();FocusGradingAt(CurrentGrading,CurrentGrading.Summary.MaxCutAt,70);await Save("grading_terrain_cut.png");
        _gradingApply.ButtonPressed=false;await Save("grading_terrain_cut_before.png");_gradingApply.ButtonPressed=true;
        _gradingLayer.Select(2);ShowGrading();await Save("grading_terrain_heat.png");
        _gradingLayer.Select(5);ShowGrading();FocusGradingAt(CurrentGrading,CurrentGrading.Summary.MaxFillAt,70);await Save("grading_terrain_fill.png");
        var core=CurrentGrading.PlazaRect.GetCenter();_camera.Yaw=-.6f;_camera.Pitch=.6f;_camera.Focus=new(core.X,CurrentGrading.GroundHeight(core),core.Y);_camera.Size=260;_camera.UpdateTransform();await Save("grading_terrain_town.png");
        var g=CurrentGrading!;GD.Print($"ROAD_GRADING_UI_PASS layers=5 click=ok invalidation=ok graded_terrain=ok boundary_edges={boundary.Edges} refined_cells={refined} triangles={triangles} removed_trees={trees} captures=11 profiles={g.Profiles.Count} cut={g.Summary.CutVolume:0} fill={g.Summary.FillVolume:0}");
    }
}
