using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

// Invert a ruled strip with a continuous transverse frame. Unlike Gaussian height
// averaging this preserves each specified straight cross-section exactly. A folded
// strip is reported explicitly; it has no unique height without a planning repair.
internal sealed class RoadSweepFrame
{
    private readonly Vector2[] _points,_normals;
    private readonly float[] _lengths;
    public float Length=>_lengths[^1];
    public RoadSweepFrame(IReadOnlyList<Vector2> points)
    {
        _points=points.ToArray();_lengths=new float[points.Count];_normals=new Vector2[points.Count];
        for(int i=1;i<points.Count;i++)_lengths[i]=_lengths[i-1]+points[i].DistanceTo(points[i-1]);
        for(int i=0;i<points.Count;i++){
            var before=i>0?(points[i]-points[i-1]).Normalized():(points[1]-points[0]).Normalized();
            var after=i+1<points.Count?(points[i+1]-points[i]).Normalized():before;
            var tangent=(before+after).Normalized();if(tangent.LengthSquared()<.5f)tangent=before;
            _normals[i]=new(-tangent.Y,tangent.X);
        }
    }
    private int Segment(float station){int i=Array.BinarySearch(_lengths,Math.Clamp(station,0,Length));if(i<0)i=~i-1;return Math.Clamp(i,0,_points.Length-2);}
    public (Vector2 Center,Vector2 Normal) At(float station)
    {
        int i=Segment(station);float u=Math.Clamp((station-_lengths[i])/Math.Max(.00001f,_lengths[i+1]-_lengths[i]),0,1);
        return (_points[i].Lerp(_points[i+1],u),_normals[i].Lerp(_normals[i+1],u).Normalized());
    }
    public (float Station,float Offset,int Branches) Locate(Vector2 point,float halfWidth)
    {
        var hits=new List<(float Station,float Offset)>();
        for(int i=0;i<_points.Length-1;i++){
            var d=_points[i+1]-_points[i];var v=point-_points[i];var n=_normals[i];var dn=_normals[i+1]-n;
            double Cross(Vector2 a,Vector2 b)=>(double)a.X*b.Y-(double)a.Y*b.X;
            double a=-Cross(d,dn),b=Cross(v,dn)-Cross(d,n),c=Cross(v,n);
            void Root(double u){if(u<-.00001||u>1.00001)return;u=Math.Clamp(u,0,1);var center=_points[i]+d*(float)u;var normal=(n+dn*(float)u).Normalized();float offset=(point-center).Dot(normal);
                if(Math.Abs(offset)>halfWidth+.001f||Math.Abs((point-center).Cross(normal))>.001f)return;
                float station=Mathf.Lerp(_lengths[i],_lengths[i+1],(float)u);
                if(!hits.Any(h=>Math.Abs(h.Station-station)<.001f))hits.Add((station,offset));}
            if(Math.Abs(a)<1e-12){if(Math.Abs(b)>1e-12)Root(-c/b);}
            else{double discriminant=b*b-4*a*c;if(discriminant<0)continue;double root=Math.Sqrt(discriminant);double q=-.5*(b+Math.CopySign(root,b));
                if(Math.Abs(q)<1e-14)Root(-b/(2*a));else{Root(q/a);Root(c/q);}}
        }
        if(hits.Count==0)return(float.NaN,float.NaN,0);
        var nearest=hits.OrderBy(h=>Math.Abs(h.Offset)).ThenBy(h=>h.Station).First();return(nearest.Station,nearest.Offset,hits.Count);
    }
}
