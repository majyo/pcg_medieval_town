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
public static class BuildingValidation
{
    private sealed class Fixture:IEnvironmentSource
    {
        public string SourceId=>"building_fixture";public long Revision=>1;public Rect2 Bounds=>new(-100,-100,200,200);public float? NativeSpacing=>10;
        public EnvironmentLayers Layers=>new(LayerStatus.Provided,LayerStatus.Provided,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty);
        public IReadOnlyList<ConstraintPolygon> NoBuildZones=>Array.Empty<ConstraintPolygon>();public IReadOnlyList<ConstraintPolygon> Obstacles=>Array.Empty<ConstraintPolygon>();
        public Func<Vector2,float> Height=p=>0;public bool Contains(Vector2 p)=>p.X>=-100&&p.X<=100&&p.Y>=-100&&p.Y<=100;public EnvironmentPoint Query(Vector2 p)=>new(Height(p),0,false,0);
    }
    private static ParcelLayout FixtureParcels(CityEnvironmentData e,float angle=0,bool hole=false,bool multi=false)
    {
        Vector2 Rotate(Vector2 p)=>p.Rotated(angle);
        ParcelRegion Region(params Vector2[] p)=>new(new Paths64{ParcelGeometry.Path(p.Select(Rotate))});
        var cells=Enumerable.Range(0,e.CellsX*e.CellsZ).Select(_=>new LayoutCell(SettlementCellState.Included,EnvironmentRestriction.None,0,false)).ToArray();
        var layout=new SettlementLayout(e,"fixture",1,new SettlementOptions(Vector2.Zero,Regional:false),Rotate(new(4,4)),cells,Array.Empty<LayoutEdge>(),Array.Empty<LayoutEntry>(),Array.Empty<LayoutEdge>(),Array.Empty<string>());
        var road=new RoadEdge(0,0,1,Array.AsReadOnly(new[]{Rotate(new Vector2(-10,-2)),Rotate(new Vector2(40,-2))}),true,4,50,0,StreetRole.MarketStreet);
        var roads=new RoadNetwork(e,layout,1,1,new(),new[]{new RoadVertex(0,road.Points[0],0,"core"),new RoadVertex(1,road.Points[1],0,"entry")},new[]{road},new(){{"main_core",0}},new(){RoadSpace.Strip(road.Points[0],road.Points[1],4)},new());
        var parcels=new List<LandParcel>();for(int i=0;i<3;i++){
            float x=i*8;var shape=Region(new(x,0),new(x+8,0),new(x+8,35),new(x,35));var band=Region(new(x,0),new(x+8,0),new(x+8,14),new(x,14));
            if(i==2&&hole)band=new ParcelRegion(ParcelGeometry.Difference(band.CopyPaths(),Region(new(x+3,3),new(x+7,3),new(x+7,11),new(x+3,11)).CopyPaths()));
            if(i==2&&multi)band=new ParcelRegion(ParcelGeometry.Union(new Paths64(Region(new(x,0),new(x+3,0),new(x+3,14),new(x,14)).CopyPaths().Concat(Region(new(x+5,0),new(x+8,0),new(x+8,14),new(x+5,14)).CopyPaths()))));
            var front=new StreetFront(0,Rotate(new(x,0)),Rotate(new(x+8,0)));parcels.Add(new(i,0,ParcelUse.Residential,shape,band,Array.AsReadOnly(new[]{front}),0,0,0,new ParcelRegion(ParcelGeometry.Difference(shape.CopyPaths(),band.CopyPaths())),true,true,"fixture"));
        }
        return new(roads,1,new(),new(){new(0,Region(new(0,0),new(24,0),new(24,35),new(0,35)))},parcels,new());
    }
    public static async Task Run(MapSettings original)
    {
        int checks=0;void Check(bool ok,string m){checks++;if(!ok)throw new InvalidOperationException("BUILDING_TEST_FAILED: "+m);}
        void Geometry(BuildingSet d,bool requirePublic)
        {
            var input=d.Parcels;var byId=d.Buildings.ToDictionary(b=>b.Id);
            Check(byId.Count==d.Buildings.Count&&d.OrdinaryCount>0,"unique buildings with ordinary houses");
            foreach(var b in d.Buildings){
                Check(b.BaseY-b.MinGround<=d.Options.MaxFoundation+.001f&&b.BaseY>=b.MaxGround,"bounded foundation based on actual footprint");
                Check(b.WallHeight>0&&b.RoofHeight>0&&b.Corners.Count==4,"valid wall and roof heights");
                if(b.ParcelId is int id){var parcel=input.Parcels.First(p=>p.Id==id);Check(BuildingGeometry.Fits(b.Footprint.CopyPaths(),parcel.Buildable.CopyPaths()),"house in actual buildable region");Check(BuildingGeometry.Fits(b.RoofProjection.CopyPaths(),parcel.Buildable.CopyPaths()),"roof inside permitted band");Check(parcel.Frontage.Any(f=>BuildingGeometry.DistanceToSegment(b.StreetPoint,f.A,f.B)<.02f),"door connected to actual frontage");
                    Check(parcel.Yard==null||BuildingGeometry.Clear(b.Footprint.CopyPaths(),parcel.Yard.CopyPaths()),"retained yard");
                }else{var precinct=input.Settlement.Town!.Precincts.First(p=>p.Id==b.PrecinctId);Check(BuildingGeometry.Fits(b.Footprint.CopyPaths(),precinct.Shape.CopyPaths()),"institution stays in its site");}
                if(input.Settlement.Town is {} town)Check(BuildingGeometry.Clear(b.RoofProjection.CopyPaths(),town.WallReserve.CopyPaths()),"wall corridor clearance");
            }
            for(int i=0;i<d.Buildings.Count;i++)for(int j=i+1;j<d.Buildings.Count;j++){
                var a=d.Buildings[i];var b=d.Buildings[j];if(!a.RoofProjection.Bounds.Intersects(b.RoofProjection.Bounds,true))continue;
                Check(ParcelGeometry.Area(ParcelGeometry.Intersection(a.RoofProjection.CopyPaths(),b.RoofProjection.CopyPaths()))<.01,"buildings and roofs do not overlap");
            }
            foreach(var path in d.Access){var b=byId[path.BuildingId];Check(path.Corridor.Area>0,"real access corridor");
                Paths64 allowed;
                if(b.ParcelId.HasValue){allowed=ParcelGeometry.Union(new Paths64(path.Parcels.SelectMany(id=>input.Parcels.First(p=>p.Id==id).Shape.CopyPaths())));var p=input.Parcels.First(p=>p.Id==b.ParcelId);Check(p.Yard!=null&&p.Yard.Contains(path.YardPoint),"passage actually reaches owned yard");Check(path.Parcels.Contains(b.ParcelId.Value),"shared access declares owners");}
                else{var p=input.Settlement.Town!.Precincts.First(p=>p.Id==b.PrecinctId);allowed=ParcelGeometry.Union(new Paths64(p.Shape.CopyPaths().Concat(p.Approach.CopyPaths())));}
                Check(BuildingGeometry.Fits(path.Corridor.CopyPaths(),allowed),"access stays within explicit rights of way");
                foreach(var body in d.Buildings.Where(x=>x.Footprint.Bounds.Intersects(path.Corridor.Bounds,true)))Check(BuildingGeometry.Clear(path.Corridor.CopyPaths(),body.Footprint.CopyPaths()),"access not through solid houses");
            }
            Check(d.Access.Count==d.Buildings.Count,"every building has access record");
            foreach(var s in d.SharedWalls)Check(byId.ContainsKey(s.A)&&byId.ContainsKey(s.B)&&s.A!=s.B&&s.Start.DistanceTo(s.End)>1&&s.Owner==Math.Min(s.A,s.B),"shared wall identity and ownership");
            if(requirePublic){Check(d.Buildings.Any(b=>b.PrecinctId=="church")&&d.Buildings.Any(b=>b.PrecinctId=="abbey"),"required institutions");Check(d.OpenSpaces.Any(s=>s.Source=="abbey"&&s.Kind=="院场"&&s.Shape.Area>100),"open institutional court");}
            var mesh=BuildingMeshBuilder.Build(d);Check(mesh.Positions.Count>0&&mesh.Positions.Count%3==0,"renderable triangles");
            var safeRoof=byId.ToDictionary(p=>p.Key,p=>ParcelGeometry.Offset(p.Value.RoofProjection.CopyPaths(),.02f));var safeFoot=byId.ToDictionary(p=>p.Key,p=>ParcelGeometry.Offset(p.Value.Footprint.CopyPaths(),.02f));
            bool validMesh=true,winding=true;
            for(int i=0;i<mesh.Positions.Count;i++){
                var p=mesh.Positions[i];if(!p.IsFinite()||!mesh.Normals[i].IsFinite()||mesh.Normals[i].LengthSquared()<.9)validMesh=false;
                if(mesh.Parts[i] is "wall" or "gable" or "roof")if(!ParcelGeometry.Contains(mesh.Parts[i]=="roof"?safeRoof[mesh.Owners[i]]:safeFoot[mesh.Owners[i]],new Vector2(p.X,p.Z)))validMesh=false;
                if(mesh.Parts[i]=="roof"&&p.Y>byId[mesh.Owners[i]].BaseY+byId[mesh.Owners[i]].WallHeight+byId[mesh.Owners[i]].RoofHeight+.001f)validMesh=false;
                if(i%3==0&&(mesh.Positions[i+1]-p).Cross(mesh.Positions[i+2]-p).Dot(mesh.Normals[i])>1e-5)winding=false;
            }
            Check(validMesh,"mesh projections, heights and normals match building data");Check(winding,"clockwise winding matches outward normals");
        }
        var reports=new List<object>();
        foreach(int seed in new[]{73129,7,22017}){
            var settings=(MapSettings)original.Duplicate();settings.Seed=seed;var map=new TemperateMapData(settings);var source=new TemperateEnvironmentSource(map,"buildings",1,Array.Empty<ConstraintPolygon>(),Array.Empty<ConstraintPolygon>());
            var env=EnvironmentSampler.Sample(source,new(source.Bounds));var layout=await new SettlementLayoutNode().RunAsync(env,1,new(Vector2.Zero));var roads=await new RoadNode().RunAsync(env,layout,1,1,new());var parcels=await new ParcelNode().RunAsync(env,layout,roads,1,1,1,new());
            var node=new BuildingNode();var options=new BuildingOptions();var d=await node.RunAsync(env,parcels,1,1,options);Geometry(d,true);
            Check(d.InsideCount>20&&d.OutsideCount>d.InsideCount,"city and substantial extramural buildings");Check(d.SharedWalls.Count>0,"real shared-wall cases");
            if(seed==73129){
                var repeat=await node.RunAsync(env,parcels,1,1,options);Check(d.Buildings.Select(b=>(b.ParcelId,b.BaseY,b.WallHeight,b.RoofHeight,b.Footprint.Center)).SequenceEqual(repeat.Buildings.Select(b=>(b.ParcelId,b.BaseY,b.WallHeight,b.RoofHeight,b.Footprint.Center))),"deterministic rerun");
                Check(!node.IsCurrent(parcels,2,options)&&!node.IsCurrent(parcels,1,options with{HeightScale=1.1f}),"signature checks");long rev=node.ResultRevision;
                using(var cancel=new CancellationTokenSource()){try{await node.RunAsync(env,parcels,1,1,options,new(cancel.Token,p=>{cancel.Cancel();return Task.CompletedTask;}));}catch(OperationCanceledException){}Check(node.State==SampleNodeState.Cancelled&&node.ResultRevision==rev&&ReferenceEquals(repeat,node.LastResult),"cancel no partial publication");}
                bool rejected=false;try{await node.RunAsync(env,parcels,2,1,options);}catch(ArgumentException){rejected=true;}Check(rejected&&node.ResultRevision==rev,"mismatched environment rejected");
                try{await node.RunAsync(env,parcels,1,1,options,new(default,p=>{node.Invalidate();return Task.CompletedTask;}));}catch(InvalidOperationException){}Check(node.State==SampleNodeState.Failed&&node.ResultRevision==rev,"input change rejects publication");
                var detached=await node.RunAsync(env,parcels,1,1,options with{PartyWalls=false,Institutions=false});Check(detached.SharedWalls.Count==0&&detached.Buildings.All(b=>b.ParcelId.HasValue),"optional rules disabled");
            }
            reports.Add(new{seed,ordinary=d.OrdinaryCount,institutions=d.Buildings.Count-d.OrdinaryCount,inside=d.InsideCount,outside=d.OutsideCount,shared=d.SharedWalls.Count,issues=d.Issues.GroupBy(i=>i.Reason).ToDictionary(g=>g.Key,g=>g.Count())});GD.Print($"BUILDING_SEED_PASS seed={seed} ordinary={d.OrdinaryCount} institutions={d.Buildings.Count-d.OrdinaryCount} shared={d.SharedWalls.Count}");
        }
        var fixture=new Fixture();var flat=EnvironmentSampler.Sample(fixture,new(fixture.Bounds));var model=FixtureParcels(flat);var fixtureNode=new BuildingNode();var paired=await fixtureNode.RunAsync(flat,model,1,1,new());Geometry(paired,false);Check(paired.SharedWalls.Count>0&&paired.Access.Any(a=>a.Parcels.Count==2),"shared lane and wall fixture");
        var angled=FixtureParcels(flat,.53f);var rotated=await fixtureNode.RunAsync(flat,angled,1,1,new());Geometry(rotated,false);Check(rotated.Buildings.All(b=>Math.Abs(b.Inward.Dot(new Vector2(0,1).Rotated(.53f)))>.999f),"actual slanted frontage orientation");
        foreach(bool multi in new[]{false,true}){var irregular=FixtureParcels(flat,0,!multi,multi);var d=await fixtureNode.RunAsync(flat,irregular,1,1,new());Geometry(d,false);Check(d.Buildings.All(b=>b.ParcelId!=2)&&d.Issues.Any(i=>i.Source=="P2"),"hole or disconnected band cannot be bridged");}
        fixture.Height=p=>Math.Max(0,p.X-12)*.5f;var slope=EnvironmentSampler.Sample(fixture,new(fixture.Bounds));var steep=await fixtureNode.RunAsync(slope,FixtureParcels(slope),1,1,new());Check(steep.Buildings.All(b=>b.ParcelId!=2),"local excessive foundation rejected");
        string dir=ProjectSettings.GlobalizePath("res://artifacts");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"building_validation.json"),JsonSerializer.Serialize(new{passed=true,checks,reports},new JsonSerializerOptions{WriteIndented=true}));GD.Print($"BUILDING_VALIDATION_PASS checks={checks}");
    }
}
