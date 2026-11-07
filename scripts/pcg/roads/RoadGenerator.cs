using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;
public static class RoadGenerator
{
    private sealed record Segment(Vector2 A,Vector2 B,bool Main,float Width);
    public static async Task<RoadNetwork> Generate(CityEnvironmentData e,SettlementLayout layout,long er,long lr,RoadOptions o,PcgContext ctx){
        if(!ReferenceEquals(layout.Environment,e)||layout.InputRevision!=er)throw new ArgumentException("环境与聚落版本不一致，请重新生成聚落");
        if(!float.IsFinite(o.MainWidth)||!float.IsFinite(o.StreetWidth)||o.StreetWidth<=0||o.MainWidth<o.StreetWidth||!float.IsFinite(o.Spacing)||o.Spacing<=o.MainWidth*3||!float.IsFinite(o.Jitter)||o.Jitter<0||o.Jitter>.25f||!float.IsFinite(o.MaxGrade)||o.MaxGrade<=0||o.MaxGrade>45||!float.IsFinite(o.MaxSurfaceSlope)||o.MaxSurfaceSlope<=0||o.MaxSurfaceSlope>45||o.Angle.HasValue&&!float.IsFinite(o.Angle.Value)||o.SearchBudget<1||o.SearchBudget>500000||o.CandidateBudget<1||o.CandidateBudget>2000)throw new ArgumentException("路宽、间距、坡度或预算参数无效");
        if(!float.IsFinite(o.CornerRadius)||o.CornerRadius<0||o.CornerRadius>12||!float.IsFinite(o.JunctionFlare)||o.JunctionFlare<0||o.JunctionFlare>8)throw new ArgumentException("转角半径或路口展开参数无效");
        async Task Yield(float p,string m){ctx.Cancellation.ThrowIfCancellationRequested();if(ctx.Yield!=null)await ctx.Yield(new(p,m));ctx.Cancellation.ThrowIfCancellationRequested();}
        await Yield(0,"检查输入和道路空间");var space=new RoadSpace(e,layout,o);int nx=e.CellsX,nz=e.CellsZ,n=nx*nz;
        Vector2 Position(int k)=>e.VertexPosition(k%nx,k/nx)+new Vector2(e.StepX,e.StepZ)/2;
        int Index(Vector2 p)=>Math.Clamp((int)((p.Y-e.Bounds.Position.Y)/e.StepZ),0,nz-1)*nx+Math.Clamp((int)((p.X-e.Bounds.Position.X)/e.StepX),0,nx-1);
        var valid=new Dictionary<(int,float),bool>();var links=new Dictionary<(int,int,float),bool>();var reused=new HashSet<(int,int)>();
        bool Valid(int k,float width){var key=(k,width);if(!valid.TryGetValue(key,out bool v)){v=layout.Cells[k].State==SettlementCellState.Included&&space.PolygonValid(RoadSpace.Pad(Position(k),width));valid[key]=v;}return v;}
        bool Link(int a,int b,float width){var key=(Math.Min(a,b),Math.Max(a,b),width);if(!links.TryGetValue(key,out bool v)){v=space.SegmentValid(Position(a),Position(b),width);links[key]=v;}return v;}
        int Snap(Vector2 p,float width){int start=Index(p),x=start%nx,z=start/nx;var near=new List<int>();for(int dz=-2;dz<=2;dz++)for(int dx=-2;dx<=2;dx++){int xx=x+dx,zz=z+dz;if(xx>=0&&xx<nx&&zz>=0&&zz<nz)near.Add(zz*nx+xx);}return near.Where(k=>Valid(k,width)).OrderBy(k=>Position(k).DistanceSquaredTo(p)).ThenBy(k=>k).DefaultIfEmpty(-1).First();}
        async Task<(List<int>? Path,string Reason)> Search(int start,int end,float width,Rect2? region,bool main,bool externalOnly=false){
            if(start<0||end<0||!Valid(start,width)||!Valid(end,width))return(null,"路宽不足或端点受限");
            var distance=new Dictionary<int,float>{{start,0}};var parent=new Dictionary<int,int>();var closed=new HashSet<int>();var open=new PriorityQueue<int,(float,int)>();open.Enqueue(start,(0,start));int expanded=0;
            while(open.TryDequeue(out int k,out _)){
                if(!closed.Add(k))continue;if(++expanded>o.SearchBudget)return(null,"搜索预算耗尽，尚不能判定不可达");
                if(k==end){var path=new List<int>{k};while(k!=start){k=parent[k];path.Add(k);}path.Reverse();
                    var simplified=new List<int>{path[0]};int cursor=0;
                    while(cursor<path.Count-1){int next=cursor+1;for(int trial=Math.Min(path.Count-1,cursor+8);trial>next;trial--)if(space.SegmentValid(Position(path[cursor]),Position(path[trial]),width)){next=trial;break;}simplified.Add(path[next]);cursor=next;}
                    return(simplified,"");}
                if(expanded%256==0)await Yield(main?.2f:.55f,main?"规划必需主路":"规划次路候选");
                int x=k%nx,z=k/nx;
                for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++){
                    if(dx==0&&dz==0)continue;int xx=x+dx,zz=z+dz;if(xx<0||xx>=nx||zz<0||zz>=nz)continue;int j=zz*nx+xx;
                    if(closed.Contains(j)||externalOnly&&layout.Town?.Interior.Contains(Position(j))==true||region.HasValue&&!region.Value.HasPoint(Position(j))||!Valid(j,width)||!Link(k,j,width))continue;
                    float length=Position(k).DistanceTo(Position(j));var cell=e.Cell(xx,zz);float cost=length*(1+(cell.MaxSlope??0)/30+(cell.Vegetation??0)*.1f);
                    if(main&&reused.Contains((Math.Min(k,j),Math.Max(k,j))))cost*=.8f;
                    if(parent.TryGetValue(k,out int previous)){var incoming=(Position(k)-Position(previous)).Normalized();var outgoing=(Position(j)-Position(k)).Normalized();cost+=(1-incoming.Dot(outgoing))*width*.2f;}
                    float next=distance[k]+cost;if(distance.TryGetValue(j,out float old)&&old<=next)continue;distance[j]=next;parent[j]=k;
                    open.Enqueue(j,(next+Position(j).DistanceTo(Position(end))*.8f,j));
                }
            }return(null,"在当前路宽与坡度限制下无合法通路");
        }
        var segments=new List<Segment>();var issues=new List<RoadIssue>();var anchors=new Dictionary<string,Vector2>{{layout.CoreId,layout.Core}};
        void AddPath(List<int> path,bool main){for(int i=1;i<path.Count;i++){segments.Add(new(Position(path[i-1]),Position(path[i]),main,main?o.MainWidth:o.StreetWidth));if(main)reused.Add((Math.Min(path[i-1],path[i]),Math.Max(path[i-1],path[i])));}}
        int core=Snap(layout.Core,o.MainWidth);if(core<0||!space.PolygonValid(RoadSpace.Pad(layout.Core,o.MainWidth))||Position(core).DistanceTo(layout.Core)>.001f&&!space.SegmentValid(layout.Core,Position(core),o.MainWidth))throw new RoadFailure(new(layout.Core,layout.Core,"核心不能容纳主路"));
        if(Position(core).DistanceTo(layout.Core)>.001f)segments.Add(new(layout.Core,Position(core),true,o.MainWidth));
        var hubs=new List<int>{core};
        foreach(var precinct in (layout.Town?.Precincts??Array.AsReadOnly(Array.Empty<TownPrecinct>())).Where(p=>layout.Region==null||layout.Town!.Interior.Contains(p.Access))){
            int start=Snap(precinct.Access,o.MainWidth);if(start<0)throw new RoadFailure(new(precinct.Access,layout.Core,precinct.Name+"入口不可达"));
            if(Position(start).DistanceTo(precinct.Access)>.001f){if(!space.SegmentValid(precinct.Access,Position(start),o.MainWidth))throw new RoadFailure(new(precinct.Access,layout.Core,precinct.Name+"入口净宽不足"));segments.Add(new(precinct.Access,Position(start),true,o.MainWidth));}
            int target=hubs.OrderBy(k=>Position(k).DistanceSquaredTo(precinct.Access)).First();var connection=await Search(start,target,o.MainWidth,null,true);if(connection.Path==null)throw new RoadFailure(new(precinct.Access,Position(target),precinct.Name+connection.Reason));AddPath(connection.Path,true);anchors[precinct.Id]=precinct.Access;hubs.Add(start);
        }
        foreach(var entry in layout.Entries.OrderBy(b=>b.Id,StringComparer.Ordinal)){
            anchors.Add(entry.Id,entry.Position);int start=entry.Edge.Cell;
            if(entry.Edge.A.DistanceTo(entry.Edge.B)+.001f<o.MainWidth||!space.SegmentValid(entry.Position,Position(start),o.MainWidth))throw new RoadFailure(new(entry.Position,layout.Core,$"{entry.Id} 入口开口或内侧走廊放不下主路"));
            segments.Add(new(entry.Position,Position(start),true,o.MainWidth));
            var gate=layout.Region!=null?layout.Town?.Gates.OrderBy(g=>g.Position.DistanceSquaredTo(entry.Position)).FirstOrDefault():layout.Town?.Gates.FirstOrDefault(g=>g.EntryId==entry.Id);
            if(gate!=null){
                int outside=Snap(gate.Position+gate.Outward*12,o.MainWidth),inside=Snap(gate.Position-gate.Outward*12,o.MainWidth);
                if(outside<0||inside<0||!space.SegmentValid(Position(outside),gate.Position,o.MainWidth)||!space.SegmentValid(gate.Position,Position(inside),o.MainWidth))throw new RoadFailure(new(gate.Position,entry.Position,"城门接入净宽不足，请增加门洞宽度"));
                var approach=await Search(start,outside,o.MainWidth,null,true);if(approach.Path==null)throw new RoadFailure(new(entry.Position,gate.Position,approach.Reason));AddPath(approach.Path,true);
                segments.Add(new(Position(outside),gate.Position,true,o.MainWidth));segments.Add(new(gate.Position,Position(inside),true,o.MainWidth));anchors[gate.Id]=gate.Position;start=inside;
            }
            int target=hubs.OrderBy(k=>Position(k).DistanceSquaredTo(Position(start))).First();var result=await Search(start,target,o.MainWidth,null,true);if(result.Path==null)throw new RoadFailure(new(entry.Position,layout.Core,$"{entry.Id}: {result.Reason}"));AddPath(result.Path,true);
        }
        if(layout.Region!=null&&layout.Town!=null)foreach(var precinct in layout.Town.Precincts.Where(p=>!layout.Town.Interior.Contains(p.Access))){
            int start=Snap(precinct.Access,o.MainWidth);var intent=layout.Region.Links.FirstOrDefault(l=>l.Id==precinct.Id+"_link");var attach=intent?.Points.LastOrDefault()??precinct.Access;
            var targets=segments.Where(s=>s.Main).SelectMany(s=>new[]{s.A,s.B}).Where(p=>!layout.Town.Interior.Contains(p)&&p.DistanceTo(precinct.Access)>20).OrderBy(p=>p.DistanceSquaredTo(attach)).ToArray();
            bool connected=false;foreach(var target in targets.Take(12)){
                int end=Snap(target,o.MainWidth);if(start<0||end<0||layout.Town.Interior.Contains(Position(end)))continue;
                if(Position(start).DistanceTo(precinct.Access)>.001f&&!space.SegmentValid(precinct.Access,Position(start),o.MainWidth))continue;
                if(Position(end).DistanceTo(target)>.001f&&!space.SegmentValid(Position(end),target,o.MainWidth))continue;
                var route=await Search(start,end,o.MainWidth,null,true,true);if(route.Path==null)continue;
                if(Position(start).DistanceTo(precinct.Access)>.001f)segments.Add(new(precinct.Access,Position(start),true,o.MainWidth));AddPath(route.Path,true);
                if(Position(end).DistanceTo(target)>.001f)segments.Add(new(Position(end),target,true,o.MainWidth));anchors[precinct.Id]=precinct.Access;hubs.Add(start);connected=true;break;
            }
            if(!connected)throw new RoadFailure(new(precinct.Access,attach,precinct.Name+"无法接入城外区域通路"));
        }
        await Yield(.4f,"主街完成，布置局部街巷");
        uint Hash(int k){unchecked{uint h=(uint)(k^o.Seed);h^=h>>16;h*=0x7feb352d;h^=h>>15;return h;}}
        // Finite spatial candidates: locally spaced street destinations, no global perpendicular template.
        var sites=new List<int>(hubs.Distinct());
        foreach(int k in Enumerable.Range(0,n).Where(k=>layout.Cells[k].State==SettlementCellState.Included).OrderBy(Hash)){
            if(!Valid(k,o.StreetWidth)||(layout.Region!=null?!layout.Region.DevelopmentArea.Contains(Position(k)):layout.Town!=null&&!layout.Town.Interior.Contains(Position(k))))continue;
            float spacing=layout.Region?.DistrictAt(Position(k))?.StreetSpacing??o.Spacing;spacing*=o.Spacing/80;
            if(sites.All(j=>Position(k).DistanceTo(Position(j))>=spacing))sites.Add(k);
            if(sites.Count>=90)break;
        }
        var pairs=new List<(int A,int B,float Length)>();for(int i=0;i<sites.Count;i++)for(int j=i+1;j<sites.Count;j++){float length=Position(sites[i]).DistanceTo(Position(sites[j]));if(length<o.Spacing*2.2f)pairs.Add((i,j,length));}
        if(o.PlannedTown){
            sites=new List<int>(hubs.Distinct());pairs.Clear();var template=new Dictionary<(int,int),int>();float angle=o.Angle.HasValue?Mathf.DegToRad(o.Angle.Value):0;var axis=Vector2.FromAngle(angle);var across=axis.Orthogonal();int range=(int)Math.Ceiling((layout.Region==null?layout.Options.Radius*2:Math.Max(layout.Region.ViewBounds.Size.X,layout.Region.ViewBounds.Size.Y))/o.Spacing)+1;
            if(range>80)throw new ArgumentException("规划新城模板过密");
            for(int z=-range;z<=range;z++)for(int x=-range;x<=range;x++){
                float jitter=((Hash(x*1009+z)&65535)/65535f-.5f)*o.Jitter;
                var p=layout.Core+axis*((x+jitter)*o.Spacing)+across*((z-jitter)*o.Spacing);if(!layout.Contains(p)||(layout.Region!=null?!layout.Region.DevelopmentArea.Contains(p):layout.Town!=null&&!layout.Town.Interior.Contains(p)))continue;int k=Snap(p,o.StreetWidth);if(k<0)continue;int index=sites.IndexOf(k);if(index<0){index=sites.Count;sites.Add(k);}template[(x,z)]=index;
            }
            foreach(var a in template)foreach(var offset in new[]{(1,0),(0,1)})if(template.TryGetValue((a.Key.Item1+offset.Item1,a.Key.Item2+offset.Item2),out int b)&&a.Value!=b)pairs.Add((a.Value,b,Position(sites[a.Value]).DistanceTo(Position(sites[b]))));
        }
        var parentSite=Enumerable.Range(0,sites.Count).ToArray();int Root(int a){while(parentSite[a]!=a)a=parentSite[a];return a;}
        for(int i=1;i<hubs.Count;i++)parentSite[i]=0;
        var accepted=new List<(int A,int B)>();int attempts=0;
        foreach(var pair in pairs.OrderBy(p=>p.Length)){
            bool same=Root(pair.A)==Root(pair.B);if(same&&!o.PlannedTown){
                // A small number of useful cross connections, avoiding uniform loops and acute junctions.
                if(Hash(pair.A*1009+pair.B)%5!=0)continue;
                bool acute=accepted.Any(link=>{int other=link.A==pair.A?link.B:link.B==pair.A?link.A:-1;return other>=0&&Math.Abs((Position(sites[other])-Position(sites[pair.A])).AngleTo(Position(sites[pair.B])-Position(sites[pair.A])))<.65f;});if(acute)continue;
            }
            if(++attempts>o.CandidateBudget){issues.Add(new(layout.Core,layout.Core,"街巷候选预算耗尽"));break;}
            var result=await Search(sites[pair.A],sites[pair.B],o.StreetWidth,null,false);
            if(result.Path!=null){AddPath(result.Path,false);parentSite[Root(pair.B)]=Root(pair.A);accepted.Add((pair.A,pair.B));}else issues.Add(new(Position(sites[pair.A]),Position(sites[pair.B]),result.Reason));
            if(attempts%10==0)await Yield(.6f,"局部街巷连接");
        }
        await Yield(.75f,"拆分交叉口与整理拓扑");
        // Deduplicate before pairwise intersection; physical edges remain at the higher accepted width.
        string Key(Vector2 p)=>$"{Math.Round(p.X*1000)},{Math.Round(p.Y*1000)}";
        string Pair(Vector2 a,Vector2 b){var ka=Key(a);var kb=Key(b);return string.CompareOrdinal(ka,kb)<0?ka+"/"+kb:kb+"/"+ka;}
        segments=segments.GroupBy(s=>Pair(s.A,s.B)).Select(g=>g.OrderByDescending(s=>s.Width).ThenByDescending(s=>s.Main).First()).ToList();
        if(segments.Count>5000)throw new RoadFailure(new(layout.Core,layout.Core,"路段预算超限，请增大街道间距"));
        var cuts=segments.Select(_=>new SortedSet<float>{0,1}).ToArray();
        for(int i=0;i<segments.Count;i++){
            var a=segments[i];var r=a.B-a.A;
            for(int j=i+1;j<segments.Count;j++){
                var b=segments[j];var s=b.B-b.A;float cross=r.Cross(s);
                if(Math.Abs(cross)>.00001f){float t=(b.A-a.A).Cross(s)/cross,u=(b.A-a.A).Cross(r)/cross;if(t>=-1e-5&&t<=1.00001&&u>=-1e-5&&u<=1.00001){cuts[i].Add(Math.Clamp(t,0,1));cuts[j].Add(Math.Clamp(u,0,1));}}
                else if(Math.Abs((b.A-a.A).Cross(r))<.001f){foreach(var p in new[]{b.A,b.B}){float t=(p-a.A).Dot(r)/r.LengthSquared();if(t>0&&t<1)cuts[i].Add(t);}foreach(var p in new[]{a.A,a.B}){float t=(p-b.A).Dot(s)/s.LengthSquared();if(t>0&&t<1)cuts[j].Add(t);}}
            }if(i%128==0)await Yield(.8f,"交叉口拆分");
        }
        var atom=new Dictionary<string,Segment>();for(int i=0;i<segments.Count;i++){var s=segments[i];var t=cuts[i].ToArray();for(int j=1;j<t.Length;j++){var a=s.A.Lerp(s.B,t[j-1]);var b=s.A.Lerp(s.B,t[j]);if(a.DistanceTo(b)<.001f)continue;string key=Pair(a,b);if(!atom.TryGetValue(key,out var old)||old.Width<s.Width)atom[key]=new(a,b,s.Main,s.Width);}}
        var points=new Dictionary<string,int>();var positions=new List<Vector2>();int Vertex(Vector2 p){string key=Key(p);if(!points.TryGetValue(key,out int id)){id=positions.Count;positions.Add(p);points[key]=id;}return id;}
        var raw=atom.Values.Select(s=>(A:Vertex(s.A),B:Vertex(s.B),S:s)).ToArray();var adjacency=new Dictionary<int,List<int>>();for(int i=0;i<raw.Length;i++)foreach(int v in new[]{raw[i].A,raw[i].B}){if(!adjacency.ContainsKey(v))adjacency[v]=new();adjacency[v].Add(i);}
        var anchorIds=anchors.ToDictionary(p=>p.Key,p=>Vertex(p.Value));var reached=new HashSet<int>{anchorIds[layout.CoreId]};var queue=new Queue<int>(reached);
        while(queue.TryDequeue(out int v))if(adjacency.TryGetValue(v,out var incident))foreach(int k in incident){int next=raw[k].A==v?raw[k].B:raw[k].A;if(reached.Add(next))queue.Enqueue(next);}
        foreach(var a in anchorIds)if(!reached.Contains(a.Value))throw new RoadFailure(new(anchors[a.Key],layout.Core,$"{a.Key} 在最终网络中未连通"));
        int discarded=raw.Count(r=>!reached.Contains(r.A));if(discarded>0)issues.Add(new(layout.Core,layout.Core,$"清理未接入主路的次路段 {discarded} 条"));
        var kept=raw.Select((r,i)=>(r,i)).Where(p=>reached.Contains(p.r.A)).Select(p=>p.i).ToHashSet();
        var anchorsSet=anchorIds.Values.ToHashSet();var important=reached.Where(v=>anchorsSet.Contains(v)||adjacency[v].Count!=2||adjacency[v].Select(i=>(raw[i].S.Main,raw[i].S.Width)).Distinct().Count()>1).ToHashSet();
        var used=new HashSet<int>();var finalEdges=new List<RoadEdge>();var polygons=new List<Vector2[]>();
        foreach(int v in important.Order())foreach(int k in adjacency[v]){
            if(!kept.Contains(k)||!used.Add(k))continue;var first=raw[k];var path=new List<Vector2>{positions[v]};int current=first.A==v?first.B:first.A;path.Add(positions[current]);
            while(!important.Contains(current)){
                int nextEdge=adjacency[current].First(i=>!used.Contains(i));used.Add(nextEdge);var next=raw[nextEdge];current=next.A==current?next.B:next.A;path.Add(positions[current]);
            }
            float length=0,grade=0;for(int i=1;i<path.Count;i++){length+=path[i].DistanceTo(path[i-1]);grade=Math.Max(grade,space.Grade(path[i-1],path[i]));var polygon=RoadSpace.Strip(path[i-1],path[i],first.S.Width);if(!space.PolygonValid(polygon))throw new RoadFailure(new(path[i-1],path[i],"整理后道路占地不合法"));polygons.Add(polygon);}
            finalEdges.Add(new(finalEdges.Count,v,current,Array.AsReadOnly(path.ToArray()),first.S.Main,first.S.Width,length,grade));
        }
        var entryVertices=layout.Entries.Select(b=>anchorIds[b.Id]).ToHashSet();
        foreach(int v in reached){if(entryVertices.Contains(v))continue;float width=adjacency[v].Where(kept.Contains).Max(i=>raw[i].S.Width);var pad=RoadSpace.Pad(positions[v],width);if(!space.PolygonValid(pad))throw new RoadFailure(new(positions[v],positions[v],"交叉口或转角占地越界"));polygons.Add(pad);}
        var vertices=important.Order().Select(v=>{e.TryHeightAt(positions[v],out float h);string role=anchorIds.FirstOrDefault(a=>a.Value==v).Key??(adjacency[v].Count==1?"端点":"交叉口");return new RoadVertex(v,positions[v],h,role);}).ToArray();
        for(int i=0;i<finalEdges.Count;i++){var edge=finalEdges[i];var midpoint=edge.Points[edge.Points.Count/2];
            var role=edge.Main?(midpoint.DistanceTo(layout.Core)<60?StreetRole.MarketStreet:anchorIds.Where(a=>a.Key is "church" or "manor" or "abbey").Any(a=>a.Value==edge.From||a.Value==edge.To)?StreetRole.InstitutionStreet:StreetRole.GateStreet):finalEdges.Count(e=>e.From==edge.From||e.To==edge.From)==1||finalEdges.Count(e=>e.From==edge.To||e.To==edge.To)==1?StreetRole.BackLane:StreetRole.LocalStreet;
            finalEdges[i]=edge with{Role=role};
        }
        if(finalEdges.Count-vertices.Length+1==0)issues.Add(new(layout.Core,layout.Core,"未形成闭合街区，请调整间距或规划范围"));
        var shaped=await RoadGeometryPlanner.Finalize(e,layout,vertices,finalEdges,o,ctx);
        await Yield(1,"道路几何定型完成");return new(e,layout,er,lr,o,vertices,shaped.Edges,anchorIds,polygons,issues,shaped.Geometry);
    }
}
