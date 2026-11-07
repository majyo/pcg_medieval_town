using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PcgCitySandbox.Pcg;
public sealed class BuildingMeshData
{
    public ReadOnlyCollection<Vector3> Positions {get;}
    public ReadOnlyCollection<Vector3> Normals {get;}
    public ReadOnlyCollection<Color> Colors {get;}
    public ReadOnlyCollection<int> Owners {get;}
    public ReadOnlyCollection<string> Parts {get;}
    internal BuildingMeshData(List<Vector3> positions,List<Vector3> normals,List<Color> colors,List<int> owners,List<string> parts)
    {Positions=Array.AsReadOnly(positions.ToArray());Normals=Array.AsReadOnly(normals.ToArray());Colors=Array.AsReadOnly(colors.ToArray());Owners=Array.AsReadOnly(owners.ToArray());Parts=Array.AsReadOnly(parts.ToArray());}
    public ArrayMesh ToMesh(){var tool=new SurfaceTool();tool.Begin(Mesh.PrimitiveType.Triangles);for(int i=0;i<Positions.Count;i++){tool.SetColor(Colors[i]);tool.SetNormal(Normals[i]);tool.AddVertex(Positions[i]);}return tool.Commit();}
}
public static class BuildingMeshBuilder
{
    public static BuildingMeshData Build(BuildingSet data,int layer=1,int selected=-1)
    {
        var vertices=new List<Vector3>();var normals=new List<Vector3>();var colors=new List<Color>();var owners=new List<int>();var parts=new List<string>();
        Vector3 V(Vector2 p,float y)=>new(p.X,y,p.Y);
        void Triangle(Vector3 a,Vector3 b,Vector3 c,Vector3 normal,Color color,int owner,string part){
            var cross=(b-a).Cross(c-a);if(cross.LengthSquared()<1e-10f)return;
            // Godot front faces use clockwise winding relative to the outward normal.
            if(cross.Dot(normal)>0)(b,c)=(c,b);
            foreach(var p in new[]{a,b,c}){vertices.Add(p);normals.Add(normal.Normalized());colors.Add(color);owners.Add(owner);parts.Add(part);}
        }
        void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d,Vector3 normal,Color color,int owner,string part){Triangle(a,b,c,normal,color,owner,part);Triangle(a,c,d,normal,color,owner,part);}
        var byId=data.Buildings.ToDictionary(b=>b.Id);
        foreach(var b in data.Buildings){
            Color wall=b.PrecinctId!=null?new("b7b6a9"):b.InsideCore?new("b5ad9a"):new("a9a48f");
            Color roof=b.PrecinctId!=null?new("555c61"):new("62594e");
            float variation=((b.ParcelId??b.Id)*37%17-8)*.003f;wall=wall.Lightened(variation);
            if(layer==2){wall=b.Program switch{"商住"=>new("c5a772"),"作坊住宅"=>new("a59cb3"),"住宅"=>new("a5b8b0"),_=>new("9daec4")};roof=wall.Darkened(.38f);}
            if(layer==4){wall=new Color("76b891").Lerp(new Color("d2765d"),Math.Clamp((b.BaseY-b.MinGround)/data.Options.MaxFoundation,0,1));roof=wall.Darkened(.3f);}
            if(b.Id==selected){wall=new("e4ce82");roof=new("b7a05e");}
            float top=b.BaseY+b.WallHeight;
            for(int edge=0;edge<4;edge++){
                var a=b.Corners[edge];var z=b.Corners[(edge+1)%4];var dir=(z-a).Normalized();var normal=new Vector2(-dir.Y,dir.X);if(b.Footprint.Contains((a+z)/2+normal*.05f))normal=-normal;
                var shared=data.SharedWalls.Where(s=>(s.A==b.Id||s.B==b.Id)&&s.Owner!=b.Id&&Math.Abs((s.Start-a).Cross(dir))<.02f&&Math.Abs((s.End-a).Cross(dir))<.02f).ToArray();
                var cuts=new SortedSet<float>{0,a.DistanceTo(z)};foreach(var s in shared){cuts.Add(Math.Clamp((s.Start-a).Dot(dir),0,a.DistanceTo(z)));cuts.Add(Math.Clamp((s.End-a).Dot(dir),0,a.DistanceTo(z)));}
                var values=cuts.ToArray();
                for(int k=1;k<values.Length;k++){
                    var p=a+dir*values[k-1];var q=a+dir*values[k];if(p.DistanceTo(q)<.001f)continue;var mid=(p+q)/2;
                    float lower=float.NegativeInfinity;foreach(var s in shared)if(BuildingGeometry.DistanceToSegment(mid,s.Start,s.End)<.02f){var other=byId[s.A==b.Id?s.B:s.A];lower=Math.Max(lower,other.BaseY+other.WallHeight);}
                    int divisions=Math.Max(1,(int)Math.Ceiling(p.DistanceTo(q)/2));
                    for(int step=0;step<divisions;step++){
                        var x=p.Lerp(q,(float)step/divisions);var y=p.Lerp(q,(float)(step+1)/divisions);data.Terrain.TryHeightAt(x,out float gx);data.Terrain.TryHeightAt(y,out float gy);
                        float bottomX=Math.Max(gx-.015f,lower),bottomY=Math.Max(gy-.015f,lower);
                        if(top>Math.Max(bottomX,bottomY)+.001f)Quad(V(x,bottomX),V(y,bottomY),V(y,top),V(x,top),new(normal.X,0,normal.Y),wall,b.Id,"wall");
                    }
                }
            }
            var tangent=(b.Corners[1]-b.Corners[0]).Normalized();var inward=b.Inward;
            float width=b.Corners[0].DistanceTo(b.Corners[1]),depth=b.Corners[0].DistanceTo(b.Corners[3]);float e=b.RoofProjection.Area-b.Footprint.Area>.001?.25f:0;
            var corners=BuildingGeometry.Rectangle(b.Corners[0]-(tangent+inward)*e,tangent,inward,width+e*2,depth+e*2);
            var r=corners.Select(p=>V(p,top)).ToArray();float ridge=top+b.RoofHeight;
            void RoofQuad(Vector3 a,Vector3 z,Vector3 c,Vector3 d){var n=(z-a).Cross(c-a).Normalized();if(n.Y<0)n=-n;Quad(a,z,c,d,n,roof,b.Id,"roof");}
            if(b.RidgeAlongDepth){
                var front=V((corners[0]+corners[1])/2,ridge);var back=V((corners[2]+corners[3])/2,ridge);
                RoofQuad(r[0],r[3],back,front);RoofQuad(r[1],front,back,r[2]);
                Triangle(V(b.Corners[0],top),V((b.Corners[0]+b.Corners[1])/2,ridge),V(b.Corners[1],top),new(-inward.X,0,-inward.Y),wall,b.Id,"gable");
                Triangle(V(b.Corners[3],top),V(b.Corners[2],top),V((b.Corners[2]+b.Corners[3])/2,ridge),new(inward.X,0,inward.Y),wall,b.Id,"gable");
            }else{
                var left=V((corners[0]+corners[3])/2,ridge);var right=V((corners[1]+corners[2])/2,ridge);
                RoofQuad(r[0],left,right,r[1]);RoofQuad(r[3],r[2],right,left);
                Triangle(V(b.Corners[0],top),V(b.Corners[3],top),V((b.Corners[0]+b.Corners[3])/2,ridge),new(-tangent.X,0,-tangent.Y),wall,b.Id,"gable");
                Triangle(V(b.Corners[1],top),V((b.Corners[1]+b.Corners[2])/2,ridge),V(b.Corners[2],top),new(tangent.X,0,tangent.Y),wall,b.Id,"gable");
            }
            var doorCenter=b.Door-inward*.012f;var doorWidth=tangent*Math.Min(.5f,width*.2f);float doorTop=b.BaseY+Math.Min(2.1f,b.WallHeight*.8f);
            Quad(V(doorCenter-doorWidth,b.BaseY),V(doorCenter+doorWidth,b.BaseY),V(doorCenter+doorWidth,doorTop),V(doorCenter-doorWidth,doorTop),new(-inward.X,0,-inward.Y),new Color("6a5b48"),b.Id,"door");
            // Front steps are explicit geometry within the validated entry corridor.
            var stairStart=b.PrecinctId!=null?b.Door-inward*(data.Options.PassageWidth+1):b.StreetPoint;
            data.Terrain.TryHeightAt(stairStart,out float streetY);float rise=b.BaseY-streetY;float length=b.Door.DistanceTo(stairStart);
            if(length>.1f&&rise>.1f){int steps=Math.Max(1,(int)Math.Ceiling(rise/.18f));var right=tangent*data.Options.PassageWidth/2;
                for(int i=0;i<steps;i++){var a=stairStart.Lerp(b.Door,(float)i/steps);var z=stairStart.Lerp(b.Door,(float)(i+1)/steps);float y=streetY+rise*(i+1)/steps;
                    Quad(V(a-right,y),V(a+right,y),V(z+right,y),V(z-right,y),Vector3.Up,wall.Darkened(.15f),b.Id,"steps");
                    float previousY=streetY+rise*i/steps;
                    Quad(V(a-right,previousY),V(a+right,previousY),V(a+right,y),V(a-right,y),new(-inward.X,0,-inward.Y),wall.Darkened(.2f),b.Id,"steps");}
            }
        }
        return new(vertices,normals,colors,owners,parts);
    }
}
