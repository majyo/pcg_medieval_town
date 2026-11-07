using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

public static class RoadNavigation
{
    public static NavigationMesh Mesh(RoadSceneData data)
    {
        var vertices=new List<Vector3>();var indices=new Dictionary<(int,int),int>();var faces=new List<int[]>();
        int Vertex(Vector2 p){var key=((int)MathF.Round(p.X*100000),(int)MathF.Round(p.Y*100000));
            if(indices.TryGetValue(key,out int id))return id;id=vertices.Count;indices[key]=id;if(!data.Grading.TryDesignHeightNear(p,.03f,out float h))throw new InvalidOperationException("导航顶点离开道路设计面");vertices.Add(new(p.X,h,p.Y));return id;}
        // Navigation needs a conforming mesh, not the independently refined material
        // patches. Simplify sub-centimetre boolean artifacts, then bisect long edges.
        // A shared edge is split at the same dyadic positions on both adjacent faces.
        var footprint=new ParcelRegion(Clipper.SimplifyPaths(data.Area.CopyPaths(),.02*ParcelGeometry.Scale));
        void Triangle(Vector2 a,Vector2 b,Vector2 c,int depth){
            float ab=a.DistanceSquaredTo(b),bc=b.DistanceSquaredTo(c),ca=c.DistanceSquaredTo(a);float longest=Math.Max(ab,Math.Max(bc,ca));
            if(longest>16&&depth<24){if(longest==ab){var m=(a+b)/2;Triangle(a,m,c,depth+1);Triangle(m,b,c,depth+1);}else if(longest==bc){var m=(b+c)/2;Triangle(a,b,m,depth+1);Triangle(a,m,c,depth+1);}else{var m=(c+a)/2;Triangle(a,b,m,depth+1);Triangle(m,b,c,depth+1);}return;}
            var face=new[]{Vertex(a),Vertex(b),Vertex(c)};if(face.Distinct().Count()<3||Math.Abs((b-a).Cross(c-a))<.000002f)return;if((b-a).Cross(c-a)<0)Array.Reverse(face);faces.Add(face);
        }
        var triangles=footprint.Triangles;for(int i=0;i<triangles.Count;i+=3)Triangle(triangles[i],triangles[i+1],triangles[i+2],0);
        // Close T junctions introduced by source triangulation and independent edge
        // bisection. Each polygon edge explicitly includes every vertex on that edge.
        var bins=new Dictionary<(int,int),List<int>>();
        for(int i=0;i<vertices.Count;i++){var p=vertices[i];var key=((int)MathF.Floor(p.X/4),(int)MathF.Floor(p.Z/4));if(!bins.TryGetValue(key,out var list))bins[key]=list=new();list.Add(i);}
        var edges=new Dictionary<(int,int),int[]>();
        int[] Edge(int a,int b){bool reverse=a>b;var key=reverse?(b,a):(a,b);if(!edges.TryGetValue(key,out var result)){
                var p=new Vector2(vertices[key.Item1].X,vertices[key.Item1].Z);var q=new Vector2(vertices[key.Item2].X,vertices[key.Item2].Z);var d=q-p;float length=d.LengthSquared();var found=new List<(float T,int Id)>{(0,key.Item1),(1,key.Item2)};
                for(int z=(int)MathF.Floor(Math.Min(p.Y,q.Y)/4);z<=(int)MathF.Floor(Math.Max(p.Y,q.Y)/4);z++)for(int x=(int)MathF.Floor(Math.Min(p.X,q.X)/4);x<=(int)MathF.Floor(Math.Max(p.X,q.X)/4);x++)if(bins.TryGetValue((x,z),out var candidates))foreach(int id in candidates){if(id==key.Item1||id==key.Item2)continue;var v=new Vector2(vertices[id].X,vertices[id].Z);float t=(v-p).Dot(d)/length;if(t>.00001f&&t<.99999f&&Math.Abs((v-p).Cross(d))<.00015f*MathF.Sqrt(length))found.Add((t,id));}
                edges[key]=result=found.OrderBy(x=>x.T).Select(x=>x.Id).ToArray();}
            return reverse?result.Reverse().ToArray():result;}
        var mesh=new NavigationMesh{CellSize=.01f,CellHeight=.01f,AgentRadius=.3f};mesh.SetVertices(vertices.ToArray());
        foreach(var face in faces){var polygon=new List<int>();for(int i=0;i<face.Length;i++){var edge=Edge(face[i],face[(i+1)%face.Length]);polygon.AddRange(edge.Take(edge.Length-1));}mesh.AddPolygon(polygon.Distinct().ToArray());}
        return mesh;
    }
    public static Node3D Build(RoadSceneData data,BuildingSet? buildings)
    {
        var root=new TownNavigation{Name="Navigation"};root.AddChild(new NavigationRegion3D{Name="RoadNetwork",NavigationMesh=Mesh(data),UseEdgeConnections=false});
        if(buildings!=null){root.SetMeta("door_positions",buildings.Buildings.Select(b=>new Vector3(b.Door.X,b.BaseY,b.Door.Y)).ToArray());root.SetMeta("door_ids",buildings.Buildings.Select(b=>b.Id).ToArray());}
        if(buildings!=null)foreach(var building in buildings.Buildings){
            var street=building.StreetPoint;var door=building.Door;var start=new Vector3(street.X,data.Grading.Graded!.HeightAt(street),street.Y);var end=new Vector3(door.X,building.BaseY,door.Y);
            // Door pads make both ends of a link belong to navigable polygons.
            var pad=new NavigationMesh();float r=.32f;pad.SetVertices(new[]{end+new Vector3(-r,0,-r),end+new Vector3(r,0,-r),end+new Vector3(r,0,r),end+new Vector3(-r,0,r)});pad.AddPolygon(new[]{0,1,2,3});
            root.AddChild(new NavigationRegion3D{Name=$"Door_{building.Id}",NavigationMesh=pad,UseEdgeConnections=false});
            root.AddChild(new NavigationLink3D{Name=$"Access_{building.Id}",StartPosition=start,EndPosition=end,Bidirectional=true});
        }
        return root;
    }
}
