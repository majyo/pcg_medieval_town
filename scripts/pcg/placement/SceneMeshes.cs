using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
namespace PcgCitySandbox.Pcg;

public sealed class SceneMeshData
{
    public ReadOnlyCollection<Vector3> Positions {get;}
    public ReadOnlyCollection<Vector3> Normals {get;}
    public ReadOnlyCollection<Color> Colors {get;}
    public ReadOnlyCollection<string> Owners {get;}
    internal SceneMeshData(List<Vector3> p,List<Vector3> n,List<Color> c,List<string> owners){Positions=Array.AsReadOnly(p.ToArray());Normals=Array.AsReadOnly(n.ToArray());Colors=Array.AsReadOnly(c.ToArray());Owners=Array.AsReadOnly(owners.ToArray());}
    public ArrayMesh ToMesh(Func<string,Color,Color>? tint=null){using var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);for(int i=0;i<Positions.Count;i++){st.SetNormal(Normals[i]);st.SetColor(tint?.Invoke(Owners[i],Colors[i])??Colors[i]);st.SetUV(new(Positions[i].X/4,Positions[i].Z/4));st.AddVertex(Positions[i]);}return st.Commit();}
}
public sealed class SceneMeshCache
{
    public ScenePlacementSet Data {get;}
    public SceneMeshData Ground {get;}
    public SceneMeshData Buildings {get;}
    public SceneMeshData Props {get;}
    public SceneMeshCache(ScenePlacementSet data){Data=data;Ground=SceneMeshes.Ground(data);Buildings=SceneMeshes.Buildings(data);Props=SceneMeshes.Props(data);}
}
public static class SceneMeshes
{
    public static Color GroundColor(string kind)=>kind switch{"主路"=>new("aa9473"),"街巷"=>new("8e8064"),"广场"=>new("aeaa96"),"院场"=>new("b3ad92"),"通院地面"=>new("aaa188"),"作坊院地"=>new("9a886d"),"后园"=>new("7f9564"),"田地"=>new("aaa06a"),"果园"=>new("7b965a"),"草地"=>new("8eaa6a"),_=>new("96988c")};
    public static Color Category(ScenePropKind kind)=>kind switch{ScenePropKind.Shed=>new("a5a0c0"),ScenePropKind.Stall=>new("dab36c"),ScenePropKind.Well=>new("79abbf"),ScenePropKind.Bench=>new("89bba6"),ScenePropKind.Fence=>new("c5b193"),_=>new("c89372")};
    public static SceneMeshData Ground(ScenePlacementSet data){if(data.Terrain is GradedTerrain g && g.Source is NativeTerrainSurface native){var geometry=CityGroundLod.Geometry(data,native.Map,data.Terrain,data.GroundPatches.Where(p=>p.RoadId==null&&p.Kind!="广场").Select(p=>($"ground/{p.Id}",p.Kind,p.Shape))).Far;return new(geometry.Positions,geometry.Normals,geometry.Colors,geometry.Owners);}
        var mesh=RoadSurfaceMesh.Build(data.Environment,data.GroundPatches.Select(p=>(p.Id,p.Shape)));var p=new List<Vector3>();var n=new List<Vector3>();var c=new List<Color>();var ids=new List<string>();
        for(int i=0;i<mesh.Positions.Count;i+=3){var normal=-(mesh.Positions[i+1]-mesh.Positions[i]).Cross(mesh.Positions[i+2]-mesh.Positions[i]).Normalized();if(normal.Y<0)normal=-normal;
            for(int j=0;j<3;j++){var source=data.GroundPatches[mesh.SurfaceIds[i+j]];p.Add(mesh.Positions[i+j]+Vector3.Up*.12f);n.Add(normal);c.Add(data.Options.Scheme==SceneScheme.GrayStudy?new Color("a4a69b"):GroundColor(source.Kind));ids.Add($"ground/{source.Id}");}}
        return new(p,n,c,ids);
    }
    public static SceneMeshData Buildings(ScenePlacementSet data){var mesh=BuildingMeshBuilder.Build(data.Buildings);var colors=mesh.Colors.ToList();
        if(data.Options.Scheme==SceneScheme.EarthAndTimber)for(int i=0;i<colors.Count;i++)if(mesh.Parts[i]=="roof"){var b=data.Buildings.Buildings.First(b=>b.Id==mesh.Owners[i]);colors[i]=b.PrecinctId!=null?new("646d6b"):b.InsideCore?new("82705b"):new("8c8061");}
        return new(mesh.Positions.ToList(),mesh.Normals.ToList(),colors,mesh.Owners.Select(id=>$"B{id}").ToList());
    }
    public static SceneMeshData Props(ScenePlacementSet data){var positions=new List<Vector3>();var normals=new List<Vector3>();var colors=new List<Color>();var owners=new List<string>();
        foreach(var prop in data.Props){
            var tangent=(prop.Corners[1]-prop.Corners[0]).Normalized();var inward=(prop.Corners[3]-prop.Corners[0]).Normalized();var center=(prop.Corners[0]+prop.Corners[2])/2;
            float w=prop.Corners[0].DistanceTo(prop.Corners[1]),d=prop.Corners[0].DistanceTo(prop.Corners[3]),h=prop.Height;
            Color wood=data.Options.Scheme==SceneScheme.GrayStudy?new("9b9b91"):new("7c6145"),dark=wood.Darkened(.2f),stone=new("aaa897"),cloth=data.Options.Scheme==SceneScheme.GrayStudy?new("c3c2b7"):new("bdad7d");
            Vector3 World(Vector3 v){var p=center+tangent*v.X+inward*v.Z;return new(p.X,prop.BaseY+v.Y,p.Y);}
            void Tri(Vector3 a,Vector3 b,Vector3 c,Vector3 normal,Color color){a=World(a);b=World(b);c=World(c);normal=new(tangent.X*normal.X+inward.X*normal.Z,normal.Y,tangent.Y*normal.X+inward.Y*normal.Z);
                if((b-a).Cross(c-a).LengthSquared()<1e-12)return;if((b-a).Cross(c-a).Dot(normal)>0)(b,c)=(c,b);
                foreach(var v in new[]{a,b,c}){positions.Add(v);normals.Add(normal.Normalized());colors.Add(color);owners.Add(prop.Id);}}
            void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 e,Vector3 normal,Color color){Tri(a,b,c,normal,color);Tri(a,c,e,normal,color);}
            void Box(Vector3 offset,Vector3 size,Color color){var a=offset-size/2;var b=offset+size/2;
                Quad(new(a.X,a.Y,a.Z),new(b.X,a.Y,a.Z),new(b.X,b.Y,a.Z),new(a.X,b.Y,a.Z),Vector3.Forward,color);
                Quad(new(a.X,a.Y,b.Z),new(a.X,b.Y,b.Z),new(b.X,b.Y,b.Z),new(b.X,a.Y,b.Z),Vector3.Back,color);
                Quad(new(a.X,a.Y,a.Z),new(a.X,b.Y,a.Z),new(a.X,b.Y,b.Z),new(a.X,a.Y,b.Z),Vector3.Left,color);
                Quad(new(b.X,a.Y,a.Z),new(b.X,a.Y,b.Z),new(b.X,b.Y,b.Z),new(b.X,b.Y,a.Z),Vector3.Right,color);
                Quad(new(a.X,b.Y,a.Z),new(b.X,b.Y,a.Z),new(b.X,b.Y,b.Z),new(a.X,b.Y,b.Z),Vector3.Up,color);
                Quad(new(a.X,a.Y,a.Z),new(a.X,a.Y,b.Z),new(b.X,a.Y,b.Z),new(b.X,a.Y,a.Z),Vector3.Down,color);}
            void Roof(float wallHeight,Color color){var a=new Vector3(-w/2,wallHeight,-d/2);var b=new Vector3(w/2,wallHeight,-d/2);var c=new Vector3(w/2,wallHeight,d/2);var e=new Vector3(-w/2,wallHeight,d/2);var left=new Vector3(-w/2,h,0);var right=new Vector3(w/2,h,0);
                var n=new Vector3(0,d/2,-(h-wallHeight)).Normalized();Quad(a,b,right,left,n,color);Quad(e,left,right,c,new(0,n.Y,-n.Z),color.Darkened(.08f));
                if(prop.Kind==ScenePropKind.Shed){Tri(a,left,e,Vector3.Left,wood);Tri(b,c,right,Vector3.Right,wood);}}
            float footing=Math.Max(.03f,prop.BaseY-prop.MinGround+.02f);
            if(prop.Kind!=ScenePropKind.Fence&&footing>.08f)Box(new(0,-footing/2,0),new(w*.98f,footing,d*.98f),stone.Darkened(.18f));
            switch(prop.Kind){
                case ScenePropKind.Fence:
                    foreach(float x in new[]{-w/2+.07f,w/2-.07f})Box(new(x,(h-footing)/2,0),new(.14f,h+footing,.14f),wood);
                    Box(new(0,h*.38f,0),new(w,.12f,.1f),wood);Box(new(0,h*.78f,0),new(w,.12f,.1f),wood);break;
                case ScenePropKind.Shed:
                    Box(new(0,h*.32f,0),new(w-.16f,h*.64f,d-.16f),wood);Roof(h*.64f,dark);Box(new(0,h*.24f,-d/2+.065f),new(w*.3f,h*.48f,.035f),dark);break;
                case ScenePropKind.Stall:
                    foreach(float x in new[]{-w/2+.08f,w/2-.08f})foreach(float z in new[]{-d/2+.08f,d/2-.08f})Box(new(x,h*.38f,z),new(.12f,h*.76f,.12f),wood);
                    Box(new(0,h*.3f,-d*.16f),new(w*.85f,.14f,d*.4f),wood);Roof(h*.76f,cloth);break;
                case ScenePropKind.Bench:
                    foreach(float x in new[]{-w*.35f,w*.35f})Box(new(x,h*.28f,0),new(.15f,h*.56f,d*.7f),dark);
                    Box(new(0,h*.56f,0),new(w,.12f,d),wood);Box(new(0,h*.88f,d*.38f),new(w,.2f,.12f),wood);break;
                case ScenePropKind.Well:
                    float radius=Math.Min(w,d)*.48f,inner=radius*.64f;
                    for(int i=0;i<12;i++){float a=i*Mathf.Tau/12,b=(i+1)*Mathf.Tau/12;var u=new Vector3(Mathf.Cos(a),0,Mathf.Sin(a));var v=new Vector3(Mathf.Cos(b),0,Mathf.Sin(b));
                        Quad(u*radius,v*radius,v*radius+Vector3.Up*h,u*radius+Vector3.Up*h,(u+v).Normalized(),stone);
                        Quad(u*inner,u*inner+Vector3.Up*h,v*inner+Vector3.Up*h,v*inner,-(u+v).Normalized(),stone.Darkened(.25f));
                        Quad(u*radius+Vector3.Up*h,v*radius+Vector3.Up*h,v*inner+Vector3.Up*h,u*inner+Vector3.Up*h,Vector3.Up,stone.Lightened(.1f));}break;
                default:
                    Box(new(-w*.18f,h*.32f,0),new(w*.6f,h*.64f,d*.9f),wood);Box(new(w*.32f,h*.23f,0),new(w*.33f,h*.46f,d*.7f),dark);
                    Box(new(-w*.16f,h*.79f,0),new(w*.52f,h*.34f,d*.72f),wood.Lightened(.13f));break;
            }
        }return new(positions,normals,colors,owners);
    }
}
