using Godot;
using System;
using System.Linq;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;

public static class SceneAssembler
{
    public static Node3D Build(SceneMeshCache cache,int layer,string? selected=null){
        var data=cache.Data;var root=new Node3D{Name="GeneratedTown"};
        Color Tint(string id,Color original){if(id==selected)return new("e8c96c");if(layer!=2)return original;
            if(id.StartsWith("B")){var b=data.BuildingPlacements.First(p=>p.Id==id).Mass;return b.PrecinctId!=null?new("94afbc"):b.Program=="作坊住宅"?new("aaa0c2"):b.Program=="商住"?new("cfb277"):new("9fbcac");}
            return data.Props.FirstOrDefault(p=>p.Id==id) is {} prop?SceneMeshes.Category(prop.Kind):original;
        }
        void AddMesh(string name,SceneMeshData mesh,bool recolor){if(mesh.Positions.Count==0)return;root.AddChild(new MeshInstance3D{Name=name,Mesh=mesh.ToMesh(recolor?Tint:null),MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,VertexColorIsSrgb=true,Roughness=1,CullMode=BaseMaterial3D.CullModeEnum.Back},CastShadow=name=="Ground"?GeometryInstance3D.ShadowCastingSetting.Off:GeometryInstance3D.ShadowCastingSetting.On});}
        AddMesh("Ground",cache.Ground,false);if(layer!=3){AddMesh("Buildings",cache.Buildings,true);AddMesh("Props",cache.Props,true);}
        var lines=new ImmediateMesh();int count=0;
        Vector3 P(Vector2 p,float lift=.18f){data.Terrain.TryHeightAt(p,out float y);return new(p.X,y+lift,p.Y);}
        void Line(Vector2 a,Vector2 b,Color color){int steps=Math.Max(1,(int)Math.Ceiling(a.DistanceTo(b)/3));for(int i=0;i<steps;i++){if(count==0)lines.SurfaceBegin(Mesh.PrimitiveType.Lines);lines.SurfaceSetColor(color);lines.SurfaceAddVertex(P(a.Lerp(b,(float)i/steps)));lines.SurfaceAddVertex(P(a.Lerp(b,(float)(i+1)/steps)));count++;}}
        void Outline(ParcelRegion region,Color color){foreach(var ring in region.Rings)for(int i=0;i<ring.Count;i++)Line(ring[i],ring[(i+1)%ring.Count],color);}
        if(layer is 1 or 2)foreach(var field in data.Roads.Landscape)foreach(var mark in field.CultivationLines??Array.AsReadOnly(Array.Empty<System.Collections.ObjectModel.ReadOnlyCollection<Vector2>>()))for(int i=1;i<mark.Count;i++)Line(mark[i-1],mark[i],new("81805b"));
        if(layer==3){foreach(var p in data.ProtectedSpaces)Outline(p.Shape,new("65c9ce"));foreach(var b in data.BuildingPlacements)Outline(b.Mass.RoofProjection,new("d2c79e"));foreach(var p in data.Props){Outline(p.Footprint,p.Id==selected?new("ffe079"):new("db9f6e"));Outline(p.Approach,new("91c5d4"));}}
        if(layer==4){Outline(data.VegetationMask,new("88bb74"));foreach(var p in data.Props)Outline(p.Footprint,new Color("75b798").Lerp(new("d47564"),Math.Clamp((p.BaseY-p.MinGround)/.43f,0,1)));foreach(var issue in data.Issues){var p=issue.Position;Line(p-new Vector2(1.5f,1.5f),p+new Vector2(1.5f,1.5f),new("e89071"));Line(p+new Vector2(-1.5f,1.5f),p+new Vector2(1.5f,-1.5f),new("e89071"));}}
        if(count>0){lines.SurfaceEnd();root.AddChild(new MeshInstance3D{Name="Diagnostics",Mesh=lines,MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,NoDepthTest=layer>=3},CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});}
        return root;
    }
}
public partial class ScenePlacementPreview:Node3D
{
    public ScenePlacementSet? Data {get;private set;}
    public SceneMeshCache? Cache {get;private set;}
    public int Layer {get;private set;}
    public void Display(ScenePlacementSet? data,int layer,string? selected=null){
        if(data==null||layer==0){Layer=0;Visible=false;return;}
        if(!ReferenceEquals(data,Data)){Cache=new(data);Data=data;}
        var replacement=SceneAssembler.Build(Cache!,layer,selected);
        foreach(Node child in GetChildren()){RemoveChild(child);child.QueueFree();}AddChild(replacement);Layer=layer;Visible=true;
    }
    public string? Pick(Vector3 origin,Vector3 ray,Vector2? ground){
        if(Data==null||Layer==0)return null;
        if(Layer==3&&ground.HasValue)return Data.Props.FirstOrDefault(p=>p.Footprint.Contains(ground.Value))?.Id??Data.BuildingPlacements.FirstOrDefault(b=>b.Mass.Footprint.Contains(ground.Value))?.Id;
        string? found=null;float nearest=float.PositiveInfinity;
        foreach(var mesh in new[]{Cache!.Props,Cache.Buildings})for(int i=0;i<mesh.Positions.Count;i+=3){var a=mesh.Positions[i];var e1=mesh.Positions[i+1]-a;var e2=mesh.Positions[i+2]-a;var p=ray.Cross(e2);float det=e1.Dot(p);if(Math.Abs(det)<1e-6)continue;var s=origin-a;float u=s.Dot(p)/det;if(u<0||u>1)continue;var q=s.Cross(e1);float v=ray.Dot(q)/det;if(v<0||u+v>1)continue;float t=e2.Dot(q)/det;if(t>0&&t<nearest){nearest=t;found=mesh.Owners[i];}}
        return found;
    }
}
