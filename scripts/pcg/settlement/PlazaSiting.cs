using Godot;
using System;
using System.Collections.Generic;

namespace PcgCitySandbox.Pcg;

// Upstream preference based only on the declared planning environment. This is a
// conservative default-design lookahead, not a replacement for the native-terrain
// and real-route feasibility checks in 03B. Manual locations are never moved.
internal static class PlazaSiting
{
    public static float Risk(CityEnvironmentData environment,Vector2 center,float size)
    {
        const float cut=1.25f,fill=.75f,slope=.02f,curvature=.002f,scale=64;
        var rows=new List<(Vector3 N,float Low,float High)>();
        void Add(Vector2 p,float allowance){if(!environment.TryHeightAt(p,out float h))return;var delta=(p-center)/scale;rows.Add((new(1,delta.X,delta.Y),h-cut-allowance,h+fill+allowance));}
        float half=size/2;
        for(int z=0;z<=4;z++)for(int x=0;x<=4;x++)Add(center+new Vector2((x/4f-.5f)*size,(z/4f-.5f)*size),0);
        for(int direction=0;direction<8;direction++){
            var d=Vector2.FromAngle(direction*Mathf.Tau/8);float toBoundary=half/Math.Max(Math.Abs(d.X),Math.Abs(d.Y));
            for(float beyond=5;beyond<=50;beyond+=5)Add(center+d*(toBoundary+beyond),.5f*curvature*beyond*beyond);
        }
        environment.TryHeightAt(center,out float ground);var value=new Vector3(ground,0,0);var correction=new Vector3[rows.Count];Vector3 slopeCorrection=Vector3.Zero;
        float residual=0;
        for(int pass=0;pass<160;pass++){
            for(int i=0;i<rows.Count;i++){var row=rows[i];var prior=value+correction[i];float h=row.N.Dot(prior);var next=prior+row.N*((Math.Clamp(h,row.Low,row.High)-h)/row.N.LengthSquared());correction[i]=prior-next;value=next;}
            var candidate=value+slopeCorrection;var gradient=new Vector2(candidate.Y,candidate.Z);if(gradient.Length()>slope*scale)gradient=gradient.Normalized()*(slope*scale);
            var projected=new Vector3(candidate.X,gradient.X,gradient.Y);slopeCorrection=candidate-projected;value=projected;
            residual=0;foreach(var row in rows){float h=row.N.Dot(value);residual=Math.Max(residual,Math.Max(row.Low-h,h-row.High));}
            if(residual<.01f)return 0;
        }
        return residual;
    }
}
