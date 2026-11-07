using Godot;
using Clipper2Lib;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;
public enum TownDistrictRole { FortifiedCore,GateSuburb,InstitutionSuburb }
public sealed record TownDistrict(string Id,string Name,TownDistrictRole Role,ParcelRegion Shape,Vector2 Anchor,float StreetSpacing,float PlotDepth);
public sealed record RegionalLink(string Id,string Name,ReadOnlyCollection<Vector2> Points,string? GateId);
public sealed record LandscapeParcel(string Id,string Use,ParcelRegion Shape,Vector2 Access,ReadOnlyCollection<Vector2> AccessRoute,string Pattern="arable",Vector2 Direction=default,ReadOnlyCollection<ReadOnlyCollection<Vector2>>? CultivationLines=null);
public sealed class RegionalTownPlan
{
    public Rect2 StudyArea {get;}
    public ReadOnlyCollection<TownDistrict> Districts {get;}
    public ReadOnlyCollection<RegionalLink> Links {get;}
    public ReadOnlyCollection<LandscapeParcel> Landscape {get;}
    public ParcelRegion DevelopmentArea {get;}
    public Rect2 ViewBounds {get;}
    public RegionalTownPlan(Rect2 study,List<TownDistrict> districts,List<RegionalLink> links,List<LandscapeParcel> landscape){StudyArea=study;Districts=Array.AsReadOnly(districts.ToArray());Links=Array.AsReadOnly(links.ToArray());Landscape=Array.AsReadOnly(landscape.ToArray());DevelopmentArea=new(ParcelGeometry.Union(new Paths64(districts.SelectMany(d=>d.Shape.CopyPaths()))));ViewBounds=DevelopmentArea.Bounds;}
    public TownDistrict? DistrictAt(Vector2 p)=>Districts.FirstOrDefault(d=>d.Shape.Bounds.HasPoint(p)&&d.Shape.Contains(p));
}
public static class RegionalTownGenerator
{
    public static async Task<SettlementLayout> Generate(CityEnvironmentData e,string node,long revision,SettlementOptions o,PcgContext ctx){
        if(!float.IsFinite(o.CoreRadius)||o.CoreRadius<90||o.CoreRadius>o.Radius||!float.IsFinite(o.SuburbLength)||o.SuburbLength<150||o.SuburbLength>1500||!float.IsFinite(o.SuburbWidth)||o.SuburbWidth<50||o.SuburbWidth>300||o.GateCount<1||o.GateCount>4)throw new ArgumentException("核心尺度、城外街带或城门数量无效");
        async Task Yield(float p,string text){ctx.Cancellation.ThrowIfCancellationRequested();if(ctx.Yield!=null)await ctx.Yield(new(p,text));ctx.Cancellation.ThrowIfCancellationRequested();}
        // The legacy selector is used only to locate the market. Its old single-component town footprint is discarded.
        var seed=await SettlementGenerator.Generate(e,node,revision,o with{Regional=false,Walls=false,CivicPrecincts=false,ManualEntries=""},ctx);
        int nx=e.CellsX,nz=e.CellsZ,n=nx*nz;Vector2 P(int k)=>e.VertexPosition(k%nx,k/nx)+new Vector2(e.StepX,e.StepZ)/2;
        int Index(Vector2 p)=>Math.Clamp((int)((p.Y-e.Bounds.Position.Y)/e.StepZ),0,nz-1)*nx+Math.Clamp((int)((p.X-e.Bounds.Position.X)/e.StepX),0,nx-1);
        var cells=new LayoutCell[n];for(int k=0;k<n;k++){var ec=e.Cell(k%nx,k/nx);cells[k]=seed.Cells[k] with{State=ec.State==BuildCandidateState.Candidate?SettlementCellState.Included:ec.State==BuildCandidateState.Blocked?SettlementCellState.Restricted:SettlementCellState.Unknown};}
        IEnumerable<int> Neighbors(int k){int x=k%nx,z=k/nx;if(x>0)yield return k-1;if(x<nx-1)yield return k+1;if(z>0)yield return k-nx;if(z<nz-1)yield return k+nx;}
        var distance=new Dictionary<int,float>{{Index(seed.Core),0}};var previous=new Dictionary<int,int>();var queue=new PriorityQueue<int,(float,int)>();queue.Enqueue(Index(seed.Core),(0,Index(seed.Core)));int visits=0;
        while(queue.TryDequeue(out int k,out var priority)){if(priority.Item1>distance[k]+.001f)continue;foreach(int j in Neighbors(k)){if(cells[j].State!=SettlementCellState.Included)continue;float cost=distance[k]+P(k).DistanceTo(P(j))*(1+(e.Cell(j%nx,j/nx).MaxSlope??0)/20);if(distance.TryGetValue(j,out float old)&&old<=cost)continue;distance[j]=cost;previous[j]=k;queue.Enqueue(j,(cost,j));}if(++visits%4096==0)await Yield(.25f,"识别区域交通与各岸土地");}
        var portalCandidates=new List<LayoutEdge>();
        foreach(int k in distance.Keys.Order()){
            int x=k%nx,z=k/nx;Vector2 a=e.VertexPosition(x,z),b=e.VertexPosition(x+1,z+1);
            if(x==0)portalCandidates.Add(new(k,a,new Vector2(a.X,b.Y),Vector2.Left,true));if(x==nx-1)portalCandidates.Add(new(k,new Vector2(b.X,a.Y),b,Vector2.Right,true));if(z==0)portalCandidates.Add(new(k,a,new Vector2(b.X,a.Y),Vector2.Up,true));if(z==nz-1)portalCandidates.Add(new(k,new Vector2(a.X,b.Y),b,Vector2.Down,true));
        }
        var portals=new List<LayoutEdge>();
        if(!string.IsNullOrWhiteSpace(o.ManualEntries))foreach(string item in o.ManualEntries.Split(';',StringSplitOptions.RemoveEmptyEntries)){
            var xy=item.Split(',');if(xy.Length!=2||!float.TryParse(xy[0],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float x)||!float.TryParse(xy[1],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float z))throw new ArgumentException("区域入口格式：X,Z;X,Z");var hit=portalCandidates.Where(p=>((p.A+p.B)/2).DistanceTo(new(x,z))<.05f).ToArray();if(hit.Length==0)throw new ArgumentException("手动区域入口须在采样边界的可达陆地上");portals.Add(hit[0]);
        }else foreach(var candidate in portalCandidates.OrderBy(p=>distance[p.Cell]).ThenBy(p=>p.Cell)){
            Vector2 p=(candidate.A+candidate.B)/2;if(portals.Any(q=>Math.Abs((p-seed.Core).AngleTo((q.A+q.B)/2-seed.Core))<.85f))continue;portals.Add(candidate);if(portals.Count==o.EntryCount)break;
        }
        if(portals.Count==0)throw new InvalidOperationException("研究范围没有可达的区域交通入口");
        var entries=portals.Select((p,i)=>{var position=(p.A+p.B)/2;e.TryHeightAt(position,out float h);return new LayoutEntry($"E{i+1}",position,h,p);}).ToArray();
        var result=new SettlementLayout(e,node,revision,o,seed.Core,cells,seed.Boundary.ToArray(),entries,portalCandidates.ToArray(),Array.Empty<string>());
        var transportLand=ParcelGeometry.SettlementCells(result);
        // A separately sized core, independent of regional development and map extent.
        var coreCells=cells.Select((c,k)=>c with{State=c.State==SettlementCellState.Included&&P(k).DistanceTo(seed.Core)<=o.CoreRadius?SettlementCellState.Included:SettlementCellState.Outside}).ToArray();
        var coreInput=new SettlementLayout(e,node,revision,o with{Regional=false,Radius=o.CoreRadius,CivicPrecincts=false},seed.Core,coreCells,Array.Empty<LayoutEdge>(),entries.Take(Math.Min(o.GateCount,entries.Length)).ToArray(),Array.Empty<LayoutEdge>(),Array.Empty<string>());
        var fort=MedievalPlanner.Build(coreInput);var coreArea=ParcelGeometry.Intersection(fort.Interior.CopyPaths(),transportLand);
        var districts=new List<TownDistrict>{new("core","设防核心",TownDistrictRole.FortifiedCore,new(coreArea),seed.Core,60,42)};
        var links=new List<RegionalLink>();var precincts=new List<TownPrecinct>();var landscapes=new List<LandscapeParcel>();
        var gateAssignments=new Dictionary<string,TownGate>();foreach(var entry in entries)if(fort.Gates.Count>0)gateAssignments[entry.Id]=fort.Gates.OrderBy(g=>g.Position.DistanceSquaredTo(entry.Position)).First();
        var obstacles=fort.RoadObstacles.CopyPaths();var obstacleBounds=fort.RoadObstacles.Bounds;
        // Bounded regional routes with wall corridors as hard barriers. No bridge is invented over water.
        async Task<List<Vector2>> Route(Vector2 start,Vector2 end){
            int a=Index(start),b=Index(end);var open=new PriorityQueue<int,(float,int)>();var costs=new Dictionary<int,float>{{a,0}};var back=new Dictionary<int,int>();var done=new HashSet<int>();open.Enqueue(a,(0,a));
            while(open.TryDequeue(out int k,out _)){
                if(!done.Add(k))continue;if(done.Count>100000)throw new InvalidOperationException("区域道路搜索预算耗尽");
                if(k==b){var path=new List<Vector2>{P(k)};while(k!=a){k=back[k];path.Add(P(k));}path.Reverse();return path;}
                foreach(int j in Neighbors(k)){if(done.Contains(j)||cells[j].State!=SettlementCellState.Included)continue;if(obstacleBounds.HasPoint(P(j))&&ParcelGeometry.Area(ParcelGeometry.Intersection(new Paths64{ParcelGeometry.Path(RoadSpace.Pad(P(j),8))},obstacles))>.001)continue;
                    float next=costs[k]+P(k).DistanceTo(P(j))*(1+(e.Cell(j%nx,j/nx).MaxSlope??0)/20);if(costs.TryGetValue(j,out float old)&&old<=next)continue;costs[j]=next;back[j]=k;open.Enqueue(j,(next+P(j).DistanceTo(P(b)),j));}
                if(done.Count%2048==0)await Yield(.45f,"规划区域连接");
            }throw new InvalidOperationException("区域片区无陆路连接；需要调整布局或提供明确跨河点");
        }
        var already=coreArea;
        foreach(var entry in entries){
            var gate=gateAssignments.GetValueOrDefault(entry.Id);var start=gate==null?seed.Core:gate.Position+gate.Outward*15;
            var path=await Route(start,P(entry.Edge.Cell));links.Add(new($"route_{entry.Id}","对外商路",Array.AsReadOnly(path.ToArray()),gate?.Id));
            var strips=new Paths64();float travelled=0;for(int i=1;i<path.Count;i++){
                float segment=path[i].DistanceTo(path[i-1]);if(travelled<o.SuburbLength&&path[i].DistanceTo(seed.Core)>o.CoreRadius*.65f)strips.Add(ParcelGeometry.Path(RoadSpace.Strip(path[i-1],path[i],o.SuburbWidth)));travelled+=segment;
            }
            var shape=ParcelGeometry.Difference(ParcelGeometry.Intersection(ParcelGeometry.Union(strips),transportLand),already);shape=ParcelGeometry.Difference(shape,fort.BuildingReserve.CopyPaths());
            if(ParcelGeometry.Area(shape)<5000)continue;var region=new ParcelRegion(shape);districts.Add(new($"suburb_{entry.Id}",(entry.Edge.Outward.X>0?"东门街带":entry.Edge.Outward.X<0?"西门街带":entry.Edge.Outward.Y>0?"南门街带":"北门街带"),TownDistrictRole.GateSuburb,region,path[Math.Min(path.Count-1,path.Count/3)],105,85));already=ParcelGeometry.Union(new Paths64(already.Concat(shape)));
        }
        if(districts.Count<Math.Min(3,entries.Length+1))throw new InvalidOperationException("城外街带空间不足，请增大研究范围或调整核心位置");
        await Yield(.65f,"规划城外机构聚落与园田");
        Paths64 facilitiesReserved=ParcelGeometry.Union(new Paths64(fort.BuildingReserve.CopyPaths().Concat(ParcelGeometry.SettlementCells(result,true))));
        if(o.CivicPrecincts){
            foreach(var spec in new[]{("church","城内教堂",new Vector2(28,36),false),("abbey","城外修道院",new Vector2(54,66),true)}){
                var candidates=new List<(int K,float Score)>();
                foreach(int k in distance.Keys.Where(k=>k%2==0).Order()){
                    var p=P(k);bool inside=fort.Interior.Contains(p);float fromCore=p.DistanceTo(seed.Core);if(spec.Item4?(inside||fromCore<o.CoreRadius+220||fromCore>o.CoreRadius+o.SuburbLength+180):(!inside||fromCore<45))continue;
                    var area=ParcelGeometry.Rect(new Rect2(p-spec.Item3/2,spec.Item3).Grow(14));if(ParcelGeometry.Area(ParcelGeometry.Difference(area,transportLand))>.01||ParcelGeometry.Area(ParcelGeometry.Intersection(area,facilitiesReserved))>.01)continue;
                    float fromLinks=links.SelectMany(l=>l.Points).Min(v=>v.DistanceTo(p));if(spec.Item4&&(fromLinks<220||fromLinks>380))continue;
                    candidates.Add((k,spec.Item4?Math.Abs(fromCore-(o.CoreRadius+o.SuburbLength*.65f))+Math.Abs(fromLinks-260):Math.Abs(fromCore-75)));
                }
                if(candidates.Count==0)throw new InvalidOperationException(spec.Item2+"缺少独立布局空间");var center=P(candidates.OrderBy(p=>p.Score).ThenBy(p=>p.K).First().K);var rect=new Rect2(center-spec.Item3/2,spec.Item3);var toward=(seed.Core-center).Normalized();var access=Math.Abs(toward.X)>Math.Abs(toward.Y)?center+new Vector2(Math.Sign(toward.X)*(spec.Item3.X/2+10),0):center+new Vector2(0,Math.Sign(toward.Y)*(spec.Item3.Y/2+10));
                var region=new ParcelRegion(ParcelGeometry.Rect(rect));precincts.Add(new(spec.Item1,spec.Item2,region,access));facilitiesReserved=ParcelGeometry.Union(new Paths64(facilitiesReserved.Concat(region.CopyPaths())));
                if(spec.Item4){
                    var local=ParcelGeometry.Intersection(ParcelGeometry.Rect(new Rect2(center-new Vector2(125,115),new Vector2(250,230))),transportLand);local=ParcelGeometry.Difference(local,ParcelGeometry.Offset(already,40));var d=new ParcelRegion(local);districts.Add(new("abbey_suburb","修道院聚落",TownDistrictRole.InstitutionSuburb,d,access,90,90));already=ParcelGeometry.Union(new Paths64(already.Concat(local)));
                    var attach=links.SelectMany(l=>l.Points).OrderBy(p=>p.DistanceSquaredTo(access)).First();var route=await Route(access,attach);links.Add(new("abbey_link","修道院联络路",Array.AsReadOnly(route.ToArray()),null));
                }
            }
        }
        obstacles=ParcelGeometry.Union(new Paths64(obstacles.Concat(precincts.SelectMany(p=>p.Shape.CopyPaths()))));obstacleBounds=new ParcelRegion(obstacles).Bounds;
        landscapes=await AgriculturalLandscapePlanner.Generate(e,o,seed.Core,transportLand,already,obstacles,links,distance.Keys.ToHashSet(),Route,(fraction,text)=>Yield(.7f+fraction*.25f,text));
        result.Town=new MedievalLayout(fort.Interior.CopyPaths(),fort.WallFootprint.CopyPaths(),fort.WallReserve.CopyPaths(),ParcelGeometry.Path(fort.WallLine),fort.Gates.ToList(),precincts);
        result.Region=new(e.Bounds,districts,links,landscapes);await Yield(1,"城内外布局检查完成");return result;
    }
}
