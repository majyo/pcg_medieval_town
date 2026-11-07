using Godot;
using System;
using System.Linq;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class BuildingPreview:Node3D
{
    public BuildingMeshData? MeshData {get;private set;}
    public BuildingSet? Data {get;private set;}public int Layer {get;private set;}
    public void Display(BuildingSet? data,int layer,int selected=-1){
        Data=data;Layer=layer;MeshData=null;foreach(Node child in GetChildren()){RemoveChild(child);child.QueueFree();}Visible=data!=null&&layer>0;if(!Visible)return;
        if(layer!=3){var mesh=BuildingMeshBuilder.Build(data!,layer,selected);MeshData=mesh;AddChild(new MeshInstance3D{Name="BuildingMasses",Mesh=mesh.ToMesh(),MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,VertexColorIsSrgb=true,Roughness=1,CullMode=BaseMaterial3D.CullModeEnum.Disabled},CastShadow=GeometryInstance3D.ShadowCastingSetting.On});}
        var lines=new ImmediateMesh();int count=0;
        Vector3 P(Vector2 p){data!.Terrain.TryHeightAt(p,out float h);return new(p.X,h+.12f,p.Y);}
        void Line(Vector2 a,Vector2 b,Color color){if(count==0)lines.SurfaceBegin(Mesh.PrimitiveType.Lines);lines.SurfaceSetColor(color);lines.SurfaceAddVertex(P(a));lines.SurfaceAddVertex(P(b));count++;}
        void Outline(ParcelRegion region,Color color){foreach(var ring in region.Rings)for(int i=0;i<ring.Count;i++)Line(ring[i],ring[(i+1)%ring.Count],color);}
        if(layer==3){var fill=new SurfaceTool();fill.Begin(Mesh.PrimitiveType.Triangles);int filled=0;
            void Fill(ParcelRegion region,Color color){foreach(var p in region.Triangles){fill.SetColor(color);fill.AddVertex(P(p));filled++;}}
            foreach(var space in data!.OpenSpaces)Fill(space.Shape,new Color(.45f,.65f,.42f,.28f));
            foreach(var path in data.Access)Fill(path.Corridor,new Color(.3f,.76f,.79f,.4f));
            foreach(var b in data.Buildings)Fill(b.Footprint,new Color(.85f,.78f,.59f,.6f));
            if(filled>0)AddChild(new MeshInstance3D{Mesh=fill.Commit(),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,Transparency=BaseMaterial3D.TransparencyEnum.Alpha,NoDepthTest=true,CullMode=BaseMaterial3D.CullModeEnum.Disabled,RenderPriority=29}});
        }
        if(layer==3){foreach(var b in data!.Buildings){Outline(b.Footprint,new Color("ead99b"));Outline(b.RoofProjection,new Color("d3b66b"));}foreach(var a in data.Access){Outline(a.Corridor,new Color("61d4d2"));Line(a.StreetPoint,a.Door,new Color("f3ecb8"));}foreach(var s in data.SharedWalls)Line(s.Start,s.End,new Color("cf8ac9"));}
        if(layer==4)foreach(var issue in data!.Issues){var p=issue.Position;Line(p-new Vector2(2,2),p+new Vector2(2,2),new Color("e77666"));Line(p+new Vector2(-2,2),p+new Vector2(2,-2),new Color("e77666"));}
        if(count>0){lines.SurfaceEnd();AddChild(new MeshInstance3D{Mesh=lines,MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,NoDepthTest=true,RenderPriority=30},CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});}
        if(selected>=0&&data!.Buildings.FirstOrDefault(b=>b.Id==selected) is {} chosen){var point=chosen.Footprint.Center;AddChild(new Label3D{Text=$"B{chosen.Id} {chosen.Program}",Position=new Vector3(point.X,chosen.BaseY+chosen.WallHeight+chosen.RoofHeight+2,point.Y),FontSize=40,PixelSize=.2f,NoDepthTest=true,RenderPriority=60,OutlineRenderPriority=59,Billboard=BaseMaterial3D.BillboardModeEnum.Enabled});}
    }
    public override void _Process(double delta){if(!Visible)return;var c=GetViewport().GetCamera3D();if(c==null)return;foreach(Node child in GetChildren())if(child is Label3D label)label.PixelSize=c.Size/Math.Max(1,GetViewport().GetVisibleRect().Size.Y)*.4f;}
}
