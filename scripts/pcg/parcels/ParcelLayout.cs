using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
namespace PcgCitySandbox.Pcg;
// Fixed-point quantization for polygon bookkeeping, independent of terrain sampling precision.
public static class ParcelGeometry
{
    public const double Scale=100000;
    public static Path64 Path(IEnumerable<Vector2> p,bool positive=true){var path=new Path64(p.Select(v=>new Point64((long)Math.Round(v.X*Scale),(long)Math.Round(v.Y*Scale))));if(positive&&Clipper.Area(path)<0)path.Reverse();return path;}
    public static Vector2 V(Point64 p)=>new((float)(p.X/Scale),(float)(p.Y/Scale));
    public static Paths64 Union(Paths64 p)=>Clipper.Union(p,FillRule.NonZero);
    public static Paths64 Difference(Paths64 a,Paths64 b)=>Clipper.Difference(a,b,FillRule.NonZero);
    public static Paths64 Intersection(Paths64 a,Paths64 b)=>Clipper.Intersect(a,b,FillRule.NonZero);
    public static double Area(Paths64 p)=>Math.Abs(Clipper.Area(p))/(Scale*Scale);
    public static Paths64 Offset(Paths64 p,float meters)=>Clipper.InflatePaths(p,meters*Scale,JoinType.Miter,EndType.Polygon,2);
    public static Paths64 Rect(Rect2 r)=>new(){Path(new[]{r.Position,new Vector2(r.End.X,r.Position.Y),r.End,new Vector2(r.Position.X,r.End.Y)})};
    public static bool Contains(Paths64 paths,Vector2 p){int winding=0;var point=new Point64((long)Math.Round(p.X*Scale),(long)Math.Round(p.Y*Scale));foreach(var ring in paths){var hit=Clipper.PointInPolygon(point,ring);if(hit==PointInPolygonResult.IsOn)return true;if(hit==PointInPolygonResult.IsInside)winding+=Clipper.Area(ring)>0?1:-1;}return winding!=0;}
    public static List<Paths64> Components(Paths64 paths){
        var tree=new PolyTree64();Clipper.BooleanOp(ClipType.Union,paths,new Paths64(),tree,FillRule.NonZero);var result=new List<Paths64>();
        void Visit(PolyPath64 node){if(node.Polygon!=null&&!node.IsHole){var rings=new Paths64{new Path64(node.Polygon)};for(int i=0;i<node.Count;i++)if(node[i].IsHole)rings.Add(new Path64(node[i].Polygon!));result.Add(rings);}for(int i=0;i<node.Count;i++)Visit(node[i]);}
        Visit(tree);return result.OrderBy(p=>p[0].Min(v=>v.Y)).ThenBy(p=>p[0].Min(v=>v.X)).ToList();
    }
    public static List<Paths64> DifferenceComponents(Paths64 subject,Paths64 clips){
        List<Paths64> Extract(Paths64 cut){var tree=new PolyTree64();Clipper.BooleanOp(ClipType.Difference,subject,cut,tree,FillRule.NonZero);var result=new List<Paths64>();
            void Visit(PolyPath64 node){if(node.Polygon!=null&&!node.IsHole){var rings=new Paths64{new Path64(node.Polygon)};for(int i=0;i<node.Count;i++)if(node[i].IsHole)rings.Add(new Path64(node[i].Polygon!));result.Add(rings);}for(int i=0;i<node.Count;i++)Visit(node[i]);}
            Visit(tree);return result;}
        var parts=Extract(clips);double expected=Area(Difference(subject,clips));
        if(Math.Abs(parts.Sum(Area)-expected)>.01){
            // Float-generated cuts can leave sub-millimetre contacts that the tree
            // misclassifies as independent negative rings. Separate only those contacts,
            // and reject the repair unless area is conserved within 0.1 square metres.
            var separated=Clipper.InflatePaths(clips,Scale*.0002,JoinType.Miter,EndType.Polygon,2);
            parts=Extract(separated);
            if(Math.Abs(parts.Sum(Area)-expected)>.1)throw new InvalidOperationException("接触轮廓归属修复未通过面积检查");
        }return parts;
    }
    internal static List<ParcelRegion> DifferenceRegions(Paths64 subject,Paths64 clips){
        double expected=Area(Difference(subject,clips));InvalidOperationException? error=null;
        foreach(float separation in new[]{0f,.00001f,.00005f,.0002f}){
            try{
                var cut=separation==0?clips:Clipper.InflatePaths(clips,Scale*separation,JoinType.Miter,EndType.Polygon,2);
                var parts=DifferenceComponents(subject,cut);if(Math.Abs(parts.Sum(Area)-expected)>.1)continue;
                return parts.Where(p=>Area(p)>.0001).Select(p=>new ParcelRegion(p)).ToList();
            }catch(InvalidOperationException ex){error=ex;}
        }throw new InvalidOperationException("接触轮廓无法在面积容差内生成网格",error);
    }
    public static Paths64 SettlementCells(SettlementLayout layout,bool reservedOnly=false){var env=layout.Environment;var rectangles=new Paths64();for(int z=0;z<env.CellsZ;z++){
        int start=-1;for(int x=0;x<=env.CellsX;x++){bool yes=x<env.CellsX&&layout.Cells[z*env.CellsX+x].State==SettlementCellState.Included&&(!reservedOnly||layout.Cells[z*env.CellsX+x].Reserved);
            if(yes&&start<0)start=x;if(!yes&&start>=0){rectangles.AddRange(Rect(new Rect2(env.VertexPosition(start,z),env.VertexPosition(x,z+1)-env.VertexPosition(start,z))));start=-1;}}
    }return Union(rectangles);}
    public static float Distance(Vector2 p,RoadEdge road){float best=float.PositiveInfinity;for(int i=1;i<road.Points.Count;i++){var a=road.Points[i-1];var d=road.Points[i]-a;best=Math.Min(best,p.DistanceTo(a+d*Math.Clamp((p-a).Dot(d)/d.LengthSquared(),0,1)));}return best;}
    internal static Paths64 SeparateTouching(Paths64 paths){
        var result=new Paths64();
        foreach(var ring in paths){
            var vertices=ring.Distinct().ToArray();var expanded=new Path64();
            for(int i=0;i<ring.Count;i++){
                var a=ring[i];var b=ring[(i+1)%ring.Count];expanded.Add(a);long dx=b.X-a.X,dy=b.Y-a.Y;
                var on=vertices.Where(p=>!(p.X==a.X&&p.Y==a.Y)&&!(p.X==b.X&&p.Y==b.Y)&&
                    (Int128)(p.X-a.X)*dy==(Int128)(p.Y-a.Y)*dx&&p.X>=Math.Min(a.X,b.X)&&p.X<=Math.Max(a.X,b.X)&&p.Y>=Math.Min(a.Y,b.Y)&&p.Y<=Math.Max(a.Y,b.Y));
                expanded.AddRange(on.OrderBy(p=>Math.Abs(dx)>=Math.Abs(dy)?(double)(p.X-a.X)/dx:(double)(p.Y-a.Y)/dy));
            }
            if(expanded.Count==0)continue;expanded.Add(expanded[0]);var stack=new Path64();var indices=new Dictionary<(long,long),int>();
            foreach(var p in expanded){var key=(p.X,p.Y);if(indices.TryGetValue(key,out int at)){
                    var cycle=new Path64(stack.Skip(at));if(cycle.Count>=3&&Math.Abs(Clipper.Area(cycle))>0)result.Add(cycle);
                    for(int i=stack.Count-1;i>at;i--){indices.Remove((stack[i].X,stack[i].Y));stack.RemoveAt(i);}
                }else{indices[key]=stack.Count;stack.Add(p);}}
        }return result;
    }
    // Deterministic scanline fallback for near-touching contours. Every slab is
    // bounded by source vertex ordinates and filled by the nonzero winding rule;
    // holes therefore remain empty without relying on fragile bridge edges.
    internal static Paths64 SlabTriangles(Paths64 paths){
        var edges=paths.SelectMany(r=>Enumerable.Range(0,r.Count).Select(i=>(A:r[i],B:r[(i+1)%r.Count]))).Where(e=>e.A.Y!=e.B.Y).ToArray();
        var ys=paths.SelectMany(p=>p.Select(v=>v.Y)).Distinct().OrderBy(y=>y).ToArray();var result=new Paths64();
        double X((Point64 A,Point64 B) e,double y)=>e.A.X+(y-e.A.Y)*(e.B.X-e.A.X)/(e.B.Y-e.A.Y);
        Point64 At((Point64 A,Point64 B) e,long y)=>new((long)Math.Round(X(e,y)),y);
        void Triangle(Point64 a,Point64 b,Point64 c){var t=new Path64{a,b,c};double area=Clipper.Area(t);if(area==0)return;if(area<0)t.Reverse();result.Add(t);}
        for(int row=1;row<ys.Length;row++){
            long y0=ys[row-1],y1=ys[row];double mid=y0+(y1-y0)/2d;
            var active=edges.Where(e=>Math.Min(e.A.Y,e.B.Y)<mid&&Math.Max(e.A.Y,e.B.Y)>mid).OrderBy(e=>X(e,mid)).ToArray();
            int winding=0;var left=default((Point64 A,Point64 B));
            foreach(var e in active){int before=winding;winding+=e.B.Y>e.A.Y?1:-1;
                if(before==0&&winding!=0)left=e;
                else if(before!=0&&winding==0){var a=At(left,y0);var b=At(e,y0);var c=At(e,y1);var d=At(left,y1);Triangle(a,b,c);Triangle(a,c,d);}
            }
        }return result;
    }
}
public sealed class ParcelRegion
{
    private readonly Paths64 _paths;
    public ReadOnlyCollection<ReadOnlyCollection<Vector2>> Rings {get;}
    public ReadOnlyCollection<Vector2> Triangles {get;}
    public double Area {get;}
    public Vector2 Center {get;}
    public Rect2 Bounds {get;}
    public ParcelRegion(Paths64 paths,bool triangulate=true){
        _paths=new Paths64(paths.Select(p=>new Path64(p)));Area=ParcelGeometry.Area(_paths);
        Rings=Array.AsReadOnly(_paths.Select(p=>Array.AsReadOnly(p.Select(ParcelGeometry.V).ToArray())).ToArray());
        if(_paths.Count==0){Triangles=Array.AsReadOnly(Array.Empty<Vector2>());return;}
        var points=Rings.SelectMany(p=>p).ToArray();var min=points.Aggregate((a,b)=>a.Min(b));var max=points.Aggregate((a,b)=>a.Max(b));Bounds=new(min,max-min);
        // Query-only masks need rings, area and bounds, not a render mesh. Large
        // unions can contain touching rings that are valid for winding queries.
        if(!triangulate){Triangles=Array.AsReadOnly(Array.Empty<Vector2>());double area2=0,cx=0,cy=0;foreach(var ring in Rings)for(int i=0;i<ring.Count;i++){var a=ring[i];var b=ring[(i+1)%ring.Count];double cross=(double)a.X*b.Y-(double)b.X*a.Y;area2+=cross;cx+=(a.X+b.X)*cross;cy+=(a.Y+b.Y)*cross;}Center=Math.Abs(area2)>1e-9?new((float)(cx/(3*area2)),(float)(cy/(3*area2))):Bounds.GetCenter();return;}
        Paths64 TrianglesFor(Paths64 paths,int depth){
            // Sub-square-centimetre slivers can be thinner than a float coordinate
            // at map scale. Keep their ownership/area, but omit only their mesh.
            if(ParcelGeometry.Area(paths)<.0001)return new();
            // Boolean cuts at a road corner can leave a zero-area out-and-back
            // spur. The triangulator requires simple contours; trim that spur on
            // a private copy, retaining the original ownership boundary.
            var clean=new Paths64(paths.Select(p=>Clipper.TrimCollinear(new Path64(p))).Where(p=>p.Count>=3));
            if(Math.Abs(ParcelGeometry.Area(clean)-ParcelGeometry.Area(paths))>.0001)throw new InvalidOperationException("退化边清理改变了轮廓面积");
            // A vertex can also touch the middle of another edge. Split such
            // contours into simple loops and verify triangle area, including holes.
            clean=ParcelGeometry.SeparateTouching(clean);
            var status=Clipper.Triangulate(clean,out var result);
            double expected=ParcelGeometry.Area(clean),drawn=result.Sum(p=>Math.Abs(Clipper.Area(p)))/(ParcelGeometry.Scale*ParcelGeometry.Scale);
            if(status==TriangulateResult.success&&Math.Abs(drawn-expected)<=Math.Max(.000001,expected*1e-9))return result;
            var slabs=ParcelGeometry.SlabTriangles(clean);double slabArea=slabs.Sum(p=>Math.Abs(Clipper.Area(p)))/(ParcelGeometry.Scale*ParcelGeometry.Scale);
            if(Math.Abs(slabArea-expected)<=Math.Max(.000001,expected*1e-9))return slabs;
            if(depth>=10)throw new InvalidOperationException($"轮廓三角化失败：面积 {expected:0.######}，三角形面积 {drawn:0.######}");
            result=new Paths64();var parts=ParcelGeometry.Components(paths);
            if(parts.Count>1){foreach(var part in parts)result.AddRange(TrianglesFor(part,depth+1));return result;}
            long minX=paths.SelectMany(p=>p).Min(p=>p.X),maxX=paths.SelectMany(p=>p).Max(p=>p.X),minY=paths.SelectMany(p=>p).Min(p=>p.Y),maxY=paths.SelectMany(p=>p).Max(p=>p.Y);
            bool vertical=maxX-minX>maxY-minY;long cut=vertical?(minX+maxX)/2:(minY+maxY)/2;
            foreach(bool first in new[]{true,false}){
                long x0=minX-1,x1=maxX+1,y0=minY-1,y1=maxY+1;if(vertical){if(first)x1=cut;else x0=cut;}else{if(first)y1=cut;else y0=cut;}
                var rectangle=new Paths64{new Path64{new Point64(x0,y0),new Point64(x1,y0),new Point64(x1,y1),new Point64(x0,y1)}};
                var piece=ParcelGeometry.Intersection(paths,rectangle);if(ParcelGeometry.Area(piece)>.000001)result.AddRange(TrianglesFor(piece,depth+1));
            }return result;
        }
        var triangles=TrianglesFor(_paths,0);
        if(Math.Abs(ParcelGeometry.Area(_paths)-Area)>.01)throw new InvalidOperationException("Triangulation changed original area");
        double drawnArea=triangles.Sum(p=>Math.Abs(Clipper.Area(p)))/(ParcelGeometry.Scale*ParcelGeometry.Scale);
        if(Math.Abs(drawnArea-Area)>.01)throw new InvalidOperationException($"轮廓网格面积不一致 expected={Area} actual={drawnArea}");
        Triangles=Array.AsReadOnly(triangles.SelectMany(p=>p.Select(ParcelGeometry.V)).ToArray());
        double sum=0;Vector2 center=Vector2.Zero;foreach(var t in triangles){double area=Math.Abs(Clipper.Area(t));center+=(ParcelGeometry.V(t[0])+ParcelGeometry.V(t[1])+ParcelGeometry.V(t[2]))/3*(float)area;sum+=area;}Center=sum>0?center/(float)sum:Bounds.GetCenter();
    }
    public bool Contains(Vector2 p)=>ParcelGeometry.Contains(_paths,p);
    internal Paths64 CopyPaths()=>new(_paths.Select(p=>new Path64(p)));
}
public sealed record ParcelOptions(float Width=8,float Depth=70,double MinimumArea=60,float MinimumFrontage=4,float RoadSetback=0,float SideSetback=0,float CommercialShare=.2f,float ProductionShare=.1f,int Seed=73129);
public enum ParcelUse { Residential,Commercial,Production }
public sealed record StreetFront(int RoadId,Vector2 A,Vector2 B){public float Length=>A.DistanceTo(B);}
public sealed record CityBlock(int Id,ParcelRegion Shape);
public sealed record LandParcel(int Id,int BlockId,ParcelUse Use,ParcelRegion Shape,ParcelRegion Buildable,ReadOnlyCollection<StreetFront> Frontage,float MaxSlope,float MinHeight,float MaxHeight,ParcelRegion? Yard=null,bool InsideWalls=false,bool AllowsPartyWalls=true,string DistrictId="legacy")
{
    public ReadOnlyCollection<string> Uses=>Array.AsReadOnly(Use switch{ParcelUse.Commercial=>new[]{"临街商铺","居住","后院"},ParcelUse.Production=>new[]{"手工作坊","居住","后园"},_=>new[]{"居住","后院或园地"}});
}
public sealed record ParcelRemainder(int BlockId,ParcelRegion Shape,string Reason);
public sealed class ParcelLayout
{
    public RoadNetwork Roads {get;} public SettlementLayout Settlement=>Roads.Settlement;public CityEnvironmentData Environment=>Roads.Environment;
    public RoadGrading? Grading {get;} public long GradingRevision {get;} public ITerrainSurface Terrain {get;}
    public long RoadRevision {get;}public ParcelOptions Options {get;}
    public ReadOnlyCollection<CityBlock> Blocks {get;}public ReadOnlyCollection<LandParcel> Parcels {get;}public ReadOnlyCollection<ParcelRemainder> Remainders {get;}
    public double InsideDevelopedArea=>Parcels.Where(p=>p.InsideWalls).Sum(p=>p.Shape.Area);
    public double OutsideDevelopedArea=>Parcels.Where(p=>!p.InsideWalls).Sum(p=>p.Shape.Area);
    public double AvailableArea=>Blocks.Sum(b=>b.Shape.Area);public double ParcelArea=>Parcels.Sum(p=>p.Shape.Area);public double RemainderArea=>Remainders.Sum(p=>p.Shape.Area);
    internal ParcelLayout(RoadNetwork roads,long revision,ParcelOptions options,List<CityBlock> blocks,List<LandParcel> parcels,List<ParcelRemainder> leftovers,RoadGrading? grading=null,long gradingRevision=0){Grading=grading;GradingRevision=gradingRevision;Terrain=grading?.Graded??(ITerrainSurface)new SampledTerrainSurface(roads.Environment);Roads=roads;RoadRevision=revision;Options=options;Blocks=Array.AsReadOnly(blocks.ToArray());Parcels=Array.AsReadOnly(parcels.ToArray());Remainders=Array.AsReadOnly(leftovers.ToArray());}
}

