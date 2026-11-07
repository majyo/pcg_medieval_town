using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PcgCitySandbox.Pcg;

public static class RoadConstructionValidation
{
    public static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException("ROAD_CONSTRUCTION_FAILED: "+message);}
    public static async Task NavigationOnly(MapSettings settings,bool bed=false){
        var map=new TemperateMapData(settings);var source=new TemperateEnvironmentSource(map,"navigation",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());var env=EnvironmentSampler.Sample(source,new(source.Bounds));var layout=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));var roads=await new RoadNode().RunAsync(env,layout,1,1,new());var grading=await new RoadGradingNode().RunAsync(roads,1,new NativeTerrainSurface(map),new());
        var area=new ParcelRegion(ParcelGeometry.Union(new Clipper2Lib.Paths64(roads.Geometry.Area.CopyPaths().Concat(ParcelGeometry.Rect(layout.Plaza)))));
        if(bed){var renderer=new MapRenderer();renderer.Build(map);renderer.SetGradedTerrain(grading.Graded);var report=renderer.VerifyRoadOcclusion();GD.Print($"ROAD_BED samples={report.Samples} intrusions={report.Intrusions} worst_mm={report.Worst*1000}");Check(report.Samples>0&&report.Intrusions==0,"road terrain occlusion");renderer.Free();return;}
        int routes=await VerifyNavigation(new(grading,new(),new(),new(),area));GD.Print($"ROAD_NAVIGATION_PASS routes={routes}");
    }
    public static async Task Run(MapSettings original)
    {
        var reports=new List<object>();
        foreach(int seed in new[]{73129,7,22017}){
            var timer=Stopwatch.StartNew();var settings=(MapSettings)original.Duplicate();settings.Seed=seed;var map=new TemperateMapData(settings);
            var source=new TemperateEnvironmentSource(map,"construction",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());
            var env=EnvironmentSampler.Sample(source,new(source.Bounds));var layout=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));
            var roads=await new RoadNode().RunAsync(env,layout,1,1,new());var native=new NativeTerrainSurface(map);var grading=await new RoadGradingNode().RunAsync(roads,1,native,new());
            GD.Print($"CONSTRUCTION_STAGE seed={seed} grading_ms={timer.ElapsedMilliseconds}");
            var parcelNode=new ParcelNode();var parcels=await parcelNode.RunAsync(grading,1,new());var buildings=await new BuildingNode().RunAsync(env,parcels,1,1,new());
            var placement=await new ScenePlacementNode().RunAsync(env,roads,parcels,buildings,1,1,1,1,new());
            Check(ReferenceEquals(parcels.Terrain,grading.Graded)&&ReferenceEquals(buildings.Terrain,placement.Terrain),"one graded terrain through 04–06");
            Check(buildings.InsideCount>20&&buildings.OutsideCount>buildings.InsideCount,"inhabited core and suburbs");
            Check(Math.Abs(parcels.AvailableArea-parcels.ParcelArea-parcels.RemainderArea)<2,"parcel area conservation");
            foreach(var b in buildings.Buildings){
                Check(BuildingGeometry.Ground(grading.Graded!,b.Corners.ToArray(),buildings.Options.MaxFoundation,out float lo,out float hi),"foundation valid on graded terrain");
                Check(Math.Abs(lo-b.MinGround)<.001f&&Math.Abs(hi-b.MaxGround)<.001f&&b.BaseY>=hi,"foundation references graded heights");
                if(b.ParcelId is int id)Check(BuildingGeometry.Fits(b.Footprint.CopyPaths(),parcels.Parcels.First(p=>p.Id==id).Buildable.CopyPaths()),"building in its parcel");
            }
            foreach(var p in placement.Props){Check(BuildingGeometry.Ground(grading.Graded!,p.Corners.ToArray(),ScenePlacementGenerator.MaxGroundDifference+.06f,out float lo,out float hi),"prop support on graded terrain");Check(Math.Abs(lo-p.MinGround)<.001f&&Math.Abs(hi-p.MaxGround)<.001f,"prop sampled heights");}
            Check(!parcelNode.IsCurrent(grading,2,new()),"03B revision invalidates parcels");
            var invalid=await new RoadGradingNode().RunAsync(roads,1,native,new(SmoothingScale:30));Check(invalid.Graded!.Fingerprint!=grading.Graded!.Fingerprint&&!parcelNode.IsCurrent(invalid,1,new()),"design change changes fingerprint and dependency");
            GD.Print($"CONSTRUCTION_STAGE seed={seed} parcels={parcels.Parcels.Count} buildings={buildings.Buildings.Count} props={placement.Props.Count} downstream_ms={timer.ElapsedMilliseconds}");
            var options=new RoadConstructionOptions();var surfaces=RoadSceneBuilder.Build(grading,options);var geometry=VerifyMesh(surfaces);
            var versions=new TownRevisions(1,1,1,1,1,1,1);TownSceneAssembler.Validate(grading,placement,versions);
            bool mixed=false;try{TownSceneAssembler.Validate(invalid,placement,versions);}catch(ArgumentException){mixed=true;}Check(mixed,"assembly rejects mixed grading chains");
            bool stale=false;try{TownSceneAssembler.Validate(grading,placement,versions with{Grading=2});}catch(ArgumentException){stale=true;}Check(stale,"assembly rejects stale revisions");
            var visualChange=RoadSceneBuilder.Build(grading,options with{Collision=false,Navigation=false});Check(ReferenceEquals(surfaces.Near[0],visualChange.Near[0])&&parcelNode.IsCurrent(grading,1,new()),"assembly options reuse geometry without invalidating planning");
            using(var cancel=new System.Threading.CancellationTokenSource()){cancel.Cancel();bool stopped=false;try{RoadSceneBuilder.Build(grading,options,cancel.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"cancelled assembly does not return a cached result");}
            GD.Print($"CONSTRUCTION_STAGE seed={seed} surface_triangles={surfaces.Triangles} mesh_ms={timer.ElapsedMilliseconds}");
            int routes=await VerifyNavigation(surfaces);
            var graph=RoadRuntimeGraph.Build(grading,options);Check(graph.RoadIds.Length==roads.Edges.Count&&graph.Points.All(p=>p.IsFinite()),"runtime graph complete");
            var locked=grading.Graded.Nodes().Where(n=>n.Kind==GradingCell.Lock).ToArray();Check(locked.All(n=>n.Delta==0),"locked nodes remain unchanged");
            // Compare the same doorway positions against native and finished street elevations.
            var oldParcels=await ParcelGenerator.Generate(roads,1,new(),new());var oldBuildings=await BuildingGenerator.Generate(oldParcels,1,new(),new());
            double before=oldBuildings.Buildings.Where(b=>b.ParcelId.HasValue).Average(b=>Math.Abs(b.BaseY-native.HeightAt(b.StreetPoint)));
            double after=buildings.Buildings.Where(b=>b.ParcelId.HasValue).Average(b=>Math.Abs(b.BaseY-grading.Graded.HeightAt(b.StreetPoint)));
            Check(after<before,"ordinary doorway height difference improves over the ungraded chain");
            reports.Add(new{seed,ungradedBuildings=oldBuildings.Buildings.Count,parcels=parcels.Parcels.Count,buildings=buildings.Buildings.Count,props=placement.Props.Count,geometry,routes,grading.Graded.Earthwork,doorwayNative=before,doorwayGraded=after,elapsedMs=timer.ElapsedMilliseconds});
            GD.Print($"ROAD_CONSTRUCTION_SEED_PASS seed={seed} routes={routes} doorway={before:0.000}->{after:0.000} ms={timer.ElapsedMilliseconds}");
        }
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/road_construction_validation.json"),JsonSerializer.Serialize(new{passed=true,reports},new JsonSerializerOptions{WriteIndented=true}));
        GD.Print("ROAD_CONSTRUCTION_VALIDATION_PASS seeds=3");
    }
    public static object VerifyMesh(RoadSceneData data)
    {
        double area=0;float worst=0;int vertices=0;var g=data.Grading;
        foreach(var chunk in data.Near){Check(chunk.Positions.Count==chunk.Faces.Count*3&&chunk.Uv.Count==chunk.Positions.Count,"mesh source ownership");
            for(int i=0;i<chunk.Positions.Count;i+=3){var a=chunk.Positions[i];var b=chunk.Positions[i+1];var c=chunk.Positions[i+2];area+=Math.Abs((double)(b.X-a.X)*(c.Z-a.Z)-(double)(b.Z-a.Z)*(c.X-a.X))/2;
                foreach(var p in new[]{a,b,c}){Check(p.IsFinite(),"finite mesh");vertices++;}
                foreach(var p in new[]{(a+b)/2,(b+c)/2,(c+a)/2,(a+b+c)/3}){g.TryDesignHeightNear(new(p.X,p.Z),.02f,out float design);worst=Math.Max(worst,Math.Abs(p.Y-design));}
            }
        }
        Check(Math.Abs(area-data.Area.Area)<Math.Max(.1,data.Area.Area*.00001),$"road mesh covers area exactly {area}/{data.Area.Area}");
        Check(worst<.005f,$"road mesh interpolation/seam error {worst*1000:0.00} mm");
        return new{triangles=data.Triangles,chunks=data.Near.Count,vertices,area,expectedArea=data.Area.Area,maxInterpolationError=worst};
    }
    public static async Task<int> VerifyNavigation(RoadSceneData data)
    {
        using var mesh=RoadNavigation.Mesh(data);
        var edgeOwners=new Dictionary<(int,int),List<int>>();var parent=Enumerable.Range(0,mesh.GetPolygonCount()).ToArray();int Root(int a){while(parent[a]!=a){parent[a]=parent[parent[a]];a=parent[a];}return a;}
        for(int i=0;i<parent.Length;i++){var face=mesh.GetPolygon(i);for(int j=0;j<face.Length;j++){int a=face[j],b=face[(j+1)%face.Length];var key=a<b?(a,b):(b,a);if(!edgeOwners.TryGetValue(key,out var owners))edgeOwners[key]=owners=new();owners.Add(i);}}
        foreach(var owners in edgeOwners.Values)for(int j=1;j<owners.Count;j++)parent[Root(owners[j])]=Root(owners[0]);
        GD.Print($"NAV_TOPOLOGY components={parent.Select(Root).Distinct().Count()} nonmanifold={edgeOwners.Values.Count(e=>e.Count>2)} boundary={edgeOwners.Values.Count(e=>e.Count==1)}");
        var map=NavigationServer3D.MapCreate();var region=NavigationServer3D.RegionCreate();
        try{
            NavigationServer3D.MapSetCellSize(map,.01f);NavigationServer3D.MapSetCellHeight(map,.01f);NavigationServer3D.MapSetMergeRasterizerCellScale(map,.001f);NavigationServer3D.MapSetActive(map,true);
            NavigationServer3D.RegionSetMap(region,map);NavigationServer3D.RegionSetNavigationMesh(region,mesh);var tree=(SceneTree)Engine.GetMainLoop();
            for(int frame=0;frame<180&&(NavigationServer3D.RegionGetIterationId(region)==0||frame<8);frame++)await tree.ToSignal(tree,SceneTree.SignalName.PhysicsFrame);
            Check(NavigationServer3D.RegionGetIterationId(region)>0,"navigation map synchronized");
            for(int frame=0;frame<8;frame++)await tree.ToSignal(tree,SceneTree.SignalName.PhysicsFrame);
            GD.Print($"NAV_READY scale={NavigationServer3D.MapGetMergeRasterizerCellScale(map)} cell={NavigationServer3D.MapGetCellSize(map)} vertices={mesh.GetVertices().Length} faces={mesh.GetPolygonCount()} region_iteration={NavigationServer3D.RegionGetIterationId(region)} map_iteration={NavigationServer3D.MapGetIterationId(map)}");
            var core=data.Grading.Roads.Settlement.Core;var target=new Vector3(core.X,data.Grading.Graded!.HeightAt(core),core.Y);int count=0;
            foreach(var anchor in data.Grading.Roads.Anchors){var node=data.Grading.Roads.Nodes.First(n=>n.Id==anchor.Value);var p=node.Position;var start=new Vector3(p.X,data.Grading.Graded.HeightAt(p),p.Y);
                if(start.DistanceTo(target)<1)continue;var path=TownNavigation.FindPath(map,start,target);
                if(path.Length==0)GD.Print($"NAV_EMPTY anchor={anchor.Key} start={start} closest_start={NavigationServer3D.MapGetClosestPoint(map,start)} target={target} closest_target={NavigationServer3D.MapGetClosestPoint(map,target)}");
                Check(path.Length>1&&path[^1].DistanceTo(target)<.5f,$"navigation {anchor.Key} to core: {path.Length} points, last distance {(path.Length==0?-1:path[^1].DistanceTo(target)):0.00}");count++;}
            return count;
        }finally{NavigationServer3D.FreeRid(region);NavigationServer3D.FreeRid(map);}
    }
}
