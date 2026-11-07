using Godot;
using Clipper2Lib;
using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
namespace PcgCitySandbox.Pcg;
public static class ParcelValidation
{
    public static async Task Run(MapSettings settings){int checks=0;void Check(bool b,string message){checks++;if(!b)throw new InvalidOperationException("PARCEL_TEST_FAILED: "+message);}
        var map=new TemperateMapData(settings);var source=new TemperateEnvironmentSource(map,"parcels",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());var env=EnvironmentSampler.Sample(source,new(source.Bounds));var settlement=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));var roads=await new RoadNode().RunAsync(env,settlement,1,1,new());var node=new ParcelNode();var options=new ParcelOptions();
        var data=await node.RunAsync(env,settlement,roads,1,1,1,options);Check(data.Blocks.Count>1&&data.Parcels.Count>10,"default blocks and frontage plots");
        var land=ParcelGeometry.SettlementCells(settlement);var roadArea=roads.Geometry.Area.CopyPaths();var reserved=ParcelGeometry.SettlementCells(settlement,true);
        var occupied=ParcelGeometry.Union(new Paths64(roadArea.Concat(reserved)));var roadBuffer=ParcelGeometry.Offset(roadArea,options.RoadSetback);
        Check(Math.Abs(data.AvailableArea-data.ParcelArea-data.RemainderArea)<1,"land accounting");
        foreach(var parcel in data.Parcels){
            Check(parcel.Shape.Area>=options.MinimumArea&&parcel.Frontage.Max(f=>f.Length)>=options.MinimumFrontage,"minimum area and true frontage");
            Check(ParcelGeometry.Area(ParcelGeometry.Intersection(parcel.Shape.CopyPaths(),occupied))<.02,"road and square exclusion");
            Check(ParcelGeometry.Area(ParcelGeometry.Difference(parcel.Shape.CopyPaths(),land))<.02,"inside settlement");
            Check(ParcelGeometry.Area(ParcelGeometry.Difference(parcel.Buildable.CopyPaths(),parcel.Shape.CopyPaths()))<.02&&ParcelGeometry.Area(ParcelGeometry.Intersection(parcel.Buildable.CopyPaths(),roadBuffer))<.02,"building envelope and road setback");
            Check(parcel.Frontage.All(f=>roads.Edges.Any(e=>e.Id==f.RoadId)),"frontage road references");
            Check(parcel.Frontage.All(f=>roads.Geometry.FrontageAt(f.A,f.B)?.RoadId==f.RoadId),"frontage inherited from exact shaped road boundary");
        }
        var parcelUnion=ParcelGeometry.Union(new Paths64(data.Parcels.SelectMany(p=>p.Shape.CopyPaths())));Check(Math.Abs(ParcelGeometry.Area(parcelUnion)-data.ParcelArea)<.1,$"parcels do not overlap: union={ParcelGeometry.Area(parcelUnion)} sum={data.ParcelArea} count={data.Parcels.Count}");
        var repeat=await node.RunAsync(env,settlement,roads,1,1,1,options);Check(data.Parcels.Select(p=>(p.Use,p.Shape.Area,p.Shape.Center)).SequenceEqual(repeat.Parcels.Select(p=>(p.Use,p.Shape.Area,p.Shape.Center))),"deterministic rerun");Check(!node.IsCurrent(roads,2,options)&&!node.IsCurrent(roads,1,options with{Width=16}),"version and parameter checks");
        var alternate=await node.RunAsync(env,settlement,roads,1,1,1,options with{Seed=19});Check(!repeat.Parcels.Select(p=>p.Shape.Area).SequenceEqual(alternate.Parcels.Select(p=>p.Shape.Area)),"seed changes frontage subdivision");
        repeat=await node.RunAsync(env,settlement,roads,1,1,1,options);
        long rev=node.ResultRevision;using(var cancellation=new CancellationTokenSource()){try{await node.RunAsync(env,settlement,roads,1,1,1,options,new(cancellation.Token,p=>{cancellation.Cancel();return Task.CompletedTask;}));}catch(OperationCanceledException){}Check(node.State==SampleNodeState.Cancelled&&node.ResultRevision==rev&&ReferenceEquals(node.LastResult,repeat),"cancel no partial publication");}
        bool rejected=false;try{await node.RunAsync(env,settlement,roads,2,1,1,options);}catch(ArgumentException){rejected=true;}Check(rejected,"mismatched upstream");
        rejected=false;try{await node.RunAsync(env,settlement,roads,1,1,1,options with{CommercialShare=.8f,ProductionShare=.8f});}catch(ArgumentException){rejected=true;}Check(rejected,"invalid proportions rejected");
        try{await node.RunAsync(env,settlement,roads,1,1,1,options,new(default,p=>{node.Invalidate();return Task.CompletedTask;}));}catch(InvalidOperationException){}Check(node.ResultRevision==rev&&node.State==SampleNodeState.Failed,"input change during run");
        var empty=await node.RunAsync(env,settlement,roads,1,1,1,options with{MinimumArea=1e8});Check(empty.Parcels.Count==0&&Math.Abs(empty.AvailableArea-empty.RemainderArea)<.1,"unusable plots retained as explicit leftover land");
        using(var fixture=JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://configs/tests/parcel_touching_boundary.json")))){
            Paths64 Read(string key)=>new(fixture.RootElement.GetProperty(key).EnumerateArray().Select(r=>new Path64(r.EnumerateArray().Select(v=>new Point64((long)Math.Round(v[0].GetDouble()*ParcelGeometry.Scale),(long)Math.Round(v[1].GetDouble()*ParcelGeometry.Scale))))));
            var subject=Read("original");var cut=Read("accepted");var pieces=ParcelGeometry.DifferenceComponents(subject,cut);
            Check(Math.Abs(pieces.Sum(ParcelGeometry.Area)-ParcelGeometry.Area(ParcelGeometry.Difference(subject,cut)))<.1,"touching-boundary hole ownership regression");
        }
        foreach(string fixture in new[]{"road_boundary_spur.json","road_boundary_touch.json","road_boundary_micro_sliver.json"})using(var spur=JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://configs/tests/"+fixture)))){
            var paths=new Paths64(spur.RootElement.EnumerateArray().Select(r=>new Path64(r.EnumerateArray().Select(p=>new Point64(p[0].GetInt64(),p[1].GetInt64())))));var region=new ParcelRegion(paths);
            double sum=0;for(int i=0;i<region.Triangles.Count;i+=3)sum+=Math.Abs((region.Triangles[i+1]-region.Triangles[i]).Cross(region.Triangles[i+2]-region.Triangles[i]))/2;
            Check(Math.Abs(sum-region.Area)<.01,"zero-area road-boundary spur triangulates without changing ownership area");
            if(fixture.Contains("micro"))Check(region.Area>0&&region.Triangles.Count==0,"unrenderable microscopic sliver keeps ownership and area");
        }
        var outer=ParcelGeometry.Rect(new(0,0,100,100));var hole=ParcelGeometry.Rect(new(40,40,20,20));var withHole=new ParcelRegion(ParcelGeometry.Difference(outer,hole));Check(Math.Abs(withHole.Area-9600)<.01&&!withHole.Contains(new(50,50))&&withHole.Rings.Count==2,"holes preserved");double triangleArea=0;for(int i=0;i<withHole.Triangles.Count;i+=3)triangleArea+=Math.Abs((withHole.Triangles[i+1]-withHole.Triangles[i]).Cross(withHole.Triangles[i+2]-withHole.Triangles[i]))/2;Check(Math.Abs(triangleArea-9600)<.01,"hole-safe triangulation");
        var split=ParcelGeometry.Difference(outer,ParcelGeometry.Rect(new(49,0,2,100)));Check(ParcelGeometry.Components(split).Count==2,"narrow road cuts continuous blocks geometrically");
        string dir=ProjectSettings.GlobalizePath("res://artifacts");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"parcel_validation.json"),JsonSerializer.Serialize(new{passed=true,checks,blocks=data.Blocks.Count,parcels=data.Parcels.Count,data.AvailableArea,data.ParcelArea,data.RemainderArea,data.InsideDevelopedArea,data.OutsideDevelopedArea,uses=data.Parcels.GroupBy(p=>p.Use).ToDictionary(g=>g.Key.ToString(),g=>g.Count())},new JsonSerializerOptions{WriteIndented=true}));
        GD.Print($"PARCEL_VALIDATION_PASS checks={checks} blocks={data.Blocks.Count} parcels={data.Parcels.Count} area={data.ParcelArea:0} remainder={data.RemainderArea:0}");
    }
}
