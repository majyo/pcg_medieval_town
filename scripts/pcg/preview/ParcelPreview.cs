using Godot;
using System;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class ParcelPreview:Node3D
{
    public int Layer {get;private set;}public ParcelLayout? Data {get;private set;}
    public void Display(ParcelLayout? data,int layer,int selected=-1){Data=data;Layer=layer;foreach(Node child in GetChildren()){RemoveChild(child);child.QueueFree();}Visible=data!=null&&layer>0;if(!Visible)return;var env=data!.Environment;
        Vector3 P(Vector2 p){if(!data.Terrain.TryHeightAt(p,out float h))throw new InvalidOperationException("地块预览缺少地面");return new(p.X,h+1.8f,p.Y);}
        var surface=new SurfaceTool();surface.Begin(Mesh.PrimitiveType.Triangles);int count=0;var lines=new ImmediateMesh();int lineCount=0;
        void Line(Vector2 a,Vector2 b,Color c){if(lineCount==0)lines.SurfaceBegin(Mesh.PrimitiveType.Lines);lines.SurfaceSetColor(c);lines.SurfaceAddVertex(P(a)+Vector3.Up*.2f);lines.SurfaceAddVertex(P(b)+Vector3.Up*.2f);lineCount++;}
        void Region(ParcelRegion shape,Color color,bool fill=true){if(fill)foreach(var p in shape.Triangles){surface.SetColor(color);surface.AddVertex(P(p));count++;}foreach(var ring in shape.Rings)for(int i=0;i<ring.Count;i++)Line(ring[i],ring[(i+1)%ring.Count],new Color(.9f,.94f,.82f,.9f));}
        if(layer==1)foreach(var block in data.Blocks)Region(block.Shape,Color.FromHsv((block.Id*.618f)%1,.32f,.8f,.62f));
        if(layer==2||layer==3)foreach(var parcel in data.Parcels){Color color=parcel.Id==selected?new Color("f6ee83"):parcel.Use switch{ParcelUse.Commercial=>new("e1b866"),ParcelUse.Production=>new("b79ccf"),_=>new("8fbf92")};color.A=.72f;Region(parcel.Shape,color,layer==2);if(layer==3){if(parcel.Yard!=null)Region(parcel.Yard,new Color(.51f,.63f,.33f,.4f));Region(parcel.Buildable,new Color(.28f,.8f,.68f,.7f));}}
        if(layer==4)foreach(var rest in data.Remainders)Region(rest.Shape,new Color(.9f,.6f,.3f,.72f));
        if(selected>=0&&selected<data.Parcels.Count){var parcel=data.Parcels[selected];foreach(var f in parcel.Frontage)Line(f.A,f.B,new Color("ffdc45"));var label=new Label3D{Text=$"地块 P{parcel.Id}",Position=P(parcel.Shape.Center)+Vector3.Up*3,FontSize=40,NoDepthTest=true,RenderPriority=50,OutlineRenderPriority=49,Billboard=BaseMaterial3D.BillboardModeEnum.Enabled};AddChild(label);}
        StandardMaterial3D Mat()=>new(){VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,Transparency=BaseMaterial3D.TransparencyEnum.Alpha,NoDepthTest=true,CullMode=BaseMaterial3D.CullModeEnum.Disabled,RenderPriority=15};
        if(count>0)AddChild(new MeshInstance3D{Mesh=surface.Commit(),MaterialOverride=Mat(),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
        if(lineCount>0){lines.SurfaceEnd();AddChild(new MeshInstance3D{Mesh=lines,MaterialOverride=Mat(),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});}
    }
    public override void _Process(double delta){if(!Visible)return;var c=GetViewport().GetCamera3D();if(c==null)return;foreach(Node child in GetChildren())if(child is Label3D label)label.PixelSize=c.Size/Math.Max(1,GetViewport().GetVisibleRect().Size.Y)*.4f;}
}
