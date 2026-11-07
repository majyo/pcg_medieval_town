using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PcgCitySandbox.Pcg;

public sealed record SurfaceEarthworkAudit(int Samples,float SampleSpacing,float MaxCut,float MaxFill,
    int Violations,Vector2 WorstPosition,int? WorstRoadId,float WorstExcess);

internal static class GradingEarthworkLimits
{
    public const float Tolerance=.001f;
    public static IEnumerable<Vector2> Samples(ITerrainSurface terrain,ParcelRegion region)
    {
        // A plane minus native triangular terrain is affine on each clipped face:
        // its extrema occur at these vertices, including hole and boundary cuts.
        if(terrain is NativeTerrainSurface native)
            return CityGroundMesh.Build(native.Map,new[]{("constraint","ground",region)},terrain).Positions
                .Select(p=>new Vector2(p.X,p.Z)).Distinct().ToArray();
        var points=new HashSet<Vector2>(region.Rings.SelectMany(r=>r));
        foreach(var polygon in RoadSceneBuilder.GridPolygons(region,.5f))foreach(var p in polygon)points.Add(p);
        return points.OrderBy(p=>p.Y).ThenBy(p=>p.X).ToArray();
    }

    public static GradePlane ConstrainPlane(GradePlane proposed,ITerrainSurface terrain,ParcelRegion region,
        float slope,RoadGradingOptions options,CancellationToken token)
    {
        var points=Samples(terrain,region).ToArray();if(points.Length==0)return proposed;
        float scale=Math.Max(1,region.Bounds.Size.Length());
        var normals=points.Select(p=>new Vector3(1,(p.X-proposed.Origin.X)/scale,(p.Y-proposed.Origin.Y)/scale)).ToArray();
        var ground=points.Select(terrain.HeightAt).ToArray();
        var corrections=new Vector3[points.Length];Vector3 slopeCorrection=Vector3.Zero;
        var x=new Vector3(proposed.Height,proposed.Gradient.X*scale,proposed.Gradient.Y*scale);
        float violation=float.PositiveInfinity;int worst=0;
        for(int iteration=0;iteration<5000;iteration++){
            if(iteration%32==0)token.ThrowIfCancellationRequested();
            for(int i=0;i<points.Length;i++){
                var y=x+corrections[i];float h=normals[i].Dot(y);
                float bounded=Math.Clamp(h,ground[i]-options.MaxCut,ground[i]+options.MaxFill);
                var next=y+normals[i]*((bounded-h)/normals[i].LengthSquared());corrections[i]=y-next;x=next;
            }
            var candidate=x+slopeCorrection;var gradient=new Vector2(candidate.Y,candidate.Z);
            if(gradient.Length()>slope*scale)gradient=gradient.Normalized()*slope*scale;
            var projected=new Vector3(candidate.X,gradient.X,gradient.Y);slopeCorrection=candidate-projected;x=projected;
            violation=0;
            for(int i=0;i<points.Length;i++){float delta=normals[i].Dot(x)-ground[i];float v=Math.Max(-delta-options.MaxCut,delta-options.MaxFill);if(v>violation){violation=v;worst=i;}}
            if(violation<.0001f)return proposed with{Height=x.X,Gradient=new(x.Y/scale,x.Z/scale)};
        }
        throw new RoadGradingFailure(points[worst],null,$"{proposed.Id} 平面无法同时满足坡度及完整范围挖填上限（残差 {violation:0.000} m）");
    }

    public static void CrossSectionBounds(Centerline line,float station,float width,RoadCrossSection section,
        ITerrainSurface terrain,RoadGradingOptions options,out double low,out double high)
    {
        low=double.NegativeInfinity;high=double.PositiveInfinity;
        var center=line.PointAt(station);var normal=line.NormalAt(station);int n=Math.Max(2,(int)Math.Ceiling(width/.5f));
        for(int i=0;i<=n;i++){
            float lateral=width*((float)i/n-.5f);var point=center+normal*lateral;if(!terrain.Contains(point))continue;
            float offset=Math.Abs(lateral)*options.Crossfall/100*(section==RoadCrossSection.Crown?-1:1);float h=terrain.HeightAt(point);
            low=Math.Max(low,h-options.MaxCut-offset);high=Math.Min(high,h+options.MaxFill-offset);
        }
        // Include the crown/kennel, even for an odd number of transverse intervals.
        float native=terrain.HeightAt(center);low=Math.Max(low,native-options.MaxCut);high=Math.Min(high,native+options.MaxFill);
    }

    public static SurfaceEarthworkAudit Audit(RoadGrading design,CancellationToken token)
    {
        const float spacing=.25f;int samples=0,violations=0;float cut=0,fill=0,worst=0;Vector2 position=default;int? owner=null;
        var seen=new HashSet<Vector2>();
        void Probe(Vector2 p,int? road){if(!seen.Add(p))return;token.ThrowIfCancellationRequested();
            if(!design.TryDesignHeightNear(p,.001f,out float h))throw new RoadGradingFailure(p,road,"设计面在占地边界缺少高程");
            float d=h-design.GroundHeight(p);samples++;cut=Math.Max(cut,-d);fill=Math.Max(fill,d);
            float excess=Math.Max(-d-design.Options.MaxCut,d-design.Options.MaxFill);if(excess>Tolerance)violations++;
            if(excess>worst){worst=excess;position=p;owner=road;}}
        var plaza=ParcelGeometry.Rect(design.PlazaRect);
        var areas=design.Roads.Geometry.Surfaces.Select(s=>(s.Shape,Id:(int?)s.RoadId)).Append((new ParcelRegion(plaza),null));
        foreach(var (shape,id) in areas){foreach(var p in shape.Rings.SelectMany(r=>r))Probe(p,id);
            foreach(var polygon in RoadSceneBuilder.GridPolygons(shape,spacing,token)){
                foreach(var p in polygon)Probe(p,id);Probe(polygon.Aggregate(Vector2.Zero,(a,b)=>a+b)/polygon.Count,id);
            }}
        return new(samples,spacing,cut,fill,violations,position,owner,worst);
    }
}
