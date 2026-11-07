using Godot;
using System;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class SettlementPreview : Node3D
{
    public SettlementLayout? Data {get;private set;}
    public int Layer {get;private set;}
    public bool Intents {get;private set;}
    public void Display(SettlementLayout? data,int layer,bool intents){Data=data;Layer=layer;Intents=intents;Rebuild();}
    public override void _Process(double delta)
    {
        if(!Visible)return;var camera=GetViewport().GetCamera3D();if(camera==null)return;
        foreach(Node child in GetChildren())if(child is Label3D label)
            label.PixelSize=camera.Size/Math.Max(1,GetViewport().GetVisibleRect().Size.Y)*.4f;
    }
    private void Rebuild()
    {
        foreach(Node child in GetChildren()){RemoveChild(child);child.QueueFree();}
        Visible=Data!=null&&Layer>0;if(!Visible)return;var d=Data!;var e=d.Environment;
        Vector3 Point(Vector2 p){e.TryHeightAt(p,out float h);return new(p.X,h+1.3f,p.Y);}
        var surface=new SurfaceTool();surface.Begin(Mesh.PrimitiveType.Triangles);int faces=0;
        for(int z=0;z<e.CellsZ;z++)for(int x=0;x<e.CellsX;x++){
            if(d.Region!=null)continue;
            if(!e.VertexHeight(x,z).HasValue||!e.VertexHeight(x+1,z).HasValue||!e.VertexHeight(x,z+1).HasValue||!e.VertexHeight(x+1,z+1).HasValue)continue;
            var c=d.Cells[z*e.CellsX+x];if(c.State==SettlementCellState.Outside)continue;
            Color color=c.State switch{SettlementCellState.Included=>new("65b785"),SettlementCellState.Disconnected=>new("a5a5a5"),SettlementCellState.Edge=>new("dfc282"),SettlementCellState.Unknown=>new("808080"),_=>new("cf6666")};
            if(Layer==1&&c.State is SettlementCellState.Restricted or SettlementCellState.Unknown)continue;
            if(Layer==2&&c.Reasons.HasFlag(EnvironmentRestriction.Water))color=new("4c9ccf");
            if(Layer==3)color=c.Score>=0?new Color("e1d6a4").Lerp(new Color("318258"),c.Score).Lerp(new Color("b55a43"),Math.Clamp((c.GradingRisk??0)*2,0,1)):new Color("888888");
            color.A=.58f;var a=e.VertexPosition(x,z);var b=e.VertexPosition(x+1,z+1);
            foreach(var p in new[]{a,new Vector2(b.X,a.Y),b,a,b,new Vector2(a.X,b.Y)}){surface.SetColor(color);surface.AddVertex(Point(p));}faces++;
        }
        if(d.Region is {} plan)foreach(var district in plan.Districts){
            Color color=district.Role switch{TownDistrictRole.FortifiedCore=>new("d8bb78"),TownDistrictRole.InstitutionSuburb=>new("b6a0ce"),_=>new("8aba96")};
            if(Layer==2)color=district.Role==TownDistrictRole.FortifiedCore?new("d8bb78"):new("77b5a4");
            color.A=Layer==3?.14f:Layer==4?(district.Role==TownDistrictRole.FortifiedCore?.38f:.08f):.45f;
            foreach(var p in district.Shape.Triangles){surface.SetColor(color);surface.AddVertex(Point(p));faces++;}
        }
        StandardMaterial3D Material()=>new(){VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,Transparency=BaseMaterial3D.TransparencyEnum.Alpha,NoDepthTest=true,CullMode=BaseMaterial3D.CullModeEnum.Disabled};
        if(faces>0)AddChild(new MeshInstance3D{Mesh=surface.Commit(),MaterialOverride=Material(),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
        var lines=new ImmediateMesh();lines.SurfaceBegin(Mesh.PrimitiveType.Lines);int segments=0;
        void Line(Vector2 a,Vector2 b,Color color){if(!e.TryHeightAt(a,out _)||!e.TryHeightAt(b,out _))return;lines.SurfaceSetColor(color);lines.SurfaceAddVertex(Point(a)+Vector3.Up);lines.SurfaceAddVertex(Point(b)+Vector3.Up);segments++;}
        void Dashed(Vector2 a,Vector2 b,Color color){int count=Math.Max(2,(int)(a.DistanceTo(b)/8));for(int i=0;i<count;i+=2)Line(a.Lerp(b,(float)i/count),a.Lerp(b,(float)Math.Min(i+1,count)/count),color);}
        if(d.Region==null)foreach(var edge in d.Boundary)Line(edge.A,edge.B,new Color("b6f4bc"));
        if(d.Region==null)for(int i=0;i<96;i+=2){float a=i*Mathf.Tau/96,b=(i+1)*Mathf.Tau/96;Line(d.Options.Center+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*d.Options.Radius,d.Options.Center+new Vector2(Mathf.Cos(b),Mathf.Sin(b))*d.Options.Radius,Colors.White);}
        if(d.Region is {} region){
            foreach(var district in region.Districts)foreach(var ring in district.Shape.Rings)for(int i=0;i<ring.Count;i++)Line(ring[i],ring[(i+1)%ring.Count],new Color("dde7cc"));
            if(Layer==3||Intents)foreach(var link in region.Links)for(int i=1;i<link.Points.Count;i++)Line(link.Points[i-1],link.Points[i],new Color("75b9dc"));
        }
        var r=d.Plaza;var corners=new[]{r.Position,new Vector2(r.End.X,r.Position.Y),r.End,new Vector2(r.Position.X,r.End.Y)};
        for(int i=0;i<4;i++)Line(corners[i],corners[(i+1)%4],new Color("ffd369"));
        for(int i=0;i<24;i++){float a=i*Mathf.Tau/24,b=(i+1)*Mathf.Tau/24;Line(d.Core+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*8,d.Core+new Vector2(Mathf.Cos(b),Mathf.Sin(b))*8,new Color("ffd369"));}
        void Label(string text,Vector2 p){var label=new Label3D{Text=text,Position=Point(p)+Vector3.Up*5,FontSize=40,PixelSize=.25f,RenderPriority=50,OutlineRenderPriority=49,NoDepthTest=true,Billboard=BaseMaterial3D.BillboardModeEnum.Enabled,Modulate=Colors.White,OutlineModulate=Colors.Black};AddChild(label);}
        if(d.Town==null)Label("主核心 / 广场",d.Core+new Vector2(0,-35));
        foreach(var entry in d.Entries){var p=entry.Position;var dir=entry.Edge.Outward;var end=p+dir*22;Line(p,end,new Color("65bdff"));Line(end,end-dir*10+dir.Orthogonal()*7,new Color("65bdff"));Line(end,end-dir*10-dir.Orthogonal()*7,new Color("65bdff"));Label(entry.Id,p-dir*22);if(Intents&&d.Region==null)Dashed(p,d.Core,new Color("e3dcbb"));}
        lines.SurfaceEnd();if(segments>0)AddChild(new MeshInstance3D{Mesh=lines,MaterialOverride=Material(),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
    }
}
