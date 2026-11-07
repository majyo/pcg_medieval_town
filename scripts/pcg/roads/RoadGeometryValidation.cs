using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;

public static class RoadGeometryValidation
{
    private sealed class Fixture:IEnvironmentSource
    {
        public string SourceId=>"road_shape_fixture";public long Revision=>1;public Rect2 Bounds=>new(0,0,100,100);public float? NativeSpacing=>10;
        public EnvironmentLayers Layers=>new(LayerStatus.Provided,LayerStatus.Provided,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty,LayerStatus.KnownEmpty);
        public IReadOnlyList<ConstraintPolygon> NoBuildZones=>Array.Empty<ConstraintPolygon>();public IReadOnlyList<ConstraintPolygon> Obstacles=>Array.Empty<ConstraintPolygon>();
        public Func<Vector2,float> Height=p=>0;public bool Contains(Vector2 p)=>p.X>=0&&p.Y>=0&&p.X<=100&&p.Y<=100;public EnvironmentPoint Query(Vector2 p)=>new(Height(p),0,false,0);
    }
    public static async Task Run(RoadNetwork real)
    {
        int checks=0;void Check(bool ok,string m){checks++;if(!ok)throw new InvalidOperationException("ROAD_GEOMETRY_TEST_FAILED: "+m);}
        void Geometry(RoadNetwork d,bool mesh=false){var g=d.Geometry;
            Check(g.Area.Area>0&&g.Surfaces.Count>0&&g.Boundaries.Count>0,"nonempty canonical geometry");
            var union=ParcelGeometry.Union(new Paths64(g.Surfaces.SelectMany(s=>s.Shape.CopyPaths())));
            Check(Math.Abs(g.Surfaces.Sum(s=>s.Shape.Area)-g.Area.Area)<.1,"disjoint surface ownership");
            Check(ParcelGeometry.Area(ParcelGeometry.Difference(union,g.Area.CopyPaths()))<.02&&ParcelGeometry.Area(ParcelGeometry.Difference(g.Area.CopyPaths(),union))<.02,"surfaces cover exactly the canonical road area");
            var space=new RoadSpace(d.Environment,d.Settlement,d.Options);Check(d.Footprint.All(t=>space.PolygonValid(t.ToArray())),"full area respects environmental restrictions");
            foreach(var boundary in g.Boundaries){Check(d.Edges.Any(e=>e.Id==boundary.RoadId)&&boundary.A.IsFinite()&&boundary.B.IsFinite()&&boundary.A.DistanceTo(boundary.B)>.0001f,"valid boundary source");
                if(boundary.NodeId.HasValue)Check(!boundary.AllowsFrontage,"junction boundary excluded from frontage");
            }
            foreach(var e in d.Edges){Check(e.Points[0].DistanceTo(d.Nodes.First(n=>n.Id==e.From).Position)<.002f&&e.Points[^1].DistanceTo(d.Nodes.First(n=>n.Id==e.To).Position)<.002f,"topological endpoints fixed");
                Check(e.MaxGrade<=d.Options.MaxGrade+.001f,"shaped centerline grade");
            }
            if(mesh){var drawn=RoadSurfaceMesh.Build(d);var triangles=new Paths64();double area=0;bool valid=true;
                for(int i=0;i<drawn.Positions.Count;i+=3){var a=drawn.Positions[i];var b=drawn.Positions[i+1];var c=drawn.Positions[i+2];var flat=new[]{new Vector2(a.X,a.Z),new Vector2(b.X,b.Z),new Vector2(c.X,c.Z)};var path=ParcelGeometry.Path(flat);triangles.Add(path);area+=ParcelGeometry.Area(new Paths64{path});
                    if(!a.IsFinite()||!b.IsFinite()||!c.IsFinite())valid=false;
                    var center=(a+b+c)/3;if(!d.Environment.TryHeightAt(new(center.X,center.Z),out float h)||Math.Abs(h-center.Y)>.002f)valid=false;
                }
                Check(valid,"surface triangles follow sampled terrain without bridging");Check(Math.Abs(area-g.Area.Area)<.15,$"mesh has no duplicate or missing road area: mesh={area} canonical={g.Area.Area}");
                Check(ParcelGeometry.Area(ParcelGeometry.Difference(ParcelGeometry.Union(triangles),g.Area.CopyPaths()))<.1,"mesh remains in canonical area");
            }
        }
        Geometry(real,true);Check(real.Geometry.SmoothedCorners>0,"default town contains smoothed turns");
        var source=new Fixture();var env=EnvironmentSampler.Sample(source,new(source.Bounds));
        SettlementLayout Layout(CityEnvironmentData data,Func<int,int,bool>? include=null,bool entry=false){var cells=Enumerable.Range(0,100).Select(k=>new LayoutCell(include==null||include(k%10,k/10)?SettlementCellState.Included:SettlementCellState.Restricted,EnvironmentRestriction.None,0,false)).ToArray();
            var opening=new LayoutEdge(20,new(0,20),new(0,30),Vector2.Left,true);return new(data,"fixture",1,new(new(50,50),Walls:false,CivicPrecincts:false,Regional:false),new(50,50),cells,Array.Empty<LayoutEdge>(),entry?new[]{new LayoutEntry("E1",new(0,25),0,opening)}:Array.Empty<LayoutEntry>(),Array.Empty<LayoutEdge>(),Array.Empty<string>());}
        RoadEdge Edge(int id,int from,int to,float width,params Vector2[] points)=>new(id,from,to,Array.AsReadOnly(points),width>=6,width,0,0);
        async Task<RoadNetwork> Shape(RoadEdge[] edges,SettlementLayout? layout=null,RoadOptions? options=null){layout??=Layout(env);options??=new();var nodes=edges.SelectMany(e=>new[]{new RoadVertex(e.From,e.Points[0],0,"fixture"),new RoadVertex(e.To,e.Points[^1],0,"fixture")}).DistinctBy(n=>n.Id).ToArray();var result=await RoadGeometryPlanner.Finalize(layout.Environment,layout,nodes,edges,options,new());
            return new(layout.Environment,layout,1,1,options,nodes,result.Edges,new(){{layout.CoreId,nodes[0].Id}},new(),new(),result.Geometry);}
        var straight=await Shape(new[]{Edge(0,0,1,6,new(0,25),new(90,25))},Layout(env,entry:true));Geometry(straight);
        Check(Math.Abs(straight.Geometry.Area.Area-540)<.01&&!straight.Contains(new(-.01f,25)),"flat regional entry cap");
        var bendEdges=new[]{Edge(0,0,1,6,new(15,20),new(50,20),new(50,80))};var bend=await Shape(bendEdges);Geometry(bend,true);
        Check(bend.Geometry.SmoothedCorners==1&&bend.Edges[0].Points.Count>3,"bounded turn smoothing");
        var tEdges=new[]{Edge(0,0,1,8,new(50,50),new(10,50)),Edge(1,0,2,8,new(50,50),new(90,50)),Edge(2,0,3,3,new(50,50),new(50,10))};
        var junction=await Shape(tEdges);Geometry(junction,true);Check(junction.Geometry.JunctionCount==1,"one unified T junction");
        Check(junction.Contains(new(53,48))&&!junction.Contains(new(53,25)),"branch width transitions to major road");
        Check(junction.Geometry.Boundaries.Where(b=>b.AllowsFrontage).All(b=>junction.Geometry.FrontageAt(b.A,b.B)?.RoadId==b.RoadId),"exact frontage lookup");
        var x=await Shape(tEdges.Append(Edge(3,0,4,3,new(50,50),new(50,90))).ToArray());Geometry(x);Check(x.Geometry.JunctionCount==1,"one unified four-way junction");
        var acute=await Shape(new[]{Edge(0,0,1,6,new(20,30),new(85,30)),Edge(1,0,2,3,new(20,30),new(80,47))});Geometry(acute);
        Check(acute.Geometry.Area.Bounds.Position.X>=17&&acute.Geometry.Area.Bounds.End.X<=88,"acute junction stays within endpoint half-width envelope");
        var narrow=await Shape(new[]{Edge(0,0,1,6,new(10,25),new(90,25))},Layout(env,(x,z)=>z==2));Geometry(narrow);
        var constrained=await Shape(new[]{Edge(0,0,1,4,new(15,38),new(52,38),new(52,85))},Layout(env,(x,z)=>!(x==4&&z==4)),new(CornerRadius:12));Geometry(constrained);
        Check(!constrained.Contains(new(45,45)),"smoothing never cuts restricted inner corner");
        var noSmooth=await Shape(bendEdges,options:new(CornerRadius:0,JunctionFlare:0));Check(noSmooth.Edges[0].Points.SequenceEqual(bendEdges[0].Points),"zero smoothing preserves planned path");
        var closeRoads=await Shape(bendEdges.Append(Edge(1,2,3,1,new(35,25),new(46,25))).ToArray(),options:new(CornerRadius:12));
        Check(closeRoads.Edges[0].Points.SequenceEqual(bendEdges[0].Points)&&closeRoads.Geometry.Notices.Any(n=>n.RoadId==0),"smoothing cannot create a new physical junction with an unrelated nearby road");
        var repeat=await Shape(tEdges);Check(junction.Geometry.Boundaries.SequenceEqual(repeat.Geometry.Boundaries)&&junction.Geometry.Area.Area==repeat.Geometry.Area.Area,"deterministic final boundaries");
        source.Height=p=>p.X*.025f+Math.Max(0,10-Math.Abs(p.Y-45))*.05f;var slope=EnvironmentSampler.Sample(source,new(source.Bounds));var terrain=await Shape(bendEdges,Layout(slope));Geometry(terrain,true);
        var node=new RoadNode();var published=await node.RunAsync(real.Environment,real.Settlement,real.EnvironmentRevision,real.SettlementRevision,real.Options);long revision=node.ResultRevision;
        using(var cancellation=new CancellationTokenSource()){
            try{await node.RunAsync(real.Environment,real.Settlement,real.EnvironmentRevision,real.SettlementRevision,real.Options,new(cancellation.Token,p=>{if(p.Fraction>=.9f)cancellation.Cancel();return Task.CompletedTask;}));}catch(OperationCanceledException){}
            Check(node.State==SampleNodeState.Cancelled&&ReferenceEquals(node.LastResult,published)&&node.ResultRevision==revision,"late geometry cancellation publishes no partial result");
        }
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/road_geometry_validation.json"),JsonSerializer.Serialize(new{passed=true,checks,area=real.Geometry.Area.Area,surfaces=real.Geometry.Surfaces.Count,junctions=real.Geometry.JunctionCount,boundaries=real.Geometry.Boundaries.Count,smoothed=real.Geometry.SmoothedCorners,notices=real.Geometry.Notices.GroupBy(n=>n.Reason).ToDictionary(g=>g.Key,g=>g.Count())},new JsonSerializerOptions{WriteIndented=true}));
        GD.Print($"ROAD_GEOMETRY_VALIDATION_PASS checks={checks} smoothed={real.Geometry.SmoothedCorners} junctions={real.Geometry.JunctionCount}");
    }
}
