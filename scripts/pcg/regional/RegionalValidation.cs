using Godot;
using Clipper2Lib;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
namespace PcgCitySandbox.Pcg;
public static class RegionalValidation
{
    public static async Task Run(MapSettings original){int checks=0;void Check(bool b,string m){checks++;if(!b)throw new InvalidOperationException("REGIONAL_TEST_FAILED: "+m);}var reports=new List<object>();
        foreach(int seed in new[]{73129,7,22017}){
            var settings=(MapSettings)original.Duplicate();settings.Seed=seed;var map=new TemperateMapData(settings);var source=new TemperateEnvironmentSource(map,"regional_test",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());var env=EnvironmentSampler.Sample(source,new(source.Bounds));var node=new SettlementLayoutNode();var layout=await node.RunAsync(env,1,new(Vector2.Zero));var plan=layout.Region!;var town=layout.Town!;
            Check(plan!=null&&plan.StudyArea==env.Bounds,"study area independent from town footprint");Check(plan!.Districts.Count(d=>d.Role==TownDistrictRole.GateSuburb)>=2,"multiple planned extramural street districts");Check(plan.Districts.Any(d=>d.Role==TownDistrictRole.InstitutionSuburb),"independent institution district");Check(layout.Entries.Count>town.Gates.Count,"gates independent of regional portals");Check(plan.Links.Where(l=>l.GateId!=null).GroupBy(l=>l.GateId).Any(g=>g.Count()>1),"gate serves multiple routes");
            var abbey=town.Precincts.Single(p=>p.Id=="abbey");Check(!town.Interior.Contains(abbey.Shape.Center),"abbey is actually outside walls");
            var abbeyDistrict=plan.Districts.Single(d=>d.Role==TownDistrictRole.InstitutionSuburb);var others=ParcelGeometry.Union(new Paths64(plan.Districts.Where(d=>d.Id!=abbeyDistrict.Id).SelectMany(d=>d.Shape.CopyPaths())));
            Check(ParcelGeometry.Area(ParcelGeometry.Intersection(ParcelGeometry.Offset(abbeyDistrict.Shape.CopyPaths(),20),others))<.02,"institution settlement separated by open land");Check(plan.Landscape.Count>=3&&plan.Landscape.All(f=>f.AccessRoute.Count>1),"productive landscape and access routes");
            Check(plan.Landscape.Count(f=>f.Pattern=="arable"&&f.CultivationLines?.Count>=3)>=2,"coherent cultivation groups contain parallel strips");
            Check(plan.Landscape.Select(f=>Math.Round(f.Shape.Area/500)).Distinct().Count()>=4,"field size variation");
            Check(plan.Landscape.Where(f=>f.Pattern=="arable").Select(f=>Math.Round(f.Direction.Angle(),1)).Distinct().Count()>=2,"cultivation groups have local orientations");
            for(int i=0;i<plan.Landscape.Count;i++)for(int j=i+1;j<plan.Landscape.Count;j++){
                var clearance=ParcelGeometry.Offset(plan.Landscape[i].Shape.CopyPaths(),AgriculturalLandscapePlanner.FieldSeparation-.01f);
                Check(ParcelGeometry.Area(ParcelGeometry.Intersection(clearance,plan.Landscape[j].Shape.CopyPaths()))<.02,"whole field groups retain a visible headland gap");
            }
            Check(plan.Landscape.Any(f=>f.Pattern!="arable"),"cultivation mixed with orchard or pasture");
            var fieldsUnion=ParcelGeometry.Union(new Paths64(plan.Landscape.SelectMany(f=>f.Shape.CopyPaths())));
            Check(Math.Abs(ParcelGeometry.Area(fieldsUnion)-plan.Landscape.Sum(f=>f.Shape.Area))<.1,"agricultural groups do not overlap");
            Check(ParcelGeometry.Area(ParcelGeometry.Intersection(fieldsUnion,plan.DevelopmentArea.CopyPaths()))<.02,"fields avoid urban districts");
            foreach(var field in plan.Landscape){
                var safe=ParcelGeometry.Offset(field.Shape.CopyPaths(),.01f);
                Check(field.CultivationLines==null||field.CultivationLines.All(line=>line.All(p=>ParcelGeometry.Contains(safe,p))),"cultivation marks stay in field outlines");
            }
            foreach(var field in plan.Landscape){
                var triangles=new Paths64();var points=field.Shape.Triangles;
                for(int i=0;i<points.Count;i+=3)triangles.Add(ParcelGeometry.Path(new[]{points[i],points[i+1],points[i+2]}));
                var drawn=ParcelGeometry.Union(triangles);double outside=ParcelGeometry.Area(ParcelGeometry.Difference(drawn,field.Shape.CopyPaths()));
                Check(outside<.1,$"field triangles stay within outline: {field.Id} outside={outside}");
            }
            Check(layout.Cells.Count(c=>c.State==SettlementCellState.Included)==env.Summary.CandidateCells,"other land components are retained");
            foreach(var link in plan.Links)Check(link.Points.All(p=>env.TryGetCell(p,out var c)&&c.State==BuildCandidateState.Candidate),"regional routes do not invent water crossings");
            var roads=await new RoadNode().RunAsync(env,layout,1,node.ResultRevision,new());foreach(var entry in layout.Entries)Check(roads.PathTo(entry.Id).Length>0,"regional destination reachable");foreach(var gate in town.Gates)Check(roads.PathTo(gate.Id).Length>0,"gate reachable");Check(roads.PathTo("abbey").Length>0,"outer institution reachable");
            var outerEdges=roads.Edges.Where(edge=>edge.Points.All(p=>!town.Interior.Contains(p))&&edge.Points.Skip(1).Select((p,i)=>(p+edge.Points[i])/2).All(p=>!town.Interior.Contains(p))).ToArray();
            var reachable=new HashSet<int>{roads.Anchors["abbey"]};var q=new Queue<int>(reachable);while(q.TryDequeue(out int v))foreach(var edge in outerEdges.Where(edge=>edge.From==v||edge.To==v)){int next=edge.From==v?edge.To:edge.From;if(reachable.Add(next))q.Enqueue(next);}
            Check(layout.Entries.Any(entry=>reachable.Contains(roads.Anchors[entry.Id])),"outer settlement reaches regional destination without entering fortified core");
            var roadArea=roads.Geometry.Area.CopyPaths();Check(roads.Landscape.All(f=>ParcelGeometry.Area(ParcelGeometry.Intersection(f.Shape.CopyPaths(),roadArea))<.02),"productive fields exclude final roads");
            foreach(var field in roads.Landscape){
                var drawnTriangles=new Paths64();var vertices=field.Shape.Triangles;
                for(int i=0;i<vertices.Count;i+=3)drawnTriangles.Add(ParcelGeometry.Path(new[]{vertices[i],vertices[i+1],vertices[i+2]}));
                Check(ParcelGeometry.Area(ParcelGeometry.Difference(ParcelGeometry.Union(drawnTriangles),field.Shape.CopyPaths()))<.1,"final preview triangles do not overdraw field boundaries");
                var safeOutline=ParcelGeometry.Offset(field.Shape.CopyPaths(),.01f);
                Check(field.CultivationLines==null||field.CultivationLines.All(line=>line.All(p=>ParcelGeometry.Contains(safeOutline,p))),"crop marks trimmed against final roads");
            }
            var parcels=await new ParcelNode().RunAsync(env,layout,roads,1,node.ResultRevision,1,new());Check(parcels.OutsideDevelopedArea>=parcels.InsideDevelopedArea*.8,"substantial actual extramural development, excluding background");Check(parcels.Parcels.Any(p=>p.InsideWalls)&&parcels.Parcels.Any(p=>!p.InsideWalls),"both sides contain actual plots");
            foreach(var district in plan.Districts.Where(d=>d.Role!=TownDistrictRole.FortifiedCore))Check(parcels.Parcels.Count(p=>p.DistrictId==district.Id)>=10,"each outer district has usable plots");
            var shapes=ParcelGeometry.Union(new Paths64(parcels.Parcels.SelectMany(p=>p.Shape.CopyPaths())));Check(ParcelGeometry.Area(ParcelGeometry.Difference(shapes,plan.DevelopmentArea.CopyPaths()))<.1,"plot generation respects regional district limits");Check(ParcelGeometry.Area(ParcelGeometry.Intersection(shapes,town.BuildingReserve.CopyPaths()))<.1,"wall and precinct exclusion");
            if(seed==73129){var changed=await node.RunAsync(env,1,layout.Options with{SuburbLength=360});Check(layout.Town!.WallLine.SequenceEqual(changed.Town!.WallLine),"changing suburbs does not regenerate a larger enclosing wall");Check(changed.Region!.DevelopmentArea.Area<plan.DevelopmentArea.Area,"suburb extent parameter changes planned area");var repeat=await node.RunAsync(env,1,layout.Options);Check(plan.Districts.Select(d=>d.Shape.Area).SequenceEqual(repeat.Region!.Districts.Select(d=>d.Shape.Area)),"regional determinism");Check(plan.Landscape.Select(f=>(f.Pattern,f.Direction,f.Shape.Area)).SequenceEqual(repeat.Region.Landscape.Select(f=>(f.Pattern,f.Direction,f.Shape.Area))),"field groups deterministic");}
            reports.Add(new{seed,districts=plan.Districts.Count,portals=layout.Entries.Count,gates=town.Gates.Count,fields=plan.Landscape.Count,fieldTypes=plan.Landscape.GroupBy(f=>f.Pattern).ToDictionary(g=>g.Key,g=>g.Count()),parcels=parcels.Parcels.Count,insideArea=parcels.InsideDevelopedArea,outsideArea=parcels.OutsideDevelopedArea,outsideShare=parcels.OutsideDevelopedArea/parcels.ParcelArea});GD.Print($"REGIONAL_SEED_PASS seed={seed} districts={plan.Districts.Count} inner={parcels.InsideDevelopedArea:0} outer={parcels.OutsideDevelopedArea:0}");
        }
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/regional_validation.json"),JsonSerializer.Serialize(new{passed=true,checks,reports},new JsonSerializerOptions{WriteIndented=true}));GD.Print($"REGIONAL_VALIDATION_PASS checks={checks}");
    }
}
