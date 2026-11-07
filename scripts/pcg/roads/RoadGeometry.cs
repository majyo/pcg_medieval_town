using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
namespace PcgCitySandbox.Pcg;

public sealed record RoadSurface(int Id,int RoadId,int? NodeId,ParcelRegion Shape);
public sealed record RoadBoundary(int Id,int RoadId,int? NodeId,Vector2 A,Vector2 B,bool AllowsFrontage);
public sealed record RoadGeometryNotice(int RoadId,Vector2 Position,string Reason);

// The canonical planar road space. Rendering and land subdivision consume the same
// area; surface pieces have disjoint interiors and boundaries carry provenance.
public sealed class RoadGeometry
{
    public ParcelRegion Area {get;}
    public ReadOnlyCollection<RoadSurface> Surfaces {get;}
    public ReadOnlyCollection<RoadBoundary> Boundaries {get;}
    public ReadOnlyCollection<RoadEdge> PlannedEdges {get;}
    public ReadOnlyCollection<RoadGeometryNotice> Notices {get;}
    public int SmoothedCorners {get;}
    public int JunctionCount=>Surfaces.Count(s=>s.NodeId.HasValue);
    internal RoadGeometry(Paths64 area,List<RoadSurface> surfaces,List<RoadBoundary> boundaries,
        IEnumerable<RoadEdge> planned,int smoothed,List<RoadGeometryNotice> notices)
    {Area=new(area);Surfaces=Array.AsReadOnly(surfaces.ToArray());Boundaries=Array.AsReadOnly(boundaries.ToArray());
        PlannedEdges=Array.AsReadOnly(planned.ToArray());SmoothedCorners=smoothed;Notices=Array.AsReadOnly(notices.ToArray());}

    public RoadBoundary? FrontageAt(Vector2 a,Vector2 b)=>Boundaries.FirstOrDefault(f=>f.AllowsFrontage&&Covers(f.A,f.B,a,b));
    public IEnumerable<(Vector2 A,Vector2 B,RoadBoundary Source)> FrontagesOn(Vector2 a,Vector2 b)
    {
        var d=b-a;float length=d.Length();if(length<.0001f)yield break;var t=d/length;
        foreach(var f in Boundaries.Where(f=>f.AllowsFrontage)){
            if(Math.Abs((f.A-a).Cross(t))>.003f||Math.Abs((f.B-a).Cross(t))>.003f)continue;
            float lo=Math.Max(0,Math.Min((f.A-a).Dot(t),(f.B-a).Dot(t))),hi=Math.Min(length,Math.Max((f.A-a).Dot(t),(f.B-a).Dot(t)));
            if(hi-lo>.001f)yield return(a+t*lo,a+t*hi,f);
        }
    }
    internal static bool Covers(Vector2 a,Vector2 b,Vector2 p,Vector2 q,float tolerance=.003f)
    {
        var d=b-a;float length=d.Length();if(length<.00001f)return false;var t=d/length;
        return Math.Abs((p-a).Cross(t))<=tolerance&&Math.Abs((q-a).Cross(t))<=tolerance&&
            (p-a).Dot(t)>=-tolerance&&(q-a).Dot(t)>=-tolerance&&(p-a).Dot(t)<=length+tolerance&&(q-a).Dot(t)<=length+tolerance;
    }

    internal sealed record Patch(int RoadId,int? NodeId,Paths64 Paths);
    internal static RoadGeometry Assemble(IEnumerable<Patch> source,IReadOnlyList<RoadEdge> edges,IEnumerable<RoadEdge> planned,
        int smoothed,List<RoadGeometryNotice> notices)
    {
        var patches=source.ToArray();
        if(patches.Sum(p=>p.Paths.Sum(r=>r.Count))>20000)throw new InvalidOperationException("道路轮廓预算超限，请增大街巷间距");
        var union=ParcelGeometry.Union(new Paths64(patches.SelectMany(p=>p.Paths)));
        var surfaces=new List<RoadSurface>();var claimed=new Paths64();
        foreach(var patch in patches){
            var free=ParcelGeometry.Difference(patch.Paths,claimed);if(ParcelGeometry.Area(free)>.00001)surfaces.Add(new(surfaces.Count,patch.RoadId,patch.NodeId,new(free)));
            claimed=ParcelGeometry.Union(new Paths64(claimed.Concat(patch.Paths)));
        }
        var segments=patches.SelectMany(p=>p.Paths.SelectMany(r=>Enumerable.Range(0,r.Count).Select(i=>(Patch:p,A:ParcelGeometry.V(r[i]),B:ParcelGeometry.V(r[(i+1)%r.Count]))))).ToArray();
        var byId=edges.ToDictionary(e=>e.Id);var boundaries=new List<RoadBoundary>();
        foreach(var ring in union)for(int i=0;i<ring.Count;i++){
            var a=ParcelGeometry.V(ring[i]);var b=ParcelGeometry.V(ring[(i+1)%ring.Count]);var d=b-a;float length=d.Length();if(length<.0001f)continue;var tangent=d/length;
            // Union can merge collinear edges from several owners. Split at their
            // original vertices before assigning a source; never guess by proximity.
            var cuts=new SortedSet<float>{0,length};
            foreach(var s in segments)foreach(var p in new[]{s.A,s.B})if(Math.Abs((p-a).Cross(tangent))<.001f){float t=(p-a).Dot(tangent);if(t>.001f&&t<length-.001f)cuts.Add(t);}
            var values=cuts.ToArray();for(int j=1;j<values.Length;j++){
                var p=a+tangent*values[j-1];var q=a+tangent*values[j];
                var owner=segments.FirstOrDefault(s=>Covers(s.A,s.B,p,q));
                if(owner.Patch==null)throw new InvalidOperationException("道路边界缺少来源");
                var road=byId[owner.Patch.RoadId];var mid=(p+q)/2;
                int nearest=Enumerable.Range(1,road.Points.Count-1).OrderBy(k=>Distance(mid,road.Points[k-1],road.Points[k])).First();
                var direction=(road.Points[nearest]-road.Points[nearest-1]).Normalized();
                bool frontage=!owner.Patch.NodeId.HasValue&&Math.Abs(tangent.Dot(direction))>.85f;
                var next=new RoadBoundary(boundaries.Count,road.Id,owner.Patch.NodeId,p,q,frontage);
                if(boundaries.LastOrDefault() is {} last&&last.RoadId==next.RoadId&&last.NodeId==next.NodeId&&last.AllowsFrontage==next.AllowsFrontage&&last.B.DistanceTo(next.A)<.001f&&
                    (last.B-last.A).Normalized().Dot((next.B-next.A).Normalized())>.999999f)boundaries[^1]=last with{B=next.B};
                else boundaries.Add(next);
            }
        }
        return new(union,surfaces,boundaries,planned,smoothed,notices);
    }
    internal static float Distance(Vector2 p,Vector2 a,Vector2 b){var d=b-a;return p.DistanceTo(a+d*Math.Clamp((p-a).Dot(d)/Math.Max(d.LengthSquared(),1e-10f),0,1));}
    internal static RoadGeometry FromLegacy(List<Vector2[]> polygons,IReadOnlyList<RoadEdge> roads)
    {
        var area=ParcelGeometry.Union(new Paths64(polygons.Select(p=>ParcelGeometry.Path(p))));
        var patches=new List<Patch>();foreach(var e in roads){var strips=new Paths64();for(int i=1;i<e.Points.Count;i++)strips.Add(ParcelGeometry.Path(RoadSpace.Strip(e.Points[i-1],e.Points[i],e.Width)));
            patches.Add(new(e.Id,null,ParcelGeometry.Intersection(area,ParcelGeometry.Union(strips))));}
        var remainder=ParcelGeometry.Difference(area,ParcelGeometry.Union(new Paths64(patches.SelectMany(p=>p.Paths))));
        if(ParcelGeometry.Area(remainder)>.00001)patches.Add(new(roads[0].Id,roads[0].From,remainder));
        return Assemble(patches,roads,roads,0,new());
    }
}
