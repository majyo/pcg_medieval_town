using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;

public static class RoadGeometryPlanner
{
    public static async Task<(RoadEdge[] Edges,RoadGeometry Geometry)> Finalize(CityEnvironmentData env,SettlementLayout layout,
        IReadOnlyList<RoadVertex> nodes,IReadOnlyList<RoadEdge> planned,RoadOptions options,PcgContext ctx)
    {
        if(planned.Count>2000||planned.Sum(e=>e.Points.Count)>12000)throw new RoadFailure(new(layout.Core,layout.Core,"道路定型几何预算超限，请增大街巷间距"));
        var space=new RoadSpace(env,layout,options);var notices=new List<RoadGeometryNotice>();int smoothed=0;
        async Task Yield(float p,string message){ctx.Cancellation.ThrowIfCancellationRequested();if(ctx.Yield!=null)await ctx.Yield(new(p,message));ctx.Cancellation.ThrowIfCancellationRequested();}
        bool Legal(Paths64 paths){foreach(var part in ParcelGeometry.Components(paths)){var region=new ParcelRegion(part);for(int i=0;i<region.Triangles.Count;i+=3)
            if(!space.PolygonValid(new[]{region.Triangles[i],region.Triangles[i+1],region.Triangles[i+2]}))return false;}return true;}
        Paths64 Ribbon(IReadOnlyList<Vector2> points,float width){var paths=new Paths64();for(int i=1;i<points.Count;i++)paths.Add(ParcelGeometry.Path(RoadSpace.Strip(points[i-1],points[i],width)));
            for(int i=1;i<points.Count-1;i++)paths.Add(Circle(points[i],width/2));return ParcelGeometry.Union(paths);}
        bool Crossing(IReadOnlyList<Vector2> points,RoadEdge edge,IReadOnlyList<RoadEdge> others){
            foreach(var other in others.Where(e=>e.Id!=edge.Id))for(int j=1;j<other.Points.Count;j++)for(int i=1;i<points.Count;i++){
                var a=points[i-1];var d=points[i]-a;var b=other.Points[j-1];var v=other.Points[j]-b;float cross=d.Cross(v);if(Math.Abs(cross)<.00001f)continue;
                float t=(b-a).Cross(v)/cross,u=(b-a).Cross(d)/cross;
                if(t<-.00001f||t>1.00001f||u<-.00001f||u>1.00001f)continue;
                var hit=a+d*t;bool shared=nodes.Any(n=>(n.Id==edge.From||n.Id==edge.To)&&(n.Id==other.From||n.Id==other.To)&&n.Position.DistanceTo(hit)<.003f);
                if(!shared)return true;
            }return false;
        }
        var ribbons=planned.ToDictionary(e=>e.Id,e=>Ribbon(e.Points,e.Width));
        bool NewContact(Paths64 candidate,Paths64 original,IEnumerable<int> owners,IReadOnlyList<RoadEdge> others){
            var ids=owners.ToHashSet();foreach(var other in others.Where(e=>!ids.Contains(e.Id))){
                var added=ParcelGeometry.Difference(ParcelGeometry.Intersection(candidate,ribbons[other.Id]),original);
                if(ParcelGeometry.Area(added)>.01)return true;
            }return false;
        }
        var edges=planned.ToArray();await Yield(.84f,"定型道路转角");
        for(int index=0;index<edges.Length;index++){
            var e=edges[index];IReadOnlyList<Vector2> original=e.Points;var points=original.ToList();int accepted=0;RoadPlanarPath? analytic=null;
            // Remove a legal backtracking spur before fitting a bend. The test
            // retains all graph endpoints and checks the full corridor/topology.
            if(options.CornerRadius>0){var route=original.ToList();bool changed=false;
                for(int i=1;i+1<route.Count;i++){var before=(route[i]-route[i-1]).Normalized();var after=(route[i+1]-route[i]).Normalized();if(Math.Abs(before.AngleTo(after))<Mathf.Pi*.6f)continue;
                    var shortened=route.Where((_,k)=>k!=i).ToList();var footprint=Ribbon(shortened,e.Width);var owners=edges.Where(other=>other.From==e.From||other.To==e.From||other.From==e.To||other.To==e.To).Select(other=>other.Id);
                    if(space.SegmentValid(route[i-1],route[i+1],e.Width)&&Legal(footprint)&&!Crossing(shortened,e,edges)&&!NewContact(footprint,ribbons[e.Id],owners,edges)){route=shortened;changed=true;i=Math.Max(0,i-1);}}
                if(changed){original=route;points=route;notices.Add(new(e.Id,route[0],"移除已复核占地与连通关系的折返尖角"));}}

            if(options.CornerRadius>0&&original.Count>2){
                var candidate=new List<Vector2>{original[0]};var spans=new List<RoadPlanarPath.Span>();var last=original[0];
                void LineTo(Vector2 p){if(last.DistanceTo(p)>.00001f)spans.Add(new(last,last,p,false));last=p;}
                for(int i=1;i<original.Count-1;i++){
                    var a=original[i-1];var b=original[i];var c=original[i+1];var incoming=(b-a).Normalized();var outgoing=(c-b).Normalized();
                    float angle=Math.Abs(incoming.AngleTo(outgoing));
                    if(angle<.035f||angle>2.6f){candidate.Add(b);LineTo(b);continue;}
                    bool fitted=false;
                    foreach(float factor in new[]{1f,.5f,.25f}){
                        float minimumRadius=(e.Width/2+.25f)/Math.Max(.1f,Mathf.Cos(angle/2));
                        float radius=Math.Max(options.CornerRadius,minimumRadius)*factor;
                        float trim=Math.Min(radius*Mathf.Tan(angle/2),Math.Min(a.DistanceTo(b),b.DistanceTo(c))*.45f);
                        if(trim/Mathf.Tan(angle/2)<minimumRadius-.001f)continue;
                        if(trim<.01f)continue;
                        var start=b-incoming*trim;var end=b+outgoing*trim;var curve=new List<Vector2>{start};
                        int count=Math.Max(4,(int)Math.Ceiling(angle/.025f));for(int j=1;j<=count;j++){float t=(float)j/count;curve.Add(start*(1-t)*(1-t)+b*(2*t*(1-t))+end*t*t);}
                        if(curve.Skip(1).Select((p,j)=>space.SegmentValid(curve[j],p,e.Width)).All(v=>v)&&Legal(Ribbon(curve,e.Width))){LineTo(start);spans.Add(new(start,b,end,true));last=end;candidate.AddRange(curve);accepted++;fitted=true;break;}
                    }
                    if(!fitted){candidate.Add(b);LineTo(b);notices.Add(new(e.Id,b,"转角受地形或占地限制，保留原折线"));}
                }
                candidate.Add(original[^1]);LineTo(original[^1]);
                var candidateArea=Ribbon(candidate,e.Width);
                var adjacent=edges.Where(other=>other.From==e.From||other.To==e.From||other.From==e.To||other.To==e.To).Select(other=>other.Id);
                if(!Crossing(candidate,e,edges)&&!NewContact(candidateArea,ribbons[e.Id],adjacent,edges)&&Legal(candidateArea)&&candidate.Skip(1).Select((p,i)=>space.SegmentValid(candidate[i],p,e.Width)).All(v=>v)){points=candidate;analytic=new RoadPlanarPath(spans);}
                else{accepted=0;notices.Add(new(e.Id,original[0],"转角整理可能改变交叉关系，保留原中心线"));}
            }
            float length=0,grade=0;for(int i=1;i<points.Count;i++){length+=points[i-1].DistanceTo(points[i]);grade=Math.Max(grade,space.Grade(points[i-1],points[i]));}
            analytic??=RoadPlanarPath.Polyline(points);edges[index]=e with{Points=Array.AsReadOnly(points.ToArray()),Length=analytic.Length,MaxGrade=grade,PlanarPath=analytic};smoothed+=accepted;
            ribbons[e.Id]=Ribbon(points,e.Width);
            if(index%8==0)await Yield(.85f+.04f*index/edges.Length,"复核转角净宽与纵坡");
        }
        var byNode=nodes.ToDictionary(n=>n.Id,n=>edges.Where(e=>e.From==n.Id||e.To==n.Id).ToArray());
        var entryNodes=layout.Entries.Select(en=>nodes.FirstOrDefault(n=>n.Position.DistanceTo(en.Position)<.002f)?.Id??-1).ToHashSet();
        var patches=new List<RoadGeometry.Patch>();var junctions=new List<RoadGeometry.Patch>();
        foreach(var node in nodes){
            var incident=byNode[node.Id];if(incident.Length<2||entryNodes.Contains(node.Id))continue;
            float maxWidth=incident.Max(e=>e.Width);var cloud=new List<Vector2>();
            foreach(var e in incident){var path=e.From==node.Id?e.Points.ToArray():e.Points.Reverse().ToArray();
                float d=Math.Min(maxWidth/2+options.JunctionFlare,path[0].DistanceTo(path[1])*.4f);var direction=(path[1]-path[0]).Normalized();var side=direction.Orthogonal()*e.Width/2;
                cloud.Add(node.Position+direction*d+side);cloud.Add(node.Position+direction*d-side);
            }
            var hull=Hull(cloud);var join=new Paths64{ParcelGeometry.Path(hull)};
            var original=ParcelGeometry.Union(new Paths64(incident.SelectMany(e=>ribbons[e.Id])));
            if(hull.Length>=3&&Legal(join)&&!NewContact(join,original,incident.Select(e=>e.Id),edges))junctions.Add(new(incident.OrderByDescending(e=>e.Width).ThenBy(e=>e.Id).First().Id,node.Id,join));
            else notices.Add(new(incident[0].Id,node.Position,"路口展开受限，使用已校验路带交集"));
        }
        await Yield(.9f,"整理路口与宽度过渡");
        foreach(var e in edges){
            var path=e.Points;var cumulative=new float[path.Count];for(int i=1;i<path.Count;i++)cumulative[i]=cumulative[i-1]+path[i].DistanceTo(path[i-1]);
            float length=cumulative[^1];float fromWidth=entryNodes.Contains(e.From)?e.Width:byNode[e.From].Max(r=>r.Width);
            float toWidth=entryNodes.Contains(e.To)?e.Width:byNode[e.To].Max(r=>r.Width);
            float fromRun=Math.Min(length*.35f,Math.Max(3,(fromWidth-e.Width)*2)),toRun=Math.Min(length*.35f,Math.Max(3,(toWidth-e.Width)*2));
            var stations=new SortedSet<float>(cumulative);if(fromWidth>e.Width)stations.Add(fromRun);if(toWidth>e.Width)stations.Add(length-toRun);
            Vector2 At(float station){int i=1;while(i<cumulative.Length-1&&cumulative[i]<station)i++;return path[i-1].Lerp(path[i],(station-cumulative[i-1])/Math.Max(.000001f,cumulative[i]-cumulative[i-1]));}
            Paths64 Build(bool taper){var list=stations.ToArray();var strips=new Paths64();
                float Width(float s)=>taper?Math.Max(e.Width,Math.Max(e.Width+(fromWidth-e.Width)*Math.Max(0,1-s/Math.Max(.001f,fromRun)),e.Width+(toWidth-e.Width)*Math.Max(0,1-(length-s)/Math.Max(.001f,toRun)))):e.Width;
                for(int i=1;i<list.Length;i++){var a=At(list[i-1]);var b=At(list[i]);if(a.DistanceTo(b)<.00001f)continue;var n=(b-a).Normalized().Orthogonal();strips.Add(ParcelGeometry.Path(new[]{a+n*Width(list[i-1])/2,b+n*Width(list[i])/2,b-n*Width(list[i])/2,a-n*Width(list[i-1])/2}));}
                for(int i=1;i<path.Count-1;i++)strips.Add(Circle(path[i],Width(cumulative[i])/2));return ParcelGeometry.Union(strips);}
            var area=Build(true);var adjacent=edges.Where(other=>other.From==e.From||other.To==e.From||other.From==e.To||other.To==e.To).Select(other=>other.Id);
            if(!Legal(area)||NewContact(area,ribbons[e.Id],adjacent,edges)){area=Build(false);notices.Add(new(e.Id,path[0],"宽度过渡受限，保留原净宽"));}
            if(!Legal(area))throw new RoadFailure(new(path[0],path[^1],"定型道路占地越界"));patches.Add(new(e.Id,null,area));ribbons[e.Id]=area;
            if(e.Id%8==0)await Yield(.91f+.03f*e.Id/edges.Length,"复核定型道路占地");
        }
        // Junctions own the middle of intersections; edges are clipped against them.
        // No stacked coplanar faces and no separately invented render footprint.
        var geometry=RoadGeometry.Assemble(junctions.Concat(patches),edges,planned,smoothed,notices);
        await Yield(.98f,"发布统一占地与临街边界");return(edges,geometry);
    }
    private static Path64 Circle(Vector2 p,float radius)=>ParcelGeometry.Path(Enumerable.Range(0,24).Select(i=>p+Vector2.FromAngle(i*Mathf.Tau/24)*radius));
    private static Vector2[] Hull(IEnumerable<Vector2> points){var p=points.Distinct().OrderBy(v=>v.X).ThenBy(v=>v.Y).ToArray();if(p.Length<3)return p;var lower=new List<Vector2>();var upper=new List<Vector2>();
        foreach(var v in p){while(lower.Count>=2&&(lower[^1]-lower[^2]).Cross(v-lower[^1])<=.00001f)lower.RemoveAt(lower.Count-1);lower.Add(v);}
        foreach(var v in p.Reverse()){while(upper.Count>=2&&(upper[^1]-upper[^2]).Cross(v-upper[^1])<=.00001f)upper.RemoveAt(upper.Count-1);upper.Add(v);}
        return lower.SkipLast(1).Concat(upper.SkipLast(1)).ToArray();}
}
