using Godot;
using Clipper2Lib;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
namespace PcgCitySandbox.Pcg;

public static class ScenePlacementValidation
{
    public static async Task Run(MapSettings original){
        int checks=0;void Check(bool ok,string m){checks++;if(!ok)throw new InvalidOperationException("PLACEMENT_TEST_FAILED: "+m);}
        void Validate(ScenePlacementSet data,bool meshes){
            Check(data.BuildingPlacements.Count==data.Buildings.Buildings.Count&&data.BuildingPlacements.All(b=>ReferenceEquals(b.Mass,data.Buildings.Buildings.First(x=>x.Id==b.Mass.Id))),"building plans reused exactly");
            Check(data.Props.Count<=data.Options.ObjectBudget&&data.Props.Select(p=>p.Id).Distinct().Count()==data.Props.Count,"bounded unique placements");
            var zones=data.Zones.ToDictionary(z=>z.Id);var roof=ParcelGeometry.Union(new Paths64(data.Buildings.Buildings.SelectMany(b=>b.RoofProjection.CopyPaths())));
            foreach(var prop in data.Props){var zone=zones[prop.ZoneId];
                Check(BuildingGeometry.Fits(prop.Footprint.CopyPaths(),zone.Shape.CopyPaths()),"entire prop inside owned usable zone");
                Check(BuildingGeometry.Clear(prop.Footprint.CopyPaths(),data.ProtectedArea.CopyPaths()),"roads, roofs, gate and shared passages remain clear");
                Check(prop.MaxGround-prop.MinGround<=.401f&&prop.BaseY>=prop.MaxGround,"bounded support and ground height");
                if(prop.ParcelId is int id)Check(data.Buildings.Buildings.Any(b=>b.ParcelId==id),"no furnishings on failed or unbuilt plots");
                if(prop.Kind!=ScenePropKind.Fence){Check(prop.Approach.Area>.01,"usable approach exists");Check(BuildingGeometry.Fits(prop.Approach.CopyPaths(),zone.Site.CopyPaths()),"approach respects site ownership");Check(BuildingGeometry.Clear(prop.Approach.CopyPaths(),roof),"approach avoids solid buildings");
                    Check(ParcelGeometry.Contains(ParcelGeometry.Offset(prop.Approach.CopyPaths(),.02f),zone.Anchor),"approach connects existing entrance");}
            }
            for(int i=0;i<data.Props.Count;i++)for(int j=i+1;j<data.Props.Count;j++){
                var a=data.Props[i];var b=data.Props[j];if(a.Footprint.Bounds.Intersects(b.Footprint.Bounds,true))Check(BuildingGeometry.Clear(a.Footprint.CopyPaths(),b.Footprint.CopyPaths()),"props do not overlap");
                if(a.Approach.Bounds.Intersects(b.Footprint.Bounds,true))Check(BuildingGeometry.Clear(a.Approach.CopyPaths(),b.Footprint.CopyPaths()),"later prop does not block earlier access");
                if(b.Approach.Bounds.Intersects(a.Footprint.Bounds,true))Check(BuildingGeometry.Clear(b.Approach.CopyPaths(),a.Footprint.CopyPaths()),"new access avoids earlier props");
            }
            var roadArea=ParcelGeometry.Union(new Paths64(data.GroundPatches.Where(g=>g.RoadId.HasValue).SelectMany(g=>g.Shape.CopyPaths())));
            Check(Math.Abs(ParcelGeometry.Area(roadArea)-data.Roads.Geometry.Area.Area)<.01,"road surface exactly inherits canonical geometry");
            var allGround=ParcelGeometry.Union(new Paths64(data.GroundPatches.SelectMany(g=>g.Shape.CopyPaths())));
            Check(Math.Abs(ParcelGeometry.Area(allGround)-data.GroundPatches.Sum(g=>g.Shape.Area))<.2,"ground ownership disjoint");
            if(data.Options.AvoidVegetation)Check(ParcelGeometry.Area(ParcelGeometry.Difference(allGround,data.VegetationMask.CopyPaths()))<.02,"all owned ground protected from natural vegetation");
            if(!meshes)return;
            var cache=new SceneMeshCache(data);var byId=data.Props.ToDictionary(p=>p.Id);bool bounds=true,winding=true,normals=true;
            for(int i=0;i<cache.Props.Positions.Count;i++){var p=cache.Props.Positions[i];var prop=byId[cache.Props.Owners[i]];if(!p.IsFinite()||p.Y<prop.MinGround-.04f||p.Y>prop.BaseY+prop.Height+.003f||!prop.Footprint.Contains(new(p.X,p.Z))&&BuildingGeometry.DistanceToSegment(new(p.X,p.Z),prop.Corners[0],prop.Corners[1])>.001f&&ParcelGeometry.Area(ParcelGeometry.Difference(BuildingGeometry.Polygon(new[]{new Vector2(p.X-.0005f,p.Z-.0005f),new(p.X+.0005f,p.Z-.0005f),new(p.X+.0005f,p.Z+.0005f),new(p.X-.0005f,p.Z+.0005f)}),ParcelGeometry.Offset(prop.Footprint.CopyPaths(),.002f)))>.0000001)bounds=false;
                if(!cache.Props.Normals[i].IsFinite()||Math.Abs(cache.Props.Normals[i].Length()-1)>.001f)normals=false;
                if(i%3==0&&(cache.Props.Positions[i+1]-p).Cross(cache.Props.Positions[i+2]-p).Dot(cache.Props.Normals[i])>1e-5)winding=false;
            }
            Check(bounds,"procedural assets and supports fit declared footprint and height");Check(normals&&winding,"finite outward normals and clockwise triangles");
            double rendered=0;bool heights=true;for(int i=0;i<cache.Ground.Positions.Count;i+=3){var a=cache.Ground.Positions[i];var b=cache.Ground.Positions[i+1];var c=cache.Ground.Positions[i+2];rendered+=Math.Abs(new Vector2(b.X-a.X,b.Z-a.Z).Cross(new(c.X-a.X,c.Z-a.Z)))/2;var p=(a+b+c)/3;data.Environment.TryHeightAt(new(p.X,p.Z),out float h);if(Math.Abs(p.Y-h-.12f)>.003f)heights=false;}
            Check(heights&&Math.Abs(rendered-data.GroundPatches.Sum(g=>g.Shape.Area))<1,"ground mesh follows terrain and covers plan once");
        }
        var reports=new List<object>();
        foreach(int seed in new[]{73129,7,22017}){
            var settings=(MapSettings)original.Duplicate();settings.Seed=seed;var map=new TemperateMapData(settings);var trees=map.Trees.ToArray();
            var source=new TemperateEnvironmentSource(map,"placement",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());var env=EnvironmentSampler.Sample(source,new(source.Bounds));
            var layout=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));var roads=await new RoadNode().RunAsync(env,layout,1,1,new());var parcels=await new ParcelNode().RunAsync(env,layout,roads,1,1,1,new());var buildings=await new BuildingNode().RunAsync(env,parcels,1,1,new());
            var node=new ScenePlacementNode();var options=new ScenePlacementOptions();
            async Task<ScenePlacementSet> Run(ScenePlacementOptions o,PcgContext? ctx=null){var watch=System.Diagnostics.Stopwatch.StartNew();var result=await node.RunAsync(env,roads,parcels,buildings,1,1,1,1,o,ctx);GD.Print($"PLACEMENT_RUN seed={seed} props={result.Props.Count} elapsed={watch.Elapsed.TotalSeconds:0.00}s");return result;}
            var data=await Run(options);GD.Print($"PLACEMENT_GENERATED seed={seed} props={data.Props.Count} ground={data.GroundPatches.Count}");Validate(data,seed==73129);
            Check(data.Props.Any(p=>p.Kind==ScenePropKind.Fence)&&data.Props.Any(p=>p.Kind==ScenePropKind.Shed)&&data.Props.Any(p=>p.ZoneId=="market"),"yard facilities, fences and public objects");
            Check(trees.SequenceEqual(map.Trees),"environment vegetation is not mutated");
            int removed=trees.Count(t=>MapRenderer.TreeMasked(t,data.VegetationMask));Check(removed>0&&removed<trees.Length,"vegetation filter affects only part of landscape");
            if(seed==73129){
                var repeat=await Run(options);Check(data.Props.Select(p=>(p.Id,p.BaseY,p.Footprint.Center)).SequenceEqual(repeat.Props.Select(p=>(p.Id,p.BaseY,p.Footprint.Center))),"deterministic rerun");
                Check(!node.IsCurrent(buildings,2,options)&&!node.IsCurrent(buildings,1,options with{Seed=3}),"input and option signatures");
                var alternate=await Run(options with{Seed=19});Check(!data.Props.Select(p=>p.Footprint.Center).SequenceEqual(alternate.Props.Select(p=>p.Footprint.Center)),"seed affects placement");
                var empty=await Run(options with{DecorationDensity=0,YardDensity=0,Fences=false,AvoidVegetation=false});Check(empty.Props.Count==0&&empty.VegetationMask.Area==0&&empty.BuildingPlacements.Count==buildings.Buildings.Count,"density and vegetation switches retain base assembly");
                var limited=await Run(options with{ObjectBudget=5});Validate(limited,false);Check(limited.Props.Count<=5&&limited.Issues.Any(i=>i.Source=="scene"),"finite budget with explicit diagnostics");
                var gray=await Run(options with{Scheme=SceneScheme.GrayStudy});Check(data.Props.Select(p=>(p.Id,p.Footprint.Center)).SequenceEqual(gray.Props.Select(p=>(p.Id,p.Footprint.Center))),"resource scheme does not relocate objects");
                long revision=node.ResultRevision;using(var cancel=new CancellationTokenSource()){try{await Run(options,new(cancel.Token,p=>{if(p.Fraction>=.65f)cancel.Cancel();return Task.CompletedTask;}));}catch(OperationCanceledException){}Check(node.State==SampleNodeState.Cancelled&&node.ResultRevision==revision&&ReferenceEquals(node.LastResult,gray),"late cancellation retains prior snapshot without publishing partial scene");}
                bool rejected=false;try{await node.RunAsync(env,roads,parcels,buildings,2,1,1,1,options);}catch(ArgumentException){rejected=true;}Check(rejected&&node.ResultRevision==revision,"mixed revisions rejected");
                try{await Run(options,new(default,p=>{if(p.Fraction>=.85f)node.Invalidate();return Task.CompletedTask;}));}catch(InvalidOperationException){}Check(node.State==SampleNodeState.Failed&&node.ResultRevision==revision,"stale generation is not published");
            }
            reports.Add(new{seed,buildings=data.BuildingPlacements.Count,props=data.Props.Count,types=data.Props.GroupBy(p=>p.Kind).ToDictionary(g=>g.Key.ToString(),g=>g.Count()),ground=data.GroundPatches.Count,removedTrees=removed,issues=data.Issues.Count,attempts=data.CandidateAttempts});GD.Print($"PLACEMENT_SEED_PASS seed={seed} props={data.Props.Count} removedTrees={removed}");
        }
        var mask=new ParcelRegion(ParcelGeometry.Rect(new(0,0,10,10)));Check(MapRenderer.TreeMasked(new(new Vector3(15,0,5),1,0,0,1),mask)&&!MapRenderer.TreeMasked(new(new Vector3(30,0,5),1,0,0,1),mask),"tree crown clearance beyond trunk point");
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/placement_validation.json"),JsonSerializer.Serialize(new{passed=true,checks,reports},new JsonSerializerOptions{WriteIndented=true}));GD.Print($"PLACEMENT_VALIDATION_PASS checks={checks}");
    }
}
