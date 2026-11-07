using Godot;
using Clipper2Lib;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
namespace PcgCitySandbox.Pcg;
public static class MedievalValidation
{
    public static async Task Run(MapSettings original){int checks=0;void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException("MEDIEVAL_TEST_FAILED: "+message);}var reports=new List<object>();
        foreach(int seed in new[]{73129,7,22017}){
            var settings=(MapSettings)original.Duplicate();settings.Seed=seed;var map=new TemperateMapData(settings);var source=new TemperateEnvironmentSource(map,"medieval",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());var env=EnvironmentSampler.Sample(source,new(source.Bounds));
            var layout=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));var town=layout.Town!;
            Check(town!=null&&town.WallLine.Count>=3&&town.WallFootprint.Area>0,"closed wall footprint");Check(town!.Gates.Count==Math.Min(layout.Options.GateCount,layout.Entries.Count),"gate count independent of regional entry count");Check(town.Precincts.Count==2,"independent inner church and outer abbey");
            var land=ParcelGeometry.SettlementCells(layout);Check(ParcelGeometry.Area(ParcelGeometry.Difference(town.WallReserve.CopyPaths(),land))<.02,"wall buffer on valid land");
            foreach(var gate in town.Gates){Check(town.Interior.Contains(gate.Position-gate.Outward*2)&&!town.Interior.Contains(gate.Position+gate.Outward*2),"gate straddles wall line");Check(ParcelGeometry.Area(ParcelGeometry.Intersection(gate.Passage.CopyPaths(),town.WallFootprint.CopyPaths()))<.02,"wall opening is clear");}
            var roads=await new RoadNode().RunAsync(env,layout,1,1,new());var surface=roads.Geometry.Area.CopyPaths();
            Check(ParcelGeometry.Area(ParcelGeometry.Intersection(surface,town.RoadObstacles.CopyPaths()))<.05,"roads avoid wall and precincts");foreach(var gate in town.Gates)Check(roads.PathTo(gate.Id).Length>0,"gate topology binds");foreach(var precinct in town.Precincts)Check(roads.PathTo(precinct.Id).Length>0,"institution access connected");Check(roads.Edges.Any(e=>e.Role==StreetRole.LocalStreet)&&roads.Edges.Any(e=>e.Role==StreetRole.MarketStreet),"street roles");
            if(seed==73129){var planned=await new RoadNode().RunAsync(env,layout,1,1,new RoadOptions(PlannedTown:true));Check(planned.Edges.Count>0&&town.Gates.All(g=>planned.PathTo(g.Id).Length>0),"planned town alternative preserves gates");}
            var plots=await new ParcelNode().RunAsync(env,layout,roads,1,1,1,new());Check(plots.Parcels.Count>20,"usable town plots");
            foreach(var parcel in plots.Parcels){Check(ParcelGeometry.Area(ParcelGeometry.Intersection(parcel.Shape.CopyPaths(),town.BuildingReserve.CopyPaths()))<.02,"plots avoid defense and public reservations");Check(parcel.Yard!=null&&Math.Abs(parcel.Shape.Area-parcel.Buildable.Area-parcel.Yard.Area)<.03,"front building and yard partition");Check(parcel.Uses.Contains("居住"),"mixed domestic uses");}
            reports.Add(new{seed,wallVertices=town.WallLine.Count,wallArea=town.WallFootprint.Area,gates=town.Gates.Count,precincts=town.Precincts.Count,roads=roads.Edges.Count,blocks=plots.Blocks.Count,parcels=plots.Parcels.Count,yards=plots.Parcels.Sum(p=>p.Yard!.Area)});GD.Print($"MEDIEVAL_SEED_PASS seed={seed} gates={town.Gates.Count} parcels={plots.Parcels.Count}");
        }
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/medieval_validation.json"),JsonSerializer.Serialize(new{passed=true,checks,reports},new JsonSerializerOptions{WriteIndented=true}));GD.Print($"MEDIEVAL_VALIDATION_PASS checks={checks}");
    }
}
