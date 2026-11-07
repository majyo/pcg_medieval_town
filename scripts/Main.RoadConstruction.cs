using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;

namespace PcgCitySandbox;
public partial class Main
{
    private RoadConstructionSettings _constructionSettings=null!;
    private Button _constructionExport=null!;
    private readonly System.Collections.Generic.List<BaseButton> _constructionControls=new();
    private void SetConstructionBusy(bool busy){foreach(var c in _constructionControls)c.Disabled=busy;if(_constructionExport!=null)_constructionExport.Disabled=busy;}
    private void BuildConstructionInterface(VBoxContainer col)
    {
        _constructionSettings=GD.Load<RoadConstructionSettings>("res://configs/road_construction.tres");
        col.AddChild(Text("正式路面与导出",18,Ink));
        var detail=new OptionButton();foreach(string text in new[]{"路面精度 0.25 m","路面精度 0.5 m","路面精度 1 m"})detail.AddItem(text);detail.Select(1);col.AddChild(detail);
        detail.ItemSelected+=i=>{if(_busy)return;_constructionSettings.NearSpacing=new[]{.25f,.5f,1}[i];_cityPresentation.RebuildRoads(_constructionSettings);};
        var collision=new CheckButton{Text="装配路面与地形碰撞",ButtonPressed=_constructionSettings.Collision};col.AddChild(collision);collision.Toggled+=on=>{if(_busy)return;_constructionSettings.Collision=on;_cityPresentation.RebuildRoads(_constructionSettings);};
        var navigation=new CheckButton{Text="导出道路导航与入口连接",ButtonPressed=_constructionSettings.Navigation};col.AddChild(navigation);navigation.Toggled+=on=>{if(!_busy)_constructionSettings.Navigation=on;};
        _constructionControls.AddRange(new BaseButton[]{detail,collision,navigation});
        _constructionExport=Button("导出当前城镇");col.AddChild(_constructionExport);
        _constructionExport.Pressed+=async()=>{
            if(_busy)return;if(CurrentPlacement==null||CurrentGrading==null){_placementState.Text="请先生成有效的 03B–06 结果";return;}
            _busy=true;SetSamplingBusy(true);SetLayoutBusy(true);SetRoadBusy(true);SetParcelBusy(true);SetBuildingBusy(true);_constructionExport.Disabled=true;
            try{_placementState.Text="正在装配与导出…";await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ExportCurrentTown();_placementState.Text="导出完成 · 路径见 artifacts/town_export_validation.json";}
            catch(Exception ex){_placementState.Text="导出失败："+ex.Message;GD.PrintErr(ex);}
            finally{_busy=false;SetSamplingBusy(false);SetLayoutBusy(false);SetRoadBusy(false);SetParcelBusy(false);SetBuildingBusy(false);_constructionExport.Disabled=false;}
        };
    }
    private async Task ExportTown()
    {
        await RunSamplingAsync();await RunSettlementAsync();await RunRoadsAsync();await RunGradingAsync();await RunParcelsAsync();await RunBuildingsAsync();await RunPlacementAsync();
        await ExportCurrentTown();
    }
    private async Task ExportCurrentTown()
    {
        if(DisplayServer.GetName()=="headless")throw new InvalidOperationException("导出程序纹理需要图形渲染器，请使用 tools/project.ps1 export-town");
        if(CurrentGrading is not {} grading||CurrentPlacement is not {} placement)throw new InvalidOperationException("导出需要有效的 01–06 生成链");
        var versions=new TownRevisions(_sampleNode.ResultRevision,_layoutNode.ResultRevision,_roadNode.ResultRevision,_gradingNode.ResultRevision,_parcelNode.ResultRevision,_buildingNode.ResultRevision,_placementNode.ResultRevision);
        var settings=GD.Load<RoadConstructionSettings>("res://configs/road_construction.tres");
        var root=TownSceneAssembler.Build(_data,grading,placement,versions,settings);string path=TownSceneAssembler.Export(root,_data.Settings.Seed,versions,settings.Snapshot());root.Free();
        await VerifyExportedTown(path);
    }
    private async Task VerifyExportedTown(string path)
    {
        var scene=ResourceLoader.Load<PackedScene>(path,cacheMode:ResourceLoader.CacheMode.IgnoreDeep);var town=scene.Instantiate<Node3D>();AddChild(town);
        var cameraState=(_camera.Focus,_camera.Size,_camera.Pitch,_camera.Yaw);bool mapVisible=_map.Visible,cityVisible=_cityPresentation.Visible,previewsVisible=_legacyPreviews.Visible;var ui=GetNode<CanvasLayer>("Interface");bool uiVisible=ui.Visible;
        try{
        var graph=town.GetMeta("road_graph").As<RoadRuntimeGraph>();var world=town.GetWorld3D().NavigationMap;
        int core=Array.FindIndex(graph.AnchorIds,id=>id.Contains("core"));if(core<0)throw new InvalidOperationException("导出道路图缺少核心锚点");var goal=graph.AnchorPositions[core];int routes=0,doorRoutes=0,collisionProbes=0;
        var regions=town.GetNodeOrNull<Node3D>("Navigation");
        if(regions!=null){var road=regions.GetNode<NavigationRegion3D>("RoadNetwork");
            for(int frame=0;frame<600;frame++){
                await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);
                if(regions.GetChildren().OfType<NavigationRegion3D>().Any(r=>NavigationServer3D.RegionGetIterationId(r.GetRid())==0))continue;
                routes=0;foreach(var start in graph.AnchorPositions){if(start.DistanceTo(goal)<1)continue;var route=TownNavigation.FindPath(world,start,goal);if(route.Length>1&&route[^1].DistanceTo(goal)<.5f)routes++;}
                if(routes==graph.AnchorPositions.Count(p=>p.DistanceTo(goal)>=1))break;
                if(frame%60==0)GD.Print($"EXPORT_NAV_WAIT frame={frame} routes={routes} map={NavigationServer3D.MapGetIterationId(world)} region={NavigationServer3D.RegionGetIterationId(road.GetRid())} faces={road.NavigationMesh.GetPolygonCount()}");
            }
            foreach(var pair in graph.AnchorIds.Zip(graph.AnchorPositions)){var start=pair.Second;if(start.DistanceTo(goal)<1)continue;var route=TownNavigation.FindPath(world,start,goal);GD.Print($"EXPORT_ROUTE {pair.First} points={route.Length} distance={(route.Length>0?route[^1].DistanceTo(goal):-1):0.000}");RoadConstructionValidation.Check(route.Length>1&&route[^1].DistanceTo(goal)<.5f,"reloaded exported scene path to core: "+pair.First);}
            if(regions.HasMeta("door_positions"))foreach(var door in regions.GetMeta("door_positions").AsVector3Array()){
                var route=TownNavigation.FindPath(world,goal,door);RoadConstructionValidation.Check(route.Length>1&&route[^1].DistanceTo(door)<.05f,$"exported door access at {door}");doorRoutes++;if(doorRoutes%32==0)await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);}
        }
        if(town.HasMeta("collision_enabled")&&town.GetMeta("collision_enabled").AsBool()){
            await ToSignal(GetTree(),SceneTree.SignalName.PhysicsFrame);
            for(int edge=0;edge<graph.RoadIds.Length;edge+=3){int at=(graph.Offsets[edge]+graph.Offsets[edge+1]-1)/2;var point=graph.Points[at];using var ray=PhysicsRayQueryParameters3D.Create(point+Vector3.Up*2,point-Vector3.Up*2);ray.HitBackFaces=false;
                var hit=town.GetWorld3D().DirectSpaceState.IntersectRay(ray);RoadConstructionValidation.Check(hit.Count>0&&Math.Abs(hit["position"].AsVector3().Y-point.Y)<.01f,$"exported road collision R{graph.RoadIds[edge]}");collisionProbes++;}
        }
        if(DisplayServer.GetName()!="headless"){
            _map.Visible=false;_cityPresentation.Visible=false;_legacyPreviews.Visible=false;ui.Visible=false;
            _camera.Focus=goal+Vector3.Up*2;_camera.Size=130;_camera.Pitch=.55f;_camera.Yaw=-.6f;_camera.UpdateTransform();
            for(int frame=0;frame<4;frame++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            string folder=ProjectSettings.GlobalizePath("res://artifacts/road_construction");Directory.CreateDirectory(folder);using var capture=GetViewport().GetTexture().GetImage();RoadConstructionValidation.Check(capture.SavePng(Path.Combine(folder,"export_market.png"))==Error.Ok,"exported scene capture");}
        using var revisionJson=System.Text.Json.JsonDocument.Parse(town.GetMeta("revisions").AsString());
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/town_export_validation.json"),System.Text.Json.JsonSerializer.Serialize(new{passed=true,path,preview=path.Replace("town.tscn","preview.tscn"),routes,doorRoutes,collisionProbes,terrain=graph.TerrainFingerprint,revisions=revisionJson.RootElement.Clone()}));
        GD.Print($"TOWN_EXPORT_PASS scene={path} routes={routes}");
        }finally{RemoveChild(town);town.QueueFree();_map.Visible=mapVisible;_cityPresentation.Visible=cityVisible;_legacyPreviews.Visible=previewsVisible;ui.Visible=uiVisible;_camera.Focus=cameraState.Focus;_camera.Size=cameraState.Size;_camera.Pitch=cameraState.Pitch;_camera.Yaw=cameraState.Yaw;_camera.UpdateTransform();}

    }
}
