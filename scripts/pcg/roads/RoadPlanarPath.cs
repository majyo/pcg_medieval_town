using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

// Authoritative analytic centerline produced by 03. Points remain the tessellated
// footprint representation; 03B uses this path's true tangent and arc length.
public sealed class RoadPlanarPath
{
    public sealed class Span
    {
        public Vector2 A {get;} public Vector2 B {get;} public Vector2 C {get;}
        public bool Curved {get;}
        private readonly double[] _arc;
        public float Length=>(float)_arc[^1];
        public Span(Vector2 a,Vector2 b,Vector2 c,bool curved){A=a;B=b;C=c;Curved=curved;_arc=new double[129];
            for(int i=1;i<_arc.Length;i++)_arc[i]=_arc[i-1]+Integrate((i-1)/128d,i/128d);}
        public Vector2 Point(double t)=>Curved?new((float)(A.X*(1-t)*(1-t)+B.X*2*t*(1-t)+C.X*t*t),(float)(A.Y*(1-t)*(1-t)+B.Y*2*t*(1-t)+C.Y*t*t)):new((float)(A.X+(C.X-A.X)*t),(float)(A.Y+(C.Y-A.Y)*t));
        public Vector2 Tangent(double t)=>(Curved?(B-A)*(float)(2*(1-t))+(C-B)*(float)(2*t):C-A).Normalized();
        private double Speed(double t){double x=Curved?2*((B.X-A.X)*(1-t)+(C.X-B.X)*t):C.X-A.X,y=Curved?2*((B.Y-A.Y)*(1-t)+(C.Y-B.Y)*t):C.Y-A.Y;return Math.Sqrt(x*x+y*y);}
        private double Integrate(double a,double b){double m=(a+b)/2,h=(b-a)/2,d=h*Math.Sqrt(3d/5);return h*(5d/9*Speed(m-d)+8d/9*Speed(m)+5d/9*Speed(m+d));}
        public float ArcAt(double t){t=Math.Clamp(t,0,1);int i=Math.Min(127,(int)(t*128));return (float)(_arc[i]+Integrate(i/128d,t));}
        public double Parameter(float s){s=Math.Clamp(s,0,Length);if(!Curved)return s/Math.Max(.000001f,Length);int i=Array.BinarySearch(_arc,(double)s);if(i<0)i=~i-1;i=Math.Clamp(i,0,127);double lo=i/128d,hi=(i+1)/128d;
            for(int k=0;k<16;k++){double m=(lo+hi)/2;if(_arc[i]+Integrate(i/128d,m)<s)lo=m;else hi=m;}return(lo+hi)/2;}
        public IEnumerable<double> NormalRoots(Vector2 p)
        {
            double vx=p.X-A.X,vy=p.Y-A.Y,bx=Curved?2*(B.X-A.X):C.X-A.X,by=Curved?2*(B.Y-A.Y):C.Y-A.Y;
            double cx=Curved?(double)A.X-2d*B.X+C.X:0,cy=Curved?(double)A.Y-2d*B.Y+C.Y:0;
            double a=-2*(cx*cx+cy*cy),b=-3*(bx*cx+by*cy),c=2*(vx*cx+vy*cy)-bx*bx-by*by,d=vx*bx+vy*by;
            double F(double t)=>((a*t+b)*t+c)*t+d;
            var cuts=new List<double>{0,1};
            if(Math.Abs(a)<1e-15){if(Math.Abs(b)>1e-15){double t=-c/(2*b);if(t>0&&t<1)cuts.Add(t);}}
            else{double discriminant=4*b*b-12*a*c;if(discriminant>=0)foreach(double t in new[]{(-2*b-Math.Sqrt(discriminant))/(6*a),(-2*b+Math.Sqrt(discriminant))/(6*a)})if(t>0&&t<1)cuts.Add(t);}
            cuts.Sort();var roots=new List<double>();
            for(int i=0;i<cuts.Count;i++){double x=cuts[i];if(Math.Abs(F(x))<1e-9)roots.Add(x);if(i+1==cuts.Count)continue;double y=cuts[i+1],fx=F(x),fy=F(y);if(fx*fy>=0)continue;
                for(int k=0;k<40;k++){double mid=(x+y)/2,fm=F(mid);if(fx*fm<=0){y=mid;fy=fm;}else{x=mid;fx=fm;}}roots.Add((x+y)/2);}
            return roots.Distinct();
        }
    }
    public IReadOnlyList<Span> Spans {get;}
    private readonly float[] _stations;
    public float Length=>_stations[^1];
    public RoadPlanarPath(IEnumerable<Span> spans){Spans=Array.AsReadOnly(spans.Where(s=>s.Length>.00001f).ToArray());_stations=new float[Spans.Count+1];for(int i=0;i<Spans.Count;i++)_stations[i+1]=_stations[i]+Spans[i].Length;}
    public static RoadPlanarPath Polyline(IReadOnlyList<Vector2> points)=>new(Enumerable.Range(1,points.Count-1).Select(i=>new Span(points[i-1],points[i-1],points[i],false)));
    public (Vector2 Point,Vector2 Tangent) At(float station){station=Math.Clamp(station,0,Length);int i=Array.BinarySearch(_stations,station);if(i<0)i=~i-1;i=Math.Clamp(i,0,Spans.Count-1);double t=Spans[i].Parameter(station-_stations[i]);return(Spans[i].Point(t),Spans[i].Tangent(t));}
    public (float Station,float Offset,int Branches) Locate(Vector2 p,float halfWidth=float.PositiveInfinity)
    {
        var hits=new List<(float Station,float Offset)>();
        for(int i=0;i<Spans.Count;i++){var span=Spans[i];var roots=span.NormalRoots(p).ToList();
            // Shared analytic joins are stored as float coordinates. Accept the
            // endpoint normal within 0.2 mm so round-off cannot leave a tiny gap.
            foreach(double endpoint in new[]{0d,1d})if(Math.Abs((p-span.Point(endpoint)).Dot(span.Tangent(endpoint)))<.0002f)roots.Add(endpoint);
            foreach(double t in roots){var q=span.Point(t);var tangent=span.Tangent(t);float offset=(p-q).Dot(new(-tangent.Y,tangent.X));if(Math.Abs(offset)>halfWidth+.0001f)continue;
                float s=_stations[i]+span.ArcAt(t);if(!hits.Any(h=>Math.Abs(h.Station-s)<.001f))hits.Add((s,offset));}}
        if(hits.Count==0){float closest=float.PositiveInfinity,station=0,offset=0;for(int i=0;i<Spans.Count;i++)foreach(double t in new[]{0d,1d}){var q=Spans[i].Point(t);float distance=p.DistanceSquaredTo(q);if(distance<closest){closest=distance;station=_stations[i]+Spans[i].ArcAt(t);var tangent=Spans[i].Tangent(t);offset=(p-q).Dot(new(-tangent.Y,tangent.X));}}return(station,offset,0);}
        var hit=hits.OrderBy(h=>Math.Abs(h.Offset)).ThenBy(h=>h.Station).First();return(hit.Station,hit.Offset,hits.Count);
    }
}
