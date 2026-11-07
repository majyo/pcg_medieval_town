using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

public static class CityGroundLod
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,Dictionary<string,(CityGeometry Near,CityGeometry Far)>> Cache=new();
    public static (CityGeometry Near,CityGeometry Far) Geometry(object source,TemperateMapData map,ITerrainSurface terrain,IEnumerable<(string Id,string Kind,ParcelRegion Shape)> patches)
    {
        var byTerrain=Cache.GetOrCreateValue(source);if(byTerrain.TryGetValue(terrain.Fingerprint,out var result))return result;
        var items=patches.ToArray();result=(CityGroundMesh.Build(map,items,terrain),CityGroundMesh.Build(map,items,terrain,precision:2,tolerance:.015f));byTerrain[terrain.Fingerprint]=result;GD.Print($"CITY_GROUND_LOD near={result.Near.Triangles} far={result.Far.Triangles}");return result;
    }
    public static Node3D Instantiate(CityGeometry near,CityGeometry far,Material material)
    {
        var root=new RoadLodRoot{Name="Ground",DetailDistance=280};
        void Add(CityGeometry source,bool detailed){
            var chunks=new Dictionary<Vector2I,CityGeometry>();
            for(int i=0;i<source.Positions.Count;i+=3){var center=(source.Positions[i]+source.Positions[i+1]+source.Positions[i+2])/3;var key=new Vector2I((int)MathF.Floor(center.X/128),(int)MathF.Floor(center.Z/128));
                if(!chunks.TryGetValue(key,out var chunk))chunks[key]=chunk=new();
                for(int j=0;j<3;j++){int k=i+j;chunk.Positions.Add(source.Positions[k]);chunk.Normals.Add(source.Normals[k]);chunk.Colors.Add(source.Colors[k]);chunk.Owners.Add(source.Owners[k]);chunk.Parts.Add(source.Parts[k]);chunk.Attributes.Add(source.Attributes[k]);}}
            foreach(var (key,chunk) in chunks){var origin=new Vector3((key.X+.5f)*128,0,(key.Y+.5f)*128);for(int i=0;i<chunk.Positions.Count;i++)chunk.Positions[i]-=origin;
                root.AddChild(new MeshInstance3D{Name=$"{(detailed?"Near":"Far")}_{key.X}_{key.Y}",Position=origin,Mesh=chunk.ToMesh(smooth:true),MaterialOverride=material,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeBegin=detailed?0:280,VisibilityRangeEnd=detailed?280:0});}
        }
        Add(near,true);Add(far,false);root.AddChild(new MeshInstance3D{Name="Overview",Mesh=far.ToMesh(lod:true),MaterialOverride=material,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});return root;
    }
}
