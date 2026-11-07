using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using PcgCitySandbox.Pcg;
namespace PcgCitySandbox;
public partial class RoadPreview:Node3D
{
    public int Layer {get;private set;}
    public RoadNetwork? Data {get;private set;}
    public RoadSurfaceMesh? MeshData {get;private set;}
    public void Display(RoadNetwork? data,int layer,IReadOnlyList<RoadIssue>? failures=null,CityEnvironmentData? failureEnvironment=null,string? selectedAnchor=null,int selectedEdge=-1,bool respectDepth=false){
        if(!ReferenceEquals(data,Data)){MeshData=null;Data=data;}
        Layer=layer;foreach(Node child in GetChildren()){RemoveChild(child);child.QueueFree();}
        var env=data?.Environment??failureEnvironment;Visible=layer>0&&env!=null;if(!Visible)return;
        float lift=respectDepth?.08f:.2f;
        Vector3 P(Vector2 p){env!.TryHeightAt(p,out float h);return new(p.X,h+lift,p.Y);}
        var lines=new ImmediateMesh();int lineCount=0;
        void Line(Vector2 a,Vector2 b,Color color){int steps=Math.Max(1,(int)Math.Ceiling(a.DistanceTo(b)/4));for(int i=0;i<steps;i++){
            if(lineCount==0)lines.SurfaceBegin(Mesh.PrimitiveType.Lines);lines.SurfaceSetColor(color);lines.SurfaceAddVertex(P(a.Lerp(b,(float)i/steps))+Vector3.Up*.04f);lines.SurfaceAddVertex(P(a.Lerp(b,(float)(i+1)/steps))+Vector3.Up*.04f);lineCount++;}}
        void Circle(Vector2 p,float radius,Color color){for(int i=0;i<16;i++)Line(p+Vector2.FromAngle(i*Mathf.Tau/16)*radius,p+Vector2.FromAngle((i+1)*Mathf.Tau/16)*radius,color);}
        if(data!=null){var highlight=selectedAnchor==null?new HashSet<int>():data.PathTo(selectedAnchor).ToHashSet();
            if(layer is 1 or 3 or 4){
                MeshData??=RoadSurfaceMesh.Build(data);var triangles=new SurfaceTool();triangles.Begin(Mesh.PrimitiveType.Triangles);
                for(int i=0;i<MeshData.Positions.Count;i++){
                    var surface=data.Geometry.Surfaces[MeshData.SurfaceIds[i]];var road=data.Edges.First(e=>e.Id==surface.RoadId);
                    Color color=layer==4?new Color("6fd591").Lerp(new Color("ed6555"),Math.Clamp(road.MaxGrade/data.Options.MaxGrade,0,1)):layer==3?surface.NodeId.HasValue?new Color("ad8fca"):new Color("d9ba77"):road.Main?new Color("e9d1a0"):new Color("b49372");
                    if(highlight.Contains(road.Id)||selectedEdge==road.Id)color=new("fff051");
                    triangles.SetColor(color);triangles.AddVertex(MeshData.Positions[i]+Vector3.Up*lift);
                }
                if(MeshData.Positions.Count>0)AddChild(new MeshInstance3D{Name="FinalRoadSurface",Mesh=triangles.Commit(),MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,NoDepthTest=!respectDepth,CullMode=BaseMaterial3D.CullModeEnum.Disabled,RenderPriority=20},CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
            }
            if(layer==3)foreach(var boundary in data.Geometry.Boundaries)Line(boundary.A,boundary.B,boundary.AllowsFrontage?new Color("f1e4ad"):new Color("bc94db"));
            foreach(var road in data.Edges){bool picked=highlight.Contains(road.Id)||selectedEdge==road.Id;if(layer==2||picked)for(int i=1;i<road.Points.Count;i++)Line(road.Points[i-1],road.Points[i],picked?new Color("fff051"):road.Main?new Color("ffe0a1"):new Color("bb9071"));}
            if(layer==2)foreach(var node in data.Nodes)Circle(node.Position,node.Role=="端点"?2:3,new Color("ece4cc"));
            if(layer==4)foreach(var issue in data.Geometry.Notices)Circle(issue.Position,2,new Color("dfad66"));
        }
        if(layer==4)foreach(var issue in (failures?.Count>0?failures:data?.Issues)??Array.Empty<RoadIssue>()){
            int steps=Math.Max(2,(int)(issue.From.DistanceTo(issue.To)/8));for(int i=0;i<steps;i+=2)Line(issue.From.Lerp(issue.To,(float)i/steps),issue.From.Lerp(issue.To,(float)Math.Min(i+1,steps)/steps),new Color("ff6454"));Circle(issue.From,7,new Color("ff6454"));
        }
        if(lineCount>0){lines.SurfaceEnd();AddChild(new MeshInstance3D{Mesh=lines,MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,NoDepthTest=!respectDepth,RenderPriority=21},CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});}
    }
}
