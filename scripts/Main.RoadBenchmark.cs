using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;

namespace PcgCitySandbox;
public partial class Main
{
    private bool _presentationOverride;
    private sealed record RoadFrameStats(double MedianMs,double P95Ms,double CpuMs,double GpuMs,double DrawCalls,double Primitives,double VideoBytes);
    private async Task BenchmarkRoadConstruction(string directory)
    {
        var d=CurrentPlacement??throw new InvalidOperationException("缺少完整城镇");var g=CurrentGrading!;
        var house=d.Buildings.Buildings.Where(b=>b.InsideCore&&b.ParcelId.HasValue).OrderBy(b=>b.Door.DistanceSquaredTo(d.Roads.Settlement.Core)).First();var gate=d.Roads.Settlement.Town!.Gates[0];
        var originalCamera=(_camera.Focus,_camera.Size,_camera.Pitch,_camera.Yaw);
        var cameras=new[]{("town",d.Roads.Settlement.Core,1600f,.8f,-.35f),("street",house.Footprint.Center,90f,.58f,Mathf.Atan2(-house.Inward.X,-house.Inward.Y)),("gate",gate.Position,100f,.5f,Mathf.Atan2(gate.Outward.X,gate.Outward.Y))};
        var stats=new Dictionary<string,RoadFrameStats>();var viewport=GetViewport().GetViewportRid();DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);Engine.MaxFps=0;RenderingServer.ViewportSetMeasureRenderTime(viewport,true);
        _presentationOverride=true;
        try{
            foreach(bool graded in new[]{false,true}){
                _map.SetGradedTerrain(graded?g.Graded:null);
                _cityPresentation.Display(new(_data,d.Roads.Settlement,d.Roads,d.Parcels,d.Buildings,d,d,false,graded?g:null));
                _cityPresentation.SetState(CityViewMode.Model,CityAnalysisTheme.Uses,true,0,true,true);
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                foreach(var (name,p,size,pitch,yaw) in cameras){
                    _camera.Focus=new(p.X,g.Graded!.HeightAt(p)+2,p.Y);_camera.Size=size;_camera.Pitch=pitch;_camera.Yaw=yaw;_camera.UpdateTransform();
                    var warm=Stopwatch.StartNew();while(warm.Elapsed.TotalSeconds<2)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                    var times=new List<double>();var cpu=new List<double>();var gpu=new List<double>();var timer=Stopwatch.StartNew();double last=0;
                    while(timer.Elapsed.TotalSeconds<6){await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);double now=timer.Elapsed.TotalMilliseconds;times.Add(now-last);last=now;cpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport));gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport));}
                    double Q(List<double> values,double q){values.Sort();return values[Math.Min(values.Count-1,(int)(values.Count*q))];}
                    var result=new RoadFrameStats(Q(times,.5),Q(times,.95),Q(cpu,.5),Q(gpu,.5),Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed));
                    stats[$"{graded}/{name}"]=result;GD.Print($"ROAD_BENCHMARK graded={graded} view={name} p95_ms={result.P95Ms:0.000} video_mb={result.VideoBytes/1048576:0.0}");
                }
            }
            var results=cameras.Select(c=>{var before=stats[$"False/{c.Item1}"];var after=stats[$"True/{c.Item1}"];return new{view=c.Item1,baseline=before,graded=after,medianRatio=after.MedianMs/before.MedianMs,p95Ratio=after.P95Ms/before.P95Ms,addedVideoMiB=(after.VideoBytes-before.VideoBytes)/1048576,within60fps=after.P95Ms<=16.7,within25percent=after.MedianMs<=before.MedianMs*1.25&&after.P95Ms<=before.P95Ms*1.25,within128MiB=after.VideoBytes-before.VideoBytes<=128*1048576};}).ToArray();
            File.WriteAllText(Path.Combine(directory,"performance.json"),JsonSerializer.Serialize(new{adapter=RenderingServer.GetVideoAdapterName(),resolution="1440x900",seed=_data.Settings.Seed,buildings=d.Buildings.Buildings.Count,props=d.Props.Count,baseline="Same buildings, props and cameras; native terrain with original road presentation",secondsPerView=6,results},new JsonSerializerOptions{WriteIndented=true}));
        }finally{_presentationOverride=false;_camera.Focus=originalCamera.Focus;_camera.Size=originalCamera.Size;_camera.Pitch=originalCamera.Pitch;_camera.Yaw=originalCamera.Yaw;_camera.UpdateTransform();UpdateCityPresentation(true);}
    }
}
