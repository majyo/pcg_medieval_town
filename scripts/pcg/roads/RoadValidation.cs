using Godot;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
namespace PcgCitySandbox.Pcg;
public static class RoadValidation
{
    private sealed class Fixture:IEnvironmentSource{
        public string SourceId=>"road_geometry";public long Revision=>1;public Rect2 Bounds=>new(0,0,100,100);public float? NativeSpacing=>10;
        public EnvironmentLayers Layers=>new(LayerStatus.Provided,LayerStatus.Provided,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty);
        public IReadOnlyList<ConstraintPolygon> NoBuildZones=>Array.Empty<ConstraintPolygon>();public IReadOnlyList<ConstraintPolygon> Obstacles=>Array.Empty<ConstraintPolygon>();
        public bool Contains(Vector2 p)=>p.X>=0&&p.Y>=0&&p.X<=100&&p.Y<=100;
        public Func<Vector2,float> Height=p=>0;public EnvironmentPoint Query(Vector2 p)=>new(Height(p),0,false,0);
    }
    private static SettlementLayout Layout(CityEnvironmentData e,Func<int,int,bool> included){
        var cells=Enumerable.Range(0,100).Select(k=>new LayoutCell(included(k%10,k/10)?SettlementCellState.Included:SettlementCellState.Restricted,EnvironmentRestriction.None,0,false)).ToArray();
        var edge=new LayoutEdge(20,new Vector2(0,20),new Vector2(0,30),Vector2.Left,true);var entry=new LayoutEntry("E1",new Vector2(0,25),0,edge);
        return new(e,"test",1,new SettlementOptions(new Vector2(50,50),100),new Vector2(25,25),cells,Array.Empty<LayoutEdge>(),new[]{entry},new[]{edge},Array.Empty<string>());
    }
    public static async Task Run(MapSettings settings){int checks=0;void Check(bool b,string s){checks++;if(!b)throw new InvalidOperationException("ROAD_TEST_FAILED: "+s);}
        var map=new TemperateMapData(settings);var source=new TemperateEnvironmentSource(map,"road_test",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());
        var env=EnvironmentSampler.Sample(source,new(source.Bounds));var layout=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));var node=new RoadNode();var options=new RoadOptions();
        var result=await node.RunAsync(env,layout,1,1,options);Check(result.Edges.Any(e=>e.Main)&&result.Edges.Any(e=>!e.Main),"main and secondary roads");Check(result.CycleCount>0,"default closed blocks");
        foreach(var entry in layout.Entries)Check(result.PathTo(entry.Id).Length>0,"entry reaches core");
        var space=new RoadSpace(env,layout,options);Check(result.Footprint.All(p=>space.PolygonValid(p.ToArray())),"all road and junction widths legal");Check(result.Edges.All(e=>e.MaxGrade<=options.MaxGrade+.001f),"grade constraints");
        Check(result.Edges.All(e=>result.Nodes.Any(v=>v.Id==e.From)&&result.Nodes.Any(v=>v.Id==e.To)),"topology references");Check(result.Edges.All(e=>e.Points.Count>=2&&e.Length>0),"nonzero edges");
        Check(result.Contains(layout.Core)&&layout.Entries.All(b=>!result.Contains(b.Position+b.Edge.Outward*2)),"footprint point query respects flat entry caps");
        var repeat=await node.RunAsync(env,layout,1,1,options);Check(result.Edges.Select(e=>string.Join(";",e.Points)).SequenceEqual(repeat.Edges.Select(e=>string.Join(";",e.Points))),"deterministic geometry");Check(result.Anchors.SequenceEqual(repeat.Anchors),"deterministic anchors");
        Check(!node.IsCurrent(env,layout,1,2,options),"layout revision invalidates");Check(!node.IsCurrent(env,layout,1,1,options with{Spacing=90}),"parameters invalidate");
        long rev=node.ResultRevision;using(var cancel=new CancellationTokenSource()){try{await node.RunAsync(env,layout,1,1,options,new(cancel.Token,p=>{cancel.Cancel();return Task.CompletedTask;}));}catch(OperationCanceledException){}Check(node.State==SampleNodeState.Cancelled&&node.ResultRevision==rev&&ReferenceEquals(node.LastResult,repeat),"cancel no publication");}
        bool rejected=false;try{await node.RunAsync(env,layout,2,1,options);}catch(ArgumentException){rejected=true;}Check(rejected,"mismatched environment rejected");
        rejected=false;try{await node.RunAsync(env,layout,1,1,options with{MainWidth=30,Spacing=100});}catch(RoadFailure){rejected=true;}Check(rejected&&node.ResultRevision==rev&&node.FailureIssues.Count>0,"wide entrance rejected with diagnostic");
        rejected=false;try{await node.RunAsync(env,layout,1,1,options with{SearchBudget=1});}catch(RoadFailure f){rejected=f.Message.Contains("预算");}Check(rejected,"budget distinguished from unreachable");
        try{await node.RunAsync(env,layout,1,1,options,new(default,p=>{node.Invalidate();return Task.CompletedTask;}));}catch(InvalidOperationException){}Check(node.State==SampleNodeState.Failed&&node.ResultRevision==rev,"input change rejects publication");
        var fixture=new Fixture();var flat=EnvironmentSampler.Sample(fixture,new(fixture.Bounds));var unrestricted=Layout(flat,(x,z)=>true);
        var flatSpace=new RoadSpace(flat,unrestricted,options);
        Check(flatSpace.SegmentValid(new Vector2(0,25),new Vector2(25,25),8),"butt entry cap stays inside boundary");
        Check(!flatSpace.PolygonValid(RoadSpace.Pad(new Vector2(0,25),8)),"round entrance cap would violate boundary");
        var narrow=new RoadSpace(flat,Layout(flat,(x,z)=>z==2),options);
        Check(narrow.SegmentValid(new Vector2(5,25),new Vector2(95,25),8)&&!narrow.SegmentValid(new Vector2(5,25),new Vector2(95,25),12),"full width narrow corridor");
        var diagonal=new RoadSpace(flat,Layout(flat,(x,z)=>!(x==5&&z==4)&&!(x==4&&z==5)),options);
        Check(!diagonal.SegmentValid(new Vector2(45,45),new Vector2(55,55),5),"diagonal cannot cut restricted corners");
        var hole=new RoadSpace(flat,Layout(flat,(x,z)=>!(x==5&&z==2)),options);
        Check(!hole.SegmentValid(new Vector2(5,25),new Vector2(95,25),5),"long segment cannot jump over internal hole");
        fixture.Height=p=>Math.Max(0,20-Math.Abs(p.X-50)*2);var hill=EnvironmentSampler.Sample(fixture,new(fixture.Bounds));
        var hillSpace=new RoadSpace(hill,Layout(hill,(x,z)=>true),options);
        Check(hillSpace.Grade(new Vector2(5,25),new Vector2(95,25))>45&&!hillSpace.SegmentValid(new Vector2(5,25),new Vector2(95,25),5),"intermediate hill detected despite equal endpoint heights");
        bool planar=true;var pieces=result.Edges.SelectMany(edge=>edge.Points.Skip(1).Select((b,i)=>(A:edge.Points[i],B:b))).ToArray();
        for(int i=0;i<pieces.Length;i++)for(int j=i+1;j<pieces.Length;j++){
            var a=pieces[i];var b=pieces[j];var r=a.B-a.A;var t=b.B-b.A;float cross=r.Cross(t);if(Math.Abs(cross)<.0001f)continue;
            float u=(b.A-a.A).Cross(t)/cross,v=(b.A-a.A).Cross(r)/cross;
            if(u>1e-4&&u<.9999&&v>1e-4&&v<.9999)planar=false;
        }
        Check(planar,"no unsplit geometric crossings");
        await RoadGeometryValidation.Run(result);
        string dir=ProjectSettings.GlobalizePath("res://artifacts");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"road_validation.json"),JsonSerializer.Serialize(new{passed=true,checks,nodes=result.Nodes.Count,edges=result.Edges.Count,result.MainLength,result.StreetLength,result.CycleCount,issues=result.Issues.Select(i=>i.Reason)},new JsonSerializerOptions{WriteIndented=true}));GD.Print($"ROAD_VALIDATION_PASS checks={checks} edges={result.Edges.Count} cycles={result.CycleCount} main={result.MainLength:0} secondary={result.StreetLength:0}");
    }
}
