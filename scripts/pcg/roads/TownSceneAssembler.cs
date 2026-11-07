using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PcgCitySandbox.Pcg;

public sealed record TownRevisions(long Environment,long Settlement,long Roads,long Grading,long Parcels,long Buildings,long Placement);

public static class TownSceneAssembler
{
    public static void Validate(RoadGrading grading,ScenePlacementSet placement,TownRevisions versions)
    {
        if(grading.Graded==null||!ReferenceEquals(grading.Roads,placement.Roads)||!ReferenceEquals(grading,placement.Parcels.Grading)||
            !ReferenceEquals(grading.Graded,placement.Terrain)||grading.RoadRevision!=versions.Roads||placement.Parcels.GradingRevision!=versions.Grading||
            placement.Parcels.RoadRevision!=versions.Roads||placement.Buildings.ParcelRevision!=versions.Parcels||placement.BuildingRevision!=versions.Buildings||
            placement.Roads.EnvironmentRevision!=versions.Environment||placement.Roads.SettlementRevision!=versions.Settlement)
            throw new ArgumentException("装配输入过期：03B 与 06 必须引用同一道路、地形及节点修订号");
    }
    public static Node3D Build(TemperateMapData map,RoadGrading grading,ScenePlacementSet placement,TownRevisions versions,RoadConstructionSettings settings)
    {
        Validate(grading,placement,versions);var native=grading.Terrain as NativeTerrainSurface;
        if(native==null||!ReferenceEquals(native.Map,map))throw new ArgumentException("装配地图与 03B 原地形不同");
        var root=new Node3D{Name="GeneratedTown"};var options=settings.Snapshot();var surface=RoadSceneBuilder.Build(grading,options);
        root.SetMeta("terrain_fingerprint",grading.Graded!.Fingerprint);root.SetMeta("revisions",JsonSerializer.Serialize(versions));
        root.SetMeta("collision_enabled",options.Collision);
        root.SetMeta("road_graph",RoadRuntimeGraph.Build(grading,options));
        var renderer=new MapRenderer();renderer.Build(map);renderer.SetGradedTerrain(grading.Graded);renderer.SetVegetationMask(placement.Options.AvoidVegetation?placement.VegetationMask:null);
        var terrain=new Node3D{Name="Terrain"};root.AddChild(terrain);
        // The saved scene contains native mesh/physics nodes, without generator scripts.
        foreach(var child in renderer.GetChildren())terrain.AddChild(child.Duplicate());
        renderer.Free();
        root.AddChild(RoadSceneBuilder.Instantiate(surface,settings.MaterialOverride));
        var cityMaterial=new ShaderMaterial{Shader=GD.Load<Shader>("res://shaders/city_surface.gdshader")};
        var groundMaterial=(ShaderMaterial)cityMaterial.Duplicate();groundMaterial.SetShaderParameter("ground_surface",true);
        void Add(string name,ArrayMesh mesh,Material material,bool collision){
            var node=new MeshInstance3D{Name=name,Mesh=mesh,MaterialOverride=material};root.AddChild(node);
            if(collision&&mesh.GetSurfaceCount()>0){var body=new StaticBody3D{Name="Collision"};node.AddChild(body);body.AddChild(new CollisionShape3D{Shape=mesh.CreateTrimeshShape()});}}
        var ground=CityGroundLod.Geometry(placement,map,placement.Terrain,placement.GroundPatches.Where(p=>p.RoadId==null&&p.Kind!="广场").Select(p=>($"ground/{p.Id}",p.Kind,p.Shape)));
        root.AddChild(CityGroundLod.Instantiate(ground.Near,ground.Far,groundMaterial));
        Add("Buildings",CityGeometry.Buildings(placement.Buildings).ToMesh(),cityMaterial,options.Collision);
        Add("BuildingDetails",BuildingDetailBuilder.Build(placement.Buildings,map,placement.Terrain).Geometry.ToMesh(),cityMaterial,false);
        Add("Props",CityGeometry.Props(placement).ToMesh(),cityMaterial,options.Collision);
        Add("Fortifications",FortificationPreviewBuilder.Build(placement.Roads.Settlement,map,6,placement.Terrain).ToMesh(),cityMaterial,options.Collision);
        Add("RetainingWalls",RetainingWalls(grading.Graded).ToMesh(),cityMaterial,options.Collision);
        if(options.Collision)terrain.AddChild(TerrainCollision(map,grading.Graded));
        if(options.Navigation)root.AddChild(RoadNavigation.Build(surface,placement.Buildings));
        return root;
    }
    private static CityGeometry RetainingWalls(GradedTerrain g)
    {
        var mesh=new CityGeometry();
        foreach(var wall in g.RetainingWalls){var a=wall.A;var b=wall.B;if(a.DistanceTo(b)<.05f)b=a+Vector2.Right*g.Cell;var d=(b-a).Normalized();var n=d.Orthogonal()*.15f;
            var footprint=new[]{a-n,b-n,b+n,a+n};float bottom=Math.Min(g.Source.HeightAt(wall.Center),g.HeightAt(wall.Center))-.3f,top=bottom+wall.Step+.6f;
            for(int i=0;i<4;i++){var p=footprint[i];var q=footprint[(i+1)%4];var normal=new Vector3(q.Y-p.Y,0,p.X-q.X).Normalized();mesh.Quad(new(p.X,bottom,p.Y),new(q.X,bottom,q.Y),new(q.X,top,q.Y),new(p.X,top,p.Y),normal,new("8a8c7f"),$"retaining/{wall.Id}","wall",8);}
            Vector3 P(int i)=>new(footprint[i].X,top,footprint[i].Y);mesh.Quad(P(0),P(1),P(2),P(3),Vector3.Up,new("a0a294"),$"retaining/{wall.Id}","cap",8);
        }
        return mesh;
    }
    private static Node3D TerrainCollision(TemperateMapData map,GradedTerrain graded)
    {
        var root=new Node3D{Name="TerrainCollision"};var modified=graded.ModifiedNativeCells(map);const int span=16;int segments=map.Side-1;
        for(int z=0;z<segments;z+=span)for(int x=0;x<segments;x+=span){int width=Math.Min(span,segments-x),depth=Math.Min(span,segments-z);bool fine=false;
            for(int dz=0;dz<depth&&!fine;dz++)for(int dx=0;dx<width;dx++)if(modified.Contains((z+dz)*segments+x+dx)){fine=true;break;}
            int n=fine?MapRenderer.RefinedSubdivision:1,nx=width*n+1,nz=depth*n+1;float step=map.Spacing/n;var heights=new float[nx*nz];var origin=map.GridPosition(x,z);
            for(int j=0;j<nz;j++)for(int i=0;i<nx;i++)heights[i+j*nx]=graded.BedHeightAt(origin+new Vector2(i,j)*step)/step;
            var body=new StaticBody3D{Name=$"Tile_{x}_{z}"};root.AddChild(body);body.AddChild(new CollisionShape3D{Position=new(origin.X+width*map.Spacing/2,0,origin.Y+depth*map.Spacing/2),Scale=Vector3.One*step,
                Shape=new HeightMapShape3D{MapWidth=nx,MapDepth=nz,MapData=heights}});
        }
        return root;
    }
    public static string Export(Node3D root,int seed,TownRevisions versions,RoadConstructionOptions options)
    {
        // Each export is a new revision directory; previous exports remain usable.
        string directory=$"res://generated/town_{seed}/{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}";
        Directory.CreateDirectory(ProjectSettings.GlobalizePath(directory));int meshId=0,shapeId=0,materialId=0;
        var saved=new HashSet<ulong>();
        void Save(Resource resource,string file){if(!saved.Add(resource.GetInstanceId()))return;string path=directory+"/"+file;
            var error=ResourceSaver.Save(resource,path);if(error!=Error.Ok)throw new IOException($"无法保存 {path}: {error}");resource.TakeOverPath(path);}
        void Visit(Node node){if(node!=root)node.Owner=root;
            if(node is MeshInstance3D m){if(m.Mesh!=null)Save(m.Mesh,$"mesh_{meshId++}.res");if(m.MaterialOverride!=null)Save(m.MaterialOverride,$"material_{materialId++}.res");}
            if(node is MultiMeshInstance3D mm&&mm.Multimesh!=null)Save(mm.Multimesh,$"forest_{meshId++}.res");
            if(node is CollisionShape3D c&&c.Shape!=null)Save(c.Shape,$"collision_{shapeId++}.res");
            if(node is NavigationRegion3D n&&n.NavigationMesh!=null)Save(n.NavigationMesh,$"navigation_{shapeId++}.res");
            foreach(var child in node.GetChildren())Visit(child);
        }
        Visit(root);Save(root.GetMeta("road_graph").As<RoadRuntimeGraph>(),"road_graph.res");
        var packed=new PackedScene();var pack=packed.Pack(root);if(pack!=Error.Ok)throw new IOException("无法打包 GeneratedTown: "+pack);
        string scene=directory+"/town.tscn";if(ResourceSaver.Save(packed,scene)!=Error.Ok)throw new IOException("无法保存城镇场景");
        var graph=root.GetMeta("road_graph").As<RoadRuntimeGraph>();int core=Array.FindIndex(graph.AnchorIds,id=>id.Contains("core"));var focus=core>=0?graph.AnchorPositions[core]:Vector3.Zero;
        var preview=new Node3D{Name="TownPreview"};
        var instance=ResourceLoader.Load<PackedScene>(scene).Instantiate<Node3D>();preview.AddChild(instance);instance.Owner=preview;
        var camera=new Camera3D{Name="PreviewCamera",Projection=Camera3D.ProjectionType.Orthogonal,Size=700,Current=true,Near=1,Far=10000,Position=focus+new Vector3(650,850,850)};camera.Basis=Basis.LookingAt(focus-camera.Position,Vector3.Up);preview.AddChild(camera);camera.Owner=preview;
        var light=new DirectionalLight3D{Name="Sun",RotationDegrees=new(-35,-38,0),LightColor=new("fff7ed"),LightEnergy=.78f,ShadowEnabled=true,DirectionalShadowMaxDistance=4500};preview.AddChild(light);light.Owner=preview;
        var environment=new WorldEnvironment{Name="Environment",Environment=new Godot.Environment{BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new("d6dfdc"),AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=new("d3dfe4"),AmbientLightEnergy=.38f,TonemapMode=Godot.Environment.ToneMapper.Linear}};preview.AddChild(environment);environment.Owner=preview;
        var demonstration=new PackedScene();if(demonstration.Pack(preview)!=Error.Ok||ResourceSaver.Save(demonstration,directory+"/preview.tscn")!=Error.Ok)throw new IOException("无法保存独立查看场景");preview.Free();
        File.WriteAllText(ProjectSettings.GlobalizePath(directory+"/manifest.json"),JsonSerializer.Serialize(new{schema=1,seed,versions,options,terrainFingerprint=root.GetMeta("terrain_fingerprint").AsString(),scene,meshResources=meshId,shapeResources=shapeId},new JsonSerializerOptions{WriteIndented=true}));
        return scene;
    }
}
