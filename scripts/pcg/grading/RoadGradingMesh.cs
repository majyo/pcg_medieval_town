using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
namespace PcgCitySandbox.Pcg;

// Preview tessellation of the design surface: road pieces and the plaza, split into
// triangles no longer than MaxEdge so the cross-section and blends are visible.
public sealed class RoadGradingMesh
{
    public const float MaxEdge=2;
    public ReadOnlyCollection<Vector3> Positions {get;}
    public ReadOnlyCollection<float> Ground {get;}
    // Owning road id per vertex; -1 for the plaza.
    public ReadOnlyCollection<int> Roads {get;}
    private RoadGradingMesh(List<Vector3> p,List<float> g,List<int> r){Positions=Array.AsReadOnly(p.ToArray());Ground=Array.AsReadOnly(g.ToArray());Roads=Array.AsReadOnly(r.ToArray());}
    public float Delta(int i)=>Positions[i].Y-Ground[i];
    public static RoadGradingMesh Build(RoadGrading g)
    {
        var positions=new List<Vector3>();var ground=new List<float>();var roads=new List<int>();var cache=new Dictionary<(long,long),(float Design,float Ground)>();
        (float,float) Heights(Vector2 p){var key=((long)Math.Round(p.X*1000),(long)Math.Round(p.Y*1000));
            if(!cache.TryGetValue(key,out var h)){float t=g.GroundHeight(p);h=(g.TryDesignHeight(p,out float d)?d:t,t);cache[key]=h;}return h;}
        void Emit(Vector2 a,Vector2 b,Vector2 c,int road,int depth){
            float ab=a.DistanceSquaredTo(b),bc=b.DistanceSquaredTo(c),ca=c.DistanceSquaredTo(a);float longest=Math.Max(ab,Math.Max(bc,ca));
            if(longest>MaxEdge*MaxEdge&&depth<14){
                if(longest==ab){var m=(a+b)/2;Emit(a,m,c,road,depth+1);Emit(m,b,c,road,depth+1);}
                else if(longest==bc){var m=(b+c)/2;Emit(a,b,m,road,depth+1);Emit(a,m,c,road,depth+1);}
                else{var m=(c+a)/2;Emit(a,b,m,road,depth+1);Emit(m,b,c,road,depth+1);}
                return;}
            if((b-a).Cross(c-a)<0)(b,c)=(c,b);
            foreach(var p in new[]{a,b,c}){var (d,t)=Heights(p);positions.Add(new(p.X,d,p.Y));ground.Add(t);roads.Add(road);}
        }
        void Region(ParcelRegion region,int road){var tri=region.Triangles;for(int i=0;i+2<tri.Count;i+=3)Emit(tri[i],tri[i+1],tri[i+2],road,0);}
        var plaza=new ParcelRegion(ParcelGeometry.Rect(g.PlazaRect));
        foreach(var s in g.Roads.Geometry.Surfaces){var outside=ParcelGeometry.Difference(s.Shape.CopyPaths(),plaza.CopyPaths());if(ParcelGeometry.Area(outside)>.0001)Region(new ParcelRegion(outside),s.RoadId);}
        Region(plaza,-1);
        return new(positions,ground,roads);
    }
}
