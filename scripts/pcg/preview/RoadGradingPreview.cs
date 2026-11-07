using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;

// 03B preview. Layers: 1 design surface, 2 cut/fill, 3 profiles, 4 issues.
public partial class RoadGradingPreview:Node3D
{
    public int Layer {get;private set;}
    public RoadGrading? Data {get;private set;}
    public RoadGradingMesh? MeshData {get;private set;}
    public const float Saturation=.6f;
    public static Color CutFill(float delta)
    {
        float t=Math.Clamp(Math.Abs(delta)/Saturation,0,1);var neutral=new Color("e6dcc4");
        return delta<0?neutral.Lerp(new Color("d2463c"),t):neutral.Lerp(new Color("2f6fd0"),t);
    }
    public void Display(RoadGrading? data,int layer,int selectedRoad=-1,RoadGradingFailure? failure=null,bool terrainApplied=false)
    {
        if(!ReferenceEquals(data,Data)){Data=data;MeshData=null;}
        Layer=layer;foreach(Node child in GetChildren()){RemoveChild(child);child.QueueFree();}
        Visible=layer>0&&(data!=null||failure!=null);if(!Visible)return;
        var lines=new ImmediateMesh();bool open=false;
        void Line(Vector3 a,Vector3 b,Color color){if(!open){lines.SurfaceBegin(Mesh.PrimitiveType.Lines);open=true;}lines.SurfaceSetColor(color);lines.SurfaceAddVertex(a);lines.SurfaceSetColor(color);lines.SurfaceAddVertex(b);}
        void Circle(Vector2 p,float y,float radius,Color color){for(int i=0;i<24;i++){var a=p+Vector2.FromAngle(i*Mathf.Tau/24)*radius;var b=p+Vector2.FromAngle((i+1)*Mathf.Tau/24)*radius;Line(new(a.X,y,a.Y),new(b.X,y,b.Y),color);}}
        if(data!=null){
            if(layer==1||layer==2&&!terrainApplied){
                MeshData??=RoadGradingMesh.Build(data);var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);
                var mains=data.Roads.Edges.ToDictionary(e=>e.Id,e=>e.Main);var sections=data.Profiles.ToDictionary(p=>p.RoadId,p=>p.Section);
                for(int i=0;i+2<MeshData.Positions.Count;i+=3){
                    var a=MeshData.Positions[i];var b=MeshData.Positions[i+1];var c=MeshData.Positions[i+2];var normal=(c-a).Cross(b-a).Normalized();if(normal.Y<0)normal=-normal;
                    for(int k=0;k<3;k++){int v=i+k;int road=MeshData.Roads[v];
                        Color color=layer==2?CutFill(MeshData.Delta(v)):road<0?new Color("c8c3b2"):sections[road]==RoadCrossSection.Kennel?new Color("a9a397"):mains[road]?new Color("d8c39a"):new Color("b4987a");
                        if(road==selectedRoad&&layer==1)color=color.Lerp(new Color("fff051"),.55f);
                        st.SetNormal(normal);st.SetColor(color);st.AddVertex(MeshData.Positions[v]+Vector3.Up*(layer==1?.03f:.15f));}}
                var material=layer==1?new StandardMaterial3D{VertexColorUseAsAlbedo=true,CullMode=BaseMaterial3D.CullModeEnum.Disabled}:new StandardMaterial3D{VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,NoDepthTest=true,CullMode=BaseMaterial3D.CullModeEnum.Disabled,RenderPriority=20};
                if(MeshData.Positions.Count>0)AddChild(new MeshInstance3D{Name="DesignSurface",Mesh=st.Commit(),MaterialOverride=material,CastShadow=layer==1?GeometryInstance3D.ShadowCastingSetting.On:GeometryInstance3D.ShadowCastingSetting.Off});
            }
            foreach(var p in data.Profiles){
                var edge=data.Roads.Edges.First(e=>e.Id==p.RoadId);bool picked=p.RoadId==selectedRoad;
                if(layer!=3&&!(picked&&layer is not 1 and not 5)&&!(layer==4&&p.GradeChangeRelaxed))continue;
                var line=new Centerline(edge);float step=layer==3?2:1;
                for(float s=0;s<p.Length;s+=step){float e=Math.Min(p.Length,s+step);var a=line.PointAt(s);var b=line.PointAt(e);
                    var color=picked?new Color("fff051"):layer==4?new Color("ff6454"):new Color("f0c060");
                    Line(new(a.X,p.HeightAt(s)+.2f,a.Y),new(b.X,p.HeightAt(e)+.2f,b.Y),color);
                    if(layer==3){Line(new(a.X,p.GroundAt(s)+.2f,a.Y),new(b.X,p.GroundAt(e)+.2f,b.Y),new Color("f4f4ee"));
                        if(((int)s)%4==0){float d=p.HeightAt(s)-p.GroundAt(s);Line(new(a.X,p.GroundAt(s)+.2f,a.Y),new(a.X,p.HeightAt(s)+.2f,a.Y),CutFill(d*3));}}}
            }
            if(layer is 4 or 5&&data.Graded is {} graded)foreach(var wall in graded.RetainingWalls){var a=wall.A;var b=wall.B;float ya=graded.RenderHeightAt(a)+1,yb=graded.RenderHeightAt(b)+1;
                for(int k=0;k<5;k++){float o=(k-2)*.12f;var n=(b-a).Normalized().Orthogonal()*o;Line(new(a.X+n.X,ya,a.Y+n.Y),new(b.X+n.X,yb,b.Y+n.Y),new Color("ff3030"));}Circle(wall.Center,(ya+yb)/2,3,new Color("ff3030"));}
            if(layer==4){
                foreach(var n in data.Notices){data.TryDesignHeight(n.Position,out float y);if(y==0)y=data.GroundHeight(n.Position);
                    float r=n.Kind=="grade_change"?6:n.Kind is "cut" or "fill"?4:8;var tint=n.Kind switch{"grade_change"=>new Color("ff6454"),"junction"=>new Color("bc94db"),"plaza"=>new Color("79abbf"),_=>new Color("dfad66")};Circle(n.Position,y+.3f,r*.6f,tint);
                    Circle(n.Position,y+.3f,r,n.Kind switch{"grade_change"=>new Color("ff6454"),"junction"=>new Color("bc94db"),"plaza"=>new Color("79abbf"),_=>new Color("dfad66")});}
                foreach(var plane in data.Planes){var c=plane.Origin;float y=plane.HeightAt(c)+.3f;var dir=plane.Gradient.LengthSquared()>1e-10f?-plane.Gradient.Normalized():Vector2.Zero;
                    Line(new(c.X,y,c.Y),new(c.X+dir.X*3,y,c.Y+dir.Y*3),new Color("bc94db"));}
            }
        }
        if(failure!=null&&layer==4){float y=data?.GroundHeight(failure.Position)??0;Circle(failure.Position,y+.3f,6,new Color("ff3030"));Circle(failure.Position,y+.3f,9,new Color("ff3030"));}
        if(open){lines.SurfaceEnd();AddChild(new MeshInstance3D{Name="GradingLines",Mesh=lines,MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,NoDepthTest=true,RenderPriority=21},CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});}
    }
}

// Longitudinal profile of one road: terrain (grey) and design (gold), with the
// straight gate runs shaded.
public partial class ProfileChart:Control
{
    private RoadProfile? _profile;private string _title="";
    public void Show(RoadProfile? profile,string title){_profile=profile;_title=title;QueueRedraw();}
    public override void _Draw()
    {
        var size=Size;DrawRect(new Rect2(Vector2.Zero,size),new Color(.05f,.08f,.07f,.9f));var font=ThemeDB.FallbackFont;
        if(_profile==null){DrawString(font,new Vector2(8,20),"点击道路查看纵断面",HorizontalAlignment.Left,-1,12,new Color("a8b6a8"));return;}
        var p=_profile;float min=Math.Min(p.Ground.Min(),p.Design.Min()),max=Math.Max(p.Ground.Max(),p.Design.Max());float pad=Math.Max(.2f,(max-min)*.12f);min-=pad;max+=pad;
        var plot=new Rect2(34,22,size.X-42,size.Y-82);var strip=new Rect2(34,plot.End.Y+8,size.X-42,30);
        Vector2 At(int i,float h)=>new(plot.Position.X+plot.Size.X*i/(p.Design.Count-1),plot.End.Y-plot.Size.Y*(h-min)/(max-min));
        foreach(var (a,b) in p.StraightRuns)DrawRect(new Rect2(At(a,max).X,plot.Position.Y,At(b,max).X-At(a,max).X,plot.Size.Y),new Color(.5f,.5f,.6f,.25f));
        DrawLine(new(plot.Position.X,plot.End.Y),plot.End,new Color("5d6b60"));DrawLine(plot.Position,new(plot.Position.X,plot.End.Y),new Color("5d6b60"));
        var ground=Enumerable.Range(0,p.Ground.Count).Select(i=>At(i,p.Ground[i])).ToArray();var design=Enumerable.Range(0,p.Design.Count).Select(i=>At(i,p.Design[i])).ToArray();
        DrawPolyline(ground,new Color("d9dccf"),1.5f,true);DrawPolyline(design,new Color("f0c060"),2,true);
        // Cut/fill strip: bars from the zero line, red cut, blue fill.
        float span=Math.Max(.05f,Enumerable.Range(0,p.Design.Count).Max(i=>Math.Abs(p.Design[i]-p.Ground[i])));float zero=strip.Position.Y+strip.Size.Y/2;
        DrawLine(new(strip.Position.X,zero),new(strip.End.X,zero),new Color("5d6b60"));
        for(int i=0;i<p.Design.Count;i++){float d=p.Design[i]-p.Ground[i];float x=strip.Position.X+strip.Size.X*i/(p.Design.Count-1);DrawLine(new(x,zero),new(x,zero-d/span*strip.Size.Y/2),RoadGradingPreview.CutFill(d*RoadGradingPreview.Saturation/span),1.5f);}
        DrawString(font,new Vector2(2,zero+4),$"±{span:0.00}",HorizontalAlignment.Left,-1,10,new Color("a8b6a8"));
        DrawString(font,new Vector2(8,15),_title,HorizontalAlignment.Left,-1,12,new Color("e6ede0"));
        DrawString(font,new Vector2(2,plot.Position.Y+8),$"{max:0.0}",HorizontalAlignment.Left,-1,10,new Color("a8b6a8"));
        DrawString(font,new Vector2(2,plot.End.Y),$"{min:0.0}",HorizontalAlignment.Left,-1,10,new Color("a8b6a8"));
        DrawString(font,new Vector2(plot.End.X-60,size.Y-4),$"{p.Length:0} m",HorizontalAlignment.Left,-1,10,new Color("a8b6a8"));
        DrawString(font,new Vector2(plot.Position.X,size.Y-4),"灰：原地形 · 金：设计 · 下：挖红/填蓝",HorizontalAlignment.Left,-1,10,new Color("a8b6a8"));
    }
}
