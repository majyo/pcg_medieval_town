using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;

public static class ScenePlacementGenerator
{
    public const float ObjectClearance=.25f,PassageMargin=.2f,MaxGroundDifference=.4f;
    private sealed class SpatialIndex
    {
        private readonly Dictionary<(int,int),List<ParcelRegion>> _cells=new();
        private static IEnumerable<(int,int)> Cells(Rect2 b){for(int z=(int)Math.Floor(b.Position.Y/20);z<=Math.Floor(b.End.Y/20);z++)for(int x=(int)Math.Floor(b.Position.X/20);x<=Math.Floor(b.End.X/20);x++)yield return(x,z);}
        public void Add(ParcelRegion shape){foreach(var cell in Cells(shape.Bounds)){if(!_cells.TryGetValue(cell,out var items))_cells[cell]=items=new();items.Add(shape);}}
        public IEnumerable<ParcelRegion> Near(Rect2 bounds){var seen=new HashSet<ParcelRegion>();foreach(var cell in Cells(bounds))if(_cells.TryGetValue(cell,out var items))foreach(var item in items)if(seen.Add(item)&&item.Bounds.Intersects(bounds,true))yield return item;}
        public bool Clear(ParcelRegion shape){var seen=new HashSet<ParcelRegion>();foreach(var cell in Cells(shape.Bounds))if(_cells.TryGetValue(cell,out var items))foreach(var item in items)if(seen.Add(item)&&item.Bounds.Intersects(shape.Bounds,true)&&!BuildingGeometry.Clear(shape.CopyPaths(),item.CopyPaths()))return false;return true;}
    }
    public static async Task<ScenePlacementSet> Generate(BuildingSet input,long revision,ScenePlacementOptions o,PcgContext ctx)
    {
        if(!Enum.IsDefined(o.Scheme)||!float.IsFinite(o.DecorationDensity)||o.DecorationDensity<0||o.DecorationDensity>1||!float.IsFinite(o.YardDensity)||o.YardDensity<0||o.YardDensity>1||o.ObjectBudget<0||o.ObjectBudget>4000)throw new ArgumentException("场景密度、资源方案或对象预算无效");
        async Task Yield(float f,string m){ctx.Cancellation.ThrowIfCancellationRequested();if(ctx.Yield!=null)await ctx.Yield(new(f,m));ctx.Cancellation.ThrowIfCancellationRequested();}
        uint Hash(string key,int slot=0){unchecked{uint h=2166136261u^(uint)o.Seed;foreach(char c in key){h^=c;h*=16777619;}h^=(uint)slot*0x9e3779b9;h^=h>>16;h*=0x7feb352d;h^=h>>15;return h;}}
        float Random(string key,int slot)=>(Hash(key,slot)&0xffffff)/16777216f;
        var env=input.Environment;var roads=input.Parcels.Roads;var layout=roads.Settlement;var town=layout.Town;
        var ground=new List<SceneGroundPatch>();var props=new List<ScenePropPlacement>();var zones=new List<ScenePlacementZone>();var issues=new List<ScenePlacementIssue>();var protectedSpaces=new List<SceneProtectedSpace>();
        var roofs=ParcelGeometry.Union(new Paths64(input.Buildings.SelectMany(b=>b.RoofProjection.CopyPaths())));
        await Yield(0,"校验来源并保护入口、道路和通院");
        protectedSpaces.Add(new("roads","道路",roads.Geometry.Area));
        foreach(var access in input.Access)protectedSpaces.Add(new($"B{access.BuildingId}",access.Kind,access.Corridor));
        if(town!=null){protectedSpaces.Add(new("walls","城墙预留",town.WallReserve));foreach(var gate in town.Gates)protectedSpaces.Add(new(gate.Id,"城门通道",gate.Passage));foreach(var p in town.Precincts)protectedSpaces.Add(new(p.Id,"院区入口",p.Approach));}
        var protectedPaths=ParcelGeometry.Union(new Paths64(protectedSpaces.SelectMany(p=>p.Shape.CopyPaths())));
        var safety=ParcelGeometry.Union(new Paths64(ParcelGeometry.Offset(protectedPaths,PassageMargin).Concat(roofs)));
        var allProtected=new ParcelRegion(safety);
        var groundIndex=new SpatialIndex();var roofIndex=new SpatialIndex();var protectedIndex=new SpatialIndex();
        foreach(var b in input.Buildings){groundIndex.Add(b.Footprint);roofIndex.Add(b.RoofProjection);protectedIndex.Add(b.RoofProjection);}
        if(town?.WallReserve.Area>0)roofIndex.Add(town.WallReserve);
        foreach(var s in roads.Geometry.Surfaces){groundIndex.Add(s.Shape);protectedIndex.Add(new(ParcelGeometry.Offset(s.Shape.CopyPaths(),PassageMargin)));}
        foreach(var s in protectedSpaces.Where(s=>s.Source!="roads"))if(s.Shape.Area>.001)protectedIndex.Add(new(ParcelGeometry.Offset(s.Shape.CopyPaths(),PassageMargin)));
        Rect2 Bounds(Paths64 paths){var points=paths.SelectMany(p=>p.Select(ParcelGeometry.V)).ToArray();var min=points.Aggregate((a,b)=>a.Min(b));var max=points.Aggregate((a,b)=>a.Max(b));return new(min,max-min);}
        // Roads retain exactly their authoritative disjoint surfaces. Other ground
        // owners are subtracted in priority order to avoid coplanar duplicates.
        foreach(var s in roads.Geometry.Surfaces)ground.Add(new(ground.Count,roads.Edges.First(e=>e.Id==s.RoadId).Main?"主路":"街巷",$"R{s.RoadId}",s.Shape,s.RoadId));
        void Ground(string kind,string source,Paths64 shape){if(shape.Count==0)return;var claimed=ParcelGeometry.Union(new Paths64(groundIndex.Near(Bounds(shape)).SelectMany(s=>s.CopyPaths())));var available=ParcelGeometry.Difference(shape,claimed);if(ParcelGeometry.Area(available)<.02)return;var region=new ParcelRegion(available);ground.Add(new(ground.Count,kind,source,region));groundIndex.Add(region);}
        if(town!=null)Ground("城墙占位","walls",town.WallFootprint.CopyPaths());
        foreach(var path in input.Access)Ground("通院地面",$"B{path.BuildingId}",path.Corridor.CopyPaths());
        var plaza=ParcelGeometry.Rect(layout.Plaza);if(layout.Region!=null)plaza=ParcelGeometry.Intersection(plaza,layout.Region.DevelopmentArea.CopyPaths());
        Ground("广场","market",plaza);
        if(town!=null)foreach(var p in town.Precincts)if(input.Buildings.Any(b=>b.PrecinctId==p.Id))Ground("院场",p.Id,p.Shape.CopyPaths());
        foreach(var b in input.Buildings.Where(b=>b.ParcelId.HasValue)){
            var p=input.Parcels.Parcels.First(p=>p.Id==b.ParcelId);if(p.Yard==null)continue;
            Ground(p.Use==ParcelUse.Production?"作坊院地":"后园",$"P{p.Id}",p.Yard.CopyPaths());
            if(b.Id%48==0)await Yield(.1f,"装配道路、建筑与院落地面");
        }
        foreach(var field in roads.Landscape)Ground(field.Pattern switch{"orchard"=>"果园","pasture"=>"草地",_=>"田地"},field.Id,field.Shape.CopyPaths());
        void Zone(string id,string kind,ParcelRegion shape,ParcelRegion site,Vector2 anchor,Vector2 dir,int? parcel=null,string? precinct=null){
            var nearby=ParcelGeometry.Union(new Paths64(protectedIndex.Near(shape.Bounds).SelectMany(s=>s.CopyPaths())));var free=ParcelGeometry.Difference(shape.CopyPaths(),nearby);if(ParcelGeometry.Area(free)<2)return;
            zones.Add(new(id,kind,new(free),site,anchor,dir,parcel,precinct));
        }
        Zone("market","市场",new(plaza),new(ParcelGeometry.Union(new Paths64(plaza.Concat(roads.Geometry.Area.CopyPaths())))),layout.Core,Vector2.Right);
        if(town!=null)foreach(var p in town.Precincts)if(input.Buildings.Any(b=>b.PrecinctId==p.Id))Zone(p.Id,"院区",p.Shape,new(ParcelGeometry.Union(new Paths64(p.Shape.CopyPaths().Concat(p.Approach.CopyPaths())))),p.Access,Vector2.Right,precinct:p.Id);
        foreach(var b in input.Buildings.Where(b=>b.ParcelId.HasValue)){
            var p=input.Parcels.Parcels.First(p=>p.Id==b.ParcelId);var access=input.Access.First(a=>a.BuildingId==b.Id);
            if(p.Yard!=null)Zone($"P{p.Id}/yard",p.Use==ParcelUse.Production?"作坊后院":"后院",p.Yard,p.Shape,access.YardPoint,(b.Corners[1]-b.Corners[0]).Normalized(),p.Id);
            if(p.Use!=ParcelUse.Residential)Zone($"P{p.Id}/front","临街",new(ParcelGeometry.Difference(p.Shape.CopyPaths(),p.Yard?.CopyPaths()??new())),p.Shape,access.StreetPoint,(b.Corners[1]-b.Corners[0]).Normalized(),p.Id);
        }
        var occupied=new SpatialIndex();var routes=new SpatialIndex();int attempts=0;bool budgetReported=false;
        bool GroundAvailable(ParcelRegion shape){var bounds=shape.Bounds;if(!env.Contains(bounds.Position)||!env.Contains(bounds.End))return false;
            int x0=Math.Clamp((int)((bounds.Position.X-env.Bounds.Position.X)/env.StepX),0,env.CellsX-1),x1=Math.Clamp((int)((bounds.End.X-env.Bounds.Position.X)/env.StepX),0,env.CellsX-1);
            int z0=Math.Clamp((int)((bounds.Position.Y-env.Bounds.Position.Y)/env.StepZ),0,env.CellsZ-1),z1=Math.Clamp((int)((bounds.End.Y-env.Bounds.Position.Y)/env.StepZ),0,env.CellsZ-1);
            for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)if(env.Cell(x,z).State!=BuildCandidateState.Candidate&&ParcelGeometry.Area(ParcelGeometry.Intersection(shape.CopyPaths(),ParcelGeometry.Rect(new(env.VertexPosition(x,z),new(env.StepX,env.StepZ)))))>.002)return false;return true;
        }
        string failure="";
        bool Budget(){if(props.Count<o.ObjectBudget)return true;if(!budgetReported){issues.Add(new("scene",layout.Core,"对象预算已用尽，保留未布置空间"));budgetReported=true;}return false;}
        bool TryPlace(string id,ScenePropKind kind,ScenePlacementZone zone,Vector2 center,Vector2 tangent,float width,float depth,float height,bool fence=false){
            attempts++;var inward=tangent.Orthogonal();var corners=BuildingGeometry.Rectangle(center-tangent*width/2-inward*depth/2,tangent,inward,width,depth);
            var footprint=new ParcelRegion(BuildingGeometry.Polygon(corners));
            failure="没有足够的院地或通道净空";if(!BuildingGeometry.Fits(footprint.CopyPaths(),zone.Shape.CopyPaths())||!occupied.Clear(footprint)||!routes.Clear(footprint))return false;
            failure="局部落地高差超限";if(!BuildingGeometry.Ground(input.Terrain,corners,MaxGroundDifference+.06f,out float low,out float high))return false;
            failure="占地涉及受限或未知地面";if(!GroundAvailable(footprint))return false;
            ParcelRegion approach=new(new());
            if(!fence){
                failure="无法从已有入口接入";
                // Connect to the already-proven parcel/public entrance. Reserve the
                // complete approach so later furniture and fences cannot close it.
                var side=Enumerable.Range(0,4).Select(i=>(corners[i]+corners[(i+1)%4])/2).OrderBy(p=>p.DistanceSquaredTo(zone.Anchor)).First();
                if(side.DistanceTo(zone.Anchor)>.05f){approach=new(ParcelGeometry.Difference(new Paths64{ParcelGeometry.Path(RoadSpace.Strip(zone.Anchor,side,1))},footprint.CopyPaths()));
                    if(!BuildingGeometry.Fits(approach.CopyPaths(),zone.Site.CopyPaths())||!roofIndex.Clear(approach)||!occupied.Clear(approach)||!BuildingGeometry.Clear(approach.CopyPaths(),footprint.CopyPaths())||!GroundAvailable(approach))return false;
                }
                if(approach.Area<.01)return false;
            }
            props.Add(new(id,kind,$"builtin/{kind.ToString().ToLowerInvariant()}-v1",zone.Id,zone.ParcelId,zone.PrecinctId,Array.AsReadOnly(corners),footprint,high+.03f,low,high,height,approach));
            occupied.Add(new(ParcelGeometry.Offset(footprint.CopyPaths(),ObjectClearance)));if(approach.Area>.001)routes.Add(new(ParcelGeometry.Offset(approach.CopyPaths(),.1f)));return true;
        }
        await Yield(.25f,"按用途布置公共设施与院落附属物");
        var ordered=zones.OrderBy(z=>z.ParcelId.HasValue?1:0).ThenBy(z=>Hash(z.Id)).ToArray();
        foreach(var zone in ordered){
            float density=zone.Kind is "后院" or "作坊后院"?o.YardDensity:o.DecorationDensity;
            int desired=zone.ParcelId.HasValue?(Random(zone.Id,0)<density?1:0):Math.Min(12,(int)Math.Ceiling(zone.Shape.Area/100*density));
            if(zone.Kind=="作坊后院"&&Random(zone.Id,1)<density*.5f)desired++;
            for(int slot=0;slot<desired&&Budget();slot++){
                var kind=zone.Kind switch{"市场"=>slot==0?ScenePropKind.Well:slot%3==0?ScenePropKind.Bench:ScenePropKind.Stall,"院区"=>slot==0?ScenePropKind.Well:ScenePropKind.Bench,"临街"=>ScenePropKind.Storage,_=>Random(zone.Id,slot+40)<.18f&&zone.Shape.Area>80?ScenePropKind.Shed:ScenePropKind.Storage};
                var size=kind switch{ScenePropKind.Well=>new Vector3(1.8f,1.8f,1),ScenePropKind.Stall=>new Vector3(2.6f,2.2f,2.6f),ScenePropKind.Bench=>new Vector3(1.8f,.65f,.85f),ScenePropKind.Shed=>new Vector3(3,2.6f,2.8f),_=>new Vector3(1.4f,1.1f,1.25f)};
                bool placed=false;var rejected=new HashSet<string>();for(int trial=0;trial<18;trial++){
                    var bounds=zone.Shape.Bounds;var point=bounds.Position+bounds.Size*new Vector2(Random(zone.Id,100+slot*50+trial*2),Random(zone.Id,101+slot*50+trial*2));
                    if(TryPlace($"{zone.Id}/prop{slot}",kind,zone,point,zone.Direction,size.X,size.Y,size.Z)){placed=true;break;}
                    rejected.Add(failure);
                }
                if(!placed)issues.Add(new(zone.Id,zone.Shape.Center,string.Join("；",rejected.OrderBy(s=>s,StringComparer.Ordinal)),18));
            }
            if(attempts%50<20)await Yield(.45f,"复核附属物占地、落地和入口");
        }
        await Yield(.65f,"沿宅地边界布置带开口的围栏");
        if(o.Fences)foreach(var zone in ordered.Where(z=>z.Kind is "后院" or "作坊后院")){
            if(!Budget())break;if(Random(zone.Id,909)>.3f)continue;
            var parcel=input.Parcels.Parcels.First(p=>p.Id==zone.ParcelId);int count=0;
            foreach(var ring in parcel.Shape.Rings)for(int edge=0;edge<ring.Count&&count<8;edge++){
                var a=ring[edge];var b=ring[(edge+1)%ring.Count];float length=a.DistanceTo(b);if(length<2)continue;var t=(b-a)/length;var normal=t.Orthogonal();if(!parcel.Shape.Contains((a+b)/2+normal*.3f))normal=-normal;
                int segments=Math.Min(20,(int)Math.Ceiling(length/5));for(int k=0;k<segments&&count<8&&Budget();k++){
                    var center=a.Lerp(b,(k+.5f)/segments)+normal*.2f;
                    if(TryPlace($"{zone.Id}/fence{edge}_{k}",ScenePropKind.Fence,zone,center,t,length/segments-.35f,.14f,1.05f,true))count++;
                }
            }
            if(props.Count%40<8)await Yield(.75f,"为通院、入口和附属物保留围栏开口");
        }
        await Yield(.85f,"汇总植被避让与来源清单");
        var maskPaths=o.AvoidVegetation?ParcelGeometry.Union(new Paths64(safety.Concat(ground.SelectMany(g=>g.Shape.CopyPaths())).Concat(props.SelectMany(p=>p.Footprint.CopyPaths())))):new Paths64();
        // Natural vegetation is filtered by the host using this mask; the source
        // terrain and tree placements remain untouched.
        var mask=new ParcelRegion(maskPaths,triangulate:false);
        await Yield(1,"场景布置完成");return new(input,revision,o,ground,props,zones,protectedSpaces,allProtected,mask,issues,attempts);
    }
}
