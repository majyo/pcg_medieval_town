using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PcgCitySandbox.Pcg;

public static class RoadSweepValidation
{
    public static async Task Run(MapSettings original)
    {
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException("SWEEP_FRAME_FAILED: "+message);}
        int probes=0;float error=0;
        foreach(float radius in new[]{10f,20f,50f}){
            var points=Enumerable.Range(0,41).Select(i=>new Vector2(100,100)+Vector2.FromAngle(i*Mathf.Pi/80)*radius).ToArray();var frame=new RoadSweepFrame(points);
            for(float s=.2f;s<frame.Length-.2f;s+=.25f)foreach(float lateral in new[]{-2.4f,-1.2f,0,1.2f,2.4f}){
                var at=frame.At(s);var hit=frame.Locate(at.Center+at.Normal*lateral,3);
                Check(hit.Branches==1,"regular bend has a unique transverse coordinate");float target=.04f*s-.02f*Math.Abs(lateral);float actual=.04f*hit.Station-.02f*Math.Abs(hit.Offset);
                error=Math.Max(error,Math.Abs(actual-target));probes++;
            }
        }
        Check(error<.005f,"curved cross-sections retain the prescribed linear crossfall");
        var tight=new RoadSweepFrame(Enumerable.Range(0,21).Select(i=>Vector2.FromAngle(i*Mathf.Pi/40)*2).ToArray());
        Check(tight.Locate(Vector2.Zero,3).Branches>1,"overlapping inner bend is detected instead of silently blended");
        foreach(var path in new[]{new RoadPlanarPath(new[]{new RoadPlanarPath.Span(new(100,100),new(120,100),new(120,120),true)}),RoadPlanarPath.Polyline(new[]{new Vector2(0,0),new Vector2(100,0)})}){
            for(float s=.25f;s<path.Length-.25f;s+=.25f)foreach(float lateral in new[]{-2.9f,-1.5f,0,1.5f,2.9f}){
                var at=path.At(s);var normal=new Vector2(-at.Tangent.Y,at.Tangent.X);var hit=path.Locate(at.Point+normal*lateral,3);
                Check(hit.Branches==1,"analytic path has a unique normal coordinate");float residual=Math.Abs(.04f*s-.02f*Math.Abs(lateral)-(.04f*hit.Station-.02f*Math.Abs(hit.Offset)));error=Math.Max(error,residual);probes++;
                Check(residual<.005f,"analytic orthogonal cross-section is planar");}}
        var reports=new List<object>();
        foreach(int seed in new[]{73129,7,22017}){
            var settings=(MapSettings)original.Duplicate();settings.Seed=seed;var map=new TemperateMapData(settings);var source=new TemperateEnvironmentSource(map,"sweep",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());
            var env=EnvironmentSampler.Sample(source,new(source.Bounds));var layout=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));var roads=await new RoadNode().RunAsync(env,layout,1,1,new());
            var missingPoints=new List<object>();var folded=new List<object>();int samples=0,ambiguous=0,missing=0;
            foreach(var road in roads.Edges){var frame=road.PlanarPath??RoadPlanarPath.Polyline(road.Points);int count=0;var positions=new List<object>();
                for(float s=.5f;s<frame.Length-.5f;s+=1)foreach(float lateral in new[]{-.8f*road.Width/2,.8f*road.Width/2}){var at=frame.At(s);var normal=new Vector2(-at.Tangent.Y,at.Tangent.X);var hit=frame.Locate(at.Point+normal*lateral,road.Width/2);samples++;if(hit.Branches==0){missing++;missingPoints.Add(new{road.Id,s,lateral,position=new[]{at.Point.X+normal.X*lateral,at.Point.Y+normal.Y*lateral},spans=frame.Spans.Select(v=>new{v.Curved,a=new[]{v.A.X,v.A.Y},b=new[]{v.B.X,v.B.Y},c=new[]{v.C.X,v.C.Y}})});}if(hit.Branches>1){ambiguous++;count++;positions.Add(new{s,lateral,point=new[]{at.Point.X,at.Point.Y},hit.Station,hit.Offset});}}
                if(count>0)folded.Add(new{road.Id,road.Width,ambiguous=count,positions,points=road.Points.Select(p=>new[]{p.X,p.Y}).ToArray(),notices=roads.Geometry.Notices.Where(n=>n.RoadId==road.Id).Select(n=>n.Reason).ToArray()});
            }
            reports.Add(new{seed,samples,ambiguous,missing,missingPoints,folded});GD.Print($"SWEEP_NETWORK_DIAGNOSTIC seed={seed} samples={samples} ambiguous={ambiguous} missing={missing}");
        }
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/road_sweep_frame_validation.json"),JsonSerializer.Serialize(new{primitivePassed=true,networkReady=false,probes,maxResidual=error,reports},new JsonSerializerOptions{WriteIndented=true}));
        GD.Print($"SWEEP_PRIMITIVE_PASS probes={probes} max_mm={error*1000:0.000}");
    }
}
