using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;

// Heights of the terrain as rendered. Planning legality keeps using CityEnvironmentData;
// road vertical design and (later) placement heights use this surface.
public interface ITerrainSurface
{
    string Fingerprint {get;}
    Rect2 Bounds {get;}
    float Spacing {get;}
    bool Contains(Vector2 p);
    float HeightAt(Vector2 p);
    // Horizontal distance to open water (≤ 0 inside water).
    float WaterClearance(Vector2 p);
}
public sealed class NativeTerrainSurface:ITerrainSurface
{
    public TemperateMapData Map {get;}
    public string Fingerprint {get;}
    public Rect2 Bounds {get;}
    public float Spacing=>Map.Spacing;
    public NativeTerrainSurface(TemperateMapData map){Map=map;Bounds=new(-map.Half,-map.Half,map.Half*2,map.Half*2);Fingerprint="temperate/"+TerrainSurface.Digest(w=>{w.Write(map.Settings.Seed);w.Write(map.Settings.SizeMeters);w.Write(map.Settings.GridSegments);w.Write(map.Settings.RiverWidthMeters);foreach(float h in map.Heights)w.Write(h);});}
    public bool Contains(Vector2 p)=>p.IsFinite()&&Map.Contains(p);
    public float HeightAt(Vector2 p)=>Map.HeightAt(p);
    // Grid-interpolated distance to the river centerline minus the widest local half width.
    public float WaterClearance(Vector2 p)=>TerrainVisualData.Field(Map,Map.RiverDistances,p)-Map.Settings.RiverWidthMeters*.5f*1.18f;
}

public sealed record RoadGradingOptions(float SmoothingScale=20,float GradeChange=2,float Crossfall=2,float JunctionSlope=3,
    float PlazaSlope=2,float MaxCut=1.5f,float MaxFill=1,float BlendLength=4,float CutRatio=1,float FillRatio=1.5f,float Resolution=.5f,float BankBuffer=1.5f);
public enum RoadCrossSection { Crown, Kennel }
public sealed record GradePlane(string Id,int? NodeId,Vector2 Origin,float Height,Vector2 Gradient,float FittedSlope)
{
    public float HeightAt(Vector2 p)=>Height+Gradient.Dot(p-Origin);
    public float Slope=>Gradient.Length();
}
public sealed record GradingNotice(string Kind,int? RoadId,Vector2 Position,string Reason);
public sealed record RoadGradingSummary(double CutVolume,double FillVolume,float MaxCut,Vector2 MaxCutAt,float MaxFill,Vector2 MaxFillAt,
    float MaxGrade,float MaxGradeChange,int RelaxedProfiles,int ClampedPlanes,double SampledArea,float GroundRoughness,float DesignRoughness);
public sealed class RoadGradingFailure:InvalidOperationException
{
    public Vector2 Position {get;} public int? RoadId {get;}
    public RoadGradingFailure(Vector2 position,int? roadId,string message):base(message){Position=position;RoadId=roadId;}
}

// One road's longitudinal design: uniform stations along the shaped centerline.
public sealed class RoadProfile
{
    public int RoadId {get;}
    public float Width {get;}
    public float Length {get;}
    public RoadCrossSection Section {get;}
    public ReadOnlyCollection<float> Ground {get;}
    public ReadOnlyCollection<float> Design {get;}
    public ReadOnlyCollection<double> Elevations {get;}
    // Station index ranges [a,b] forced to a straight grade (gate passages).
    public ReadOnlyCollection<(int A,int B)> StraightRuns {get;}
    public bool GradeChangeRelaxed {get;}
    public float Step=>Length/(Design.Count-1);
    internal RoadProfile(int id,float width,float length,RoadCrossSection section,double[] ground,double[] design,List<(int,int)> runs,bool relaxed)
    {RoadId=id;Width=width;Length=length;Section=section;Ground=Array.AsReadOnly(ground.Select(v=>(float)v).ToArray());Design=Array.AsReadOnly(design.Select(v=>(float)v).ToArray());Elevations=Array.AsReadOnly((double[])design.Clone());StraightRuns=Array.AsReadOnly(runs.ToArray());GradeChangeRelaxed=relaxed;}
    public float StationAt(int i)=>i*Step;
    private double SlopeAt(int i){foreach(var run in StraightRuns)if(i>=run.A&&i<=run.B)return(Elevations[run.B]-Elevations[run.A])/((run.B-run.A)*Step);
        if(i==0)return(Elevations[1]-Elevations[0])/Step;if(i==Elevations.Count-1)return(Elevations[^1]-Elevations[^2])/Step;return(Elevations[i+1]-Elevations[i-1])/(2*Step);}
    public float HeightAt(float s){double x=Math.Clamp(s,0,Length)/Step;int i=Math.Min((int)x,Design.Count-2);double t=x-i,t2=t*t,t3=t2*t;
        return(float)((2*t3-3*t2+1)*Elevations[i]+(t3-2*t2+t)*Step*SlopeAt(i)+(-2*t3+3*t2)*Elevations[i+1]+(t3-t2)*Step*SlopeAt(i+1));}
    public float ContinuousMaxGradeChange {get{double worst=0;for(int i=0;i+1<Elevations.Count;i++){double d=Step,delta=Elevations[i+1]-Elevations[i],m0=SlopeAt(i),m1=SlopeAt(i+1);
        worst=Math.Max(worst,Math.Max(Math.Abs(6*delta/(d*d)-(4*m0+2*m1)/d),Math.Abs(-6*delta/(d*d)+(2*m0+4*m1)/d))*10);}return(float)worst;}}
    public float GroundAt(float s){float x=Math.Clamp(s,0,Length)/Step;int i=Math.Min((int)x,Ground.Count-2);return Mathf.Lerp(Ground[i],Ground[i+1],x-i);}
    public float MaxGrade{get{double worst=0;for(int i=0;i+1<Elevations.Count;i++){double delta=(Elevations[i+1]-Elevations[i])/Step,m0=SlopeAt(i),m1=SlopeAt(i+1),a=-6*delta+3*(m0+m1),b=6*delta-4*m0-2*m1;worst=Math.Max(worst,Math.Max(Math.Abs(m0),Math.Abs(m1)));
        if(Math.Abs(a)>1e-12){double t=-b/(2*a);if(t>0&&t<1)worst=Math.Max(worst,Math.Abs((a*t+b)*t+m0));}}return(float)worst;}}
    // Change of grade per 10 m, as a ratio.
    public float MaxGradeChange{get{float g=0;for(int i=1;i+1<Design.Count;i++)g=Math.Max(g,(float)(Math.Abs(Elevations[i-1]-2*Elevations[i]+Elevations[i+1])/(Step*Step)*10));return g;}}
    public float SectionOffset(float lateral,float crossfall){float o=Math.Min(Math.Abs(lateral),Width/2)*crossfall;return Section==RoadCrossSection.Crown?-o:o;}
}

// Exact transverse sweep over the analytic centerline owned by 03. This uses
// the path's true tangent and arc length instead of averaging neighbouring heights.
internal sealed class Centerline
{
    public readonly Vector2[] P;public readonly float[] Cum;
    private readonly RoadPlanarPath _path;
    public float Length=>_path.Length;
    public Centerline(RoadEdge road):this(road.Points,road.PlanarPath){}
    public Centerline(IReadOnlyList<Vector2> points,RoadPlanarPath? path=null){
        var list=new List<Vector2>{points[0]};foreach(var p in points.Skip(1))if(p.DistanceTo(list[^1])>.0005f)list.Add(p);
        if(list.Count<2)list.Add(list[0]+Vector2.Right*.001f);P=list.ToArray();Cum=new float[P.Length];for(int i=1;i<P.Length;i++)Cum[i]=Cum[i-1]+P[i].DistanceTo(P[i-1]);
        _path=path??RoadPlanarPath.Polyline(P);
    }
    public Vector2 PointAt(float s)=>_path.At(s).Point;
    public Vector2 TangentAt(float s)=>_path.At(s).Tangent;
    public Vector2 NormalAt(float s){var t=TangentAt(s);return new(-t.Y,t.X);}
    public void Prepare(){}
    public float Sweep(Vector2 p,RoadProfile profile,float crossfall){var hit=_path.Locate(p);return profile.HeightAt(hit.Station)+profile.SectionOffset(hit.Offset,crossfall);}
    public void Locate(Vector2 p,out float station,out float offset){var hit=_path.Locate(p);station=hit.Station;offset=hit.Offset;}
}

internal sealed class JunctionRegion
{
    public int NodeId;public GradePlane Plane=null!;public bool UsesPlaza;public ParcelRegion[] Shapes=Array.Empty<ParcelRegion>();public Vector2 Center;public float Radius;
    private Rect2? _bounds;
    public Rect2 Bounds=>_bounds??=Shapes.Length==0?new Rect2(Center-Vector2.One*Radius,Vector2.One*Radius*2):Shapes.Skip(1).Aggregate(Shapes[0].Bounds,(a,b)=>a.Merge(b.Bounds));
    public float Distance(Vector2 p)
    {
        if(Shapes.Length==0)return Math.Max(0,p.DistanceTo(Center)-Radius);
        float best=float.PositiveInfinity;
        foreach(var s in Shapes){if(s.Bounds.Grow(.01f).HasPoint(p)&&s.Contains(p))return 0;
            foreach(var ring in s.Rings)for(int i=0;i<ring.Count;i++)best=Math.Min(best,RoadGeometry.Distance(p,ring[i],ring[(i+1)%ring.Count]));}
        return best;
    }
}

public sealed class RoadGrading
{
    public RoadNetwork Roads {get;}
    public long RoadRevision {get;}
    public ITerrainSurface Terrain {get;}
    public RoadGradingOptions Options {get;}
    public ReadOnlyCollection<RoadProfile> Profiles {get;}
    public ReadOnlyDictionary<int,float> NodeHeights {get;}
    public ReadOnlyCollection<GradePlane> Planes {get;}
    public GradePlane Plaza {get;}
    public Rect2 PlazaRect {get;}
    public ReadOnlyCollection<GradingNotice> Notices {get;private set;}
    public RoadGradingSummary Summary {get;private set;}=null!;
    public SurfaceEarthworkAudit EarthworkAudit {get;internal set;}=null!;
    private readonly Dictionary<int,RoadProfile> _profiles;
    private readonly Dictionary<int,Centerline> _lines;
    private readonly Dictionary<int,JunctionRegion> _junctions;
    private readonly List<JunctionRegion> _junctionList;
    private readonly Dictionary<long,List<int>> _index=new();
    private const float Cell=16;
    internal RoadGrading(RoadNetwork roads,long revision,ITerrainSurface terrain,RoadGradingOptions options,List<RoadProfile> profiles,Dictionary<int,Centerline> lines,
        Dictionary<int,float> nodes,Dictionary<int,JunctionRegion> junctions,GradePlane plaza,List<GradingNotice> notices)
    {
        Roads=roads;RoadRevision=revision;Terrain=terrain;Options=options;Profiles=Array.AsReadOnly(profiles.ToArray());NodeHeights=new(new Dictionary<int,float>(nodes));
        Plaza=plaza;PlazaRect=roads.Settlement.Plaza;Notices=Array.AsReadOnly(notices.ToArray());_profiles=profiles.ToDictionary(p=>p.RoadId);_lines=lines;_junctions=junctions;
        Planes=Array.AsReadOnly(junctions.Values.Where(j=>!j.UsesPlaza).Select(j=>j.Plane).Distinct().OrderBy(p=>p.NodeId).ToArray());
        _junctionList=junctions.Values.OrderBy(j=>j.NodeId).ToList();
        // Lazily cached geometry is filled here so the design can be queried from several threads.
        foreach(var j in _junctionList)_=j.Bounds;foreach(var line in lines.Values)line.Prepare();
        var surfaces=roads.Geometry.Surfaces;
        for(int i=0;i<surfaces.Count;i++){var b=surfaces[i].Shape.Bounds;
            for(int z=(int)Math.Floor(b.Position.Y/Cell);z<=(int)Math.Floor(b.End.Y/Cell);z++)for(int x=(int)Math.Floor(b.Position.X/Cell);x<=(int)Math.Floor(b.End.X/Cell);x++){
                long key=Key(x,z);if(!_index.TryGetValue(key,out var list))_index[key]=list=new();list.Add(i);}}
    }
    internal void Finish(RoadGradingSummary summary,List<GradingNotice> notices){Summary=summary;Notices=Array.AsReadOnly(notices.ToArray());}
    private static long Key(int x,int z)=>((long)x<<32)^(uint)z;
    public RoadProfile? Profile(int roadId)=>_profiles.GetValueOrDefault(roadId);
    public float GroundHeight(Vector2 p)=>Terrain.HeightAt(p);
    public bool InPlaza(Vector2 p)=>PlazaRect.HasPoint(p);
    public RoadSurface? SurfaceAt(Vector2 p)
    {
        if(!_index.TryGetValue(Key((int)Math.Floor(p.X/Cell),(int)Math.Floor(p.Y/Cell)),out var list))return null;
        foreach(int i in list){var s=Roads.Geometry.Surfaces[i];if(s.Shape.Bounds.Grow(.001f).HasPoint(p)&&s.Shape.Contains(p))return s;}
        // Points on a shared boundary can fall between integer point-in-polygon tests.
        RoadSurface? near=null;float best=.01f;foreach(int i in list){var s=Roads.Geometry.Surfaces[i];float d=SurfaceDistance(s,p,best);if(d<best){best=d;near=s;}}
        return near;
    }
    // The design surface over the road area and the plaza. Every value is a blend whose
    // weights depend only on distances to surfaces, so both sides of any seam agree:
    // neighbouring road sweeps mix with finite weights; junction and plaza planes are
    // reached exactly on their boundaries.
    public bool TryDesignHeight(Vector2 p,out float height)
    {
        height=0;if(!p.IsFinite())return false;
        if(InPlaza(p)){height=Plaza.HeightAt(p);return true;}
        var surface=SurfaceAt(p);if(surface==null)return false;
        if(surface.NodeId is int node&&_junctions.TryGetValue(node,out var junction)){height=JunctionValue(junction,p);return true;}
        height=EdgeValue(surface,p);return true;
    }
    // Continuous extension of the design just outside the road area, for shoulders.
    public bool TryDesignHeightNear(Vector2 p,float reach,out float height)=>TryDesignHeightNear(p,reach,out height,out _);
    public bool TryDesignHeightNear(Vector2 p,float reach,out float height,out float distance)
    {
        distance=0;if(TryDesignHeight(p,out height))return true;float best=PlazaDistance(p);RoadSurface? near=null;
        foreach(int i in Nearby(p,reach)){var s=Roads.Geometry.Surfaces[i];float d=SurfaceDistance(s,p,Math.Min(best,reach+.001f));if(d<best){best=d;near=s;}}
        distance=best;if(best>reach)return false;
        if(near==null){height=Plaza.HeightAt(p);return true;}
        height=EdgeValue(near,p);return true;
    }
    public GradedTerrain? Graded {get;internal set;}
    public (float Station,float Offset) Locate(int roadId,Vector2 p){_lines[roadId].Locate(p,out float s,out float o);return (s,o);}
    public float SweepHeight(int roadId,Vector2 p)=>_lines[roadId].Sweep(p,_profiles[roadId],Options.Crossfall/100);
    // True where the design is one road's sweep alone (flat cross-section).
    public bool IsPureSweep(Vector2 p)
    {
        float t=Options.BlendLength;if(InPlaza(p)||PlazaDistance(p)<t)return false;var surface=SurfaceAt(p);if(surface==null||IsJunctionSurface(surface))return false;
        foreach(int i in Nearby(p,t)){var other=Roads.Geometry.Surfaces[i];if(other.Id!=surface.Id&&!IsJunctionSurface(other)&&other.RoadId!=surface.RoadId&&SurfaceDistance(other,p,t)<t)return false;}
        foreach(var j in _junctionList)if(j.Bounds.Grow(t).HasPoint(p)&&j.Distance(p)<t)return false;return true;
    }
    public float PlazaDistance(Vector2 p){float dx=Math.Max(0,Math.Max(PlazaRect.Position.X-p.X,p.X-PlazaRect.End.X)),dz=Math.Max(0,Math.Max(PlazaRect.Position.Y-p.Y,p.Y-PlazaRect.End.Y));return MathF.Sqrt(dx*dx+dz*dz);}
    private bool IsJunctionSurface(RoadSurface s)=>s.NodeId is int n&&_junctions.ContainsKey(n);
    private List<int> Nearby(Vector2 p,float r)
    {
        var result=new List<int>(8);
        for(int z=(int)Math.Floor((p.Y-r)/Cell);z<=(int)Math.Floor((p.Y+r)/Cell);z++)for(int x=(int)Math.Floor((p.X-r)/Cell);x<=(int)Math.Floor((p.X+r)/Cell);x++)
            if(_index.TryGetValue(Key(x,z),out var list))foreach(int i in list)if(!result.Contains(i))result.Add(i);
        return result;
    }
    private static float SurfaceDistance(RoadSurface s,Vector2 p,float limit)
    {
        if(!s.Shape.Bounds.Grow(limit).HasPoint(p))return limit;if(s.Shape.Bounds.Grow(.001f).HasPoint(p)&&s.Shape.Contains(p))return 0;
        float best=limit;foreach(var ring in s.Shape.Rings)for(int i=0;i<ring.Count;i++)best=Math.Min(best,RoadGeometry.Distance(p,ring[i],ring[(i+1)%ring.Count]));return best;
    }
    private float JunctionValue(JunctionRegion j,Vector2 p)=>j.UsesPlaza?Plaza.HeightAt(p):Blend(j.Plane.HeightAt(p),stackalloc (float,float)[]{(Plaza.HeightAt(p),PlazaDistance(p))});
    internal string DescribeBlend(Vector2 p)
    {
        float t=Options.BlendLength;var parts=new List<string>();var own=SurfaceAt(p);
        foreach(int i in Nearby(p,t)){var s=Roads.Geometry.Surfaces[i];if(IsJunctionSurface(s))continue;float d=own!=null&&s.Id==own.Id?0:SurfaceDistance(s,p,t);if(d>=t)continue;var (st,off)=Locate(s.RoadId,p);parts.Add($"S{s.Id}/R{s.RoadId} d={d:0.000} v={SweepHeight(s.RoadId,p):0.000} s={st:0.00} o={off:0.00}");}
        foreach(var j in _junctionList){if(!j.Bounds.Grow(t).HasPoint(p))continue;float d=j.Distance(p);if(d<t)parts.Add($"J{j.NodeId} d={d:0.000} v={JunctionValue(j,p):0.000}");}
        parts.Add($"plaza d={PlazaDistance(p):0.000} v={Plaza.HeightAt(p):0.000}");return string.Join("; ",parts);
    }
    private float EdgeValue(RoadSurface own,Vector2 p)
    {
        float t=Options.BlendLength;double sum=0,weight=0;
        foreach(int i in Nearby(p,t)){var s=Roads.Geometry.Surfaces[i];if(IsJunctionSurface(s))continue;float d=SurfaceDistance(s,p,t);if(d>=t)continue;
            double w=1-d/t;w*=w;sum+=w*SweepHeight(s.RoadId,p);weight+=w;}
        float sweep=weight>0?(float)(sum/weight):SweepHeight(own.RoadId,p);
        var regions=new List<(float,float)>(4){(Plaza.HeightAt(p),PlazaDistance(p))};
        foreach(var j in _junctionList){if(!j.Bounds.Grow(t).HasPoint(p))continue;float d=j.Distance(p);if(d<t)regions.Add((JunctionValue(j,p),d));}
        return Blend(sweep,System.Runtime.InteropServices.CollectionsMarshal.AsSpan(regions));
    }
    // Compact Shepard weights: a region's value is reached exactly at distance zero.
    private float Blend(float baseline,ReadOnlySpan<(float Value,float Distance)> regions)
    {
        double sum=baseline,weight=1;float t=Options.BlendLength;
        foreach(var (value,d) in regions){if(d<=1e-4f)return value;if(d>=t)continue;double w=(t/d-1);w*=w;sum+=w*value;weight+=w;}
        return (float)(sum/weight);
    }
}

public static class RoadGradingGenerator
{
    public static Task<RoadGrading> Generate(RoadNetwork roads,long roadRevision,ITerrainSurface terrain,RoadGradingOptions o,PcgContext ctx)=>GenerateInternal(roads,roadRevision,terrain,o,ctx,null);
    private static async Task<RoadGrading> GenerateInternal(RoadNetwork roads,long roadRevision,ITerrainSurface terrain,RoadGradingOptions o,PcgContext ctx,Dictionary<int,ProfileBounds>? repairs,float surfaceMargin=0,int surfaceAttempt=0)
    {
        Validate(o);var constrained=o with{MaxCut=o.MaxCut-surfaceMargin,MaxFill=o.MaxFill-surfaceMargin};var env=roads.Environment.Bounds;
        foreach(var corner in new[]{env.Position,env.End,new Vector2(env.Position.X,env.End.Y),new Vector2(env.End.X,env.Position.Y)})
            if(!terrain.Bounds.Grow(.01f).HasPoint(corner))throw new ArgumentException("道路竖向的地形与道路所用环境范围不一致");
        var token=ctx.Cancellation;var notices=new List<GradingNotice>();var layout=roads.Settlement;var edges=roads.Edges;
        float maxGrade=MathF.Tan(Mathf.DegToRad(roads.Options.MaxGrade));
        float Ground(Vector2 p)=>terrain.HeightAt(p);
        // Roads near the plaza must come up to it: |z − plaza| ≤ k·distance within this range.
        float approach=maxGrade*.5f;const float approachRange=30;
        float PlazaGap(Vector2 p){var r=layout.Plaza;float dx=Math.Max(0,Math.Max(r.Position.X-p.X,p.X-r.End.X)),dz=Math.Max(0,Math.Max(r.Position.Y-p.Y,p.Y-r.End.Y));return MathF.Sqrt(dx*dx+dz*dz);}
        async Task Step(float fraction,string message){token.ThrowIfCancellationRequested();if(ctx.Yield!=null)await ctx.Yield(new(fraction,message));}
        await Step(0,"道路竖向：广场与节点");

        // Plaza plane: least squares on terrain, slope limited.
        var plazaRect=layout.Plaza;var plazaSamples=new List<(Vector2,float)>();
        for(float z=plazaRect.Position.Y+1;z<plazaRect.End.Y;z+=2)for(float x=plazaRect.Position.X+1;x<plazaRect.End.X;x+=2){var p=new Vector2(x,z);if(terrain.Contains(p))plazaSamples.Add((p,Ground(p)));}
        if(plazaSamples.Count==0)plazaSamples.Add((layout.Core,Ground(layout.Core)));
        var plaza=FitPlane("plaza",null,plazaRect.GetCenter(),plazaSamples,o.PlazaSlope/100,out bool plazaClamped);
        plaza=GradingEarthworkLimits.ConstrainPlane(plaza,terrain,new ParcelRegion(ParcelGeometry.Rect(plazaRect)),o.PlazaSlope/100,constrained,token);
        if(plazaClamped)notices.Add(new("plaza",null,plazaRect.GetCenter(),$"广场坡度受限：地形 {plaza.FittedSlope*100:0.0}% → {plaza.Slope*100:0.0}%"));

        // Node heights: long-range terrain average; entries keep the outside terrain; plaza nodes follow the plaza.
        var byNode=edges.SelectMany(e=>new[]{(Node:e.From,Edge:e),(Node:e.To,Edge:e)}).GroupBy(x=>x.Node).ToDictionary(g=>g.Key,g=>g.Select(x=>x.Edge).ToArray());
        var entryNodes=layout.Entries.Where(e=>roads.Anchors.ContainsKey(e.Id)).Select(e=>roads.Anchors[e.Id]).ToHashSet();
        var heights=new Dictionary<int,float>();var fixedNodes=new HashSet<int>();var nodeGround=new Dictionary<int,float>();
        float radius=o.SmoothingScale*.75f;
        RoadCrossSection Section(RoadEdge e,Centerline line)=>(e.Role is StreetRole.MarketStreet or StreetRole.GateStreet)&&layout.Town!=null&&layout.Town.Interior.Contains(line.PointAt(line.Length/2))?RoadCrossSection.Kennel:RoadCrossSection.Crown;
        var nodeLimits=roads.Nodes.ToDictionary(v=>v.Id,v=>(Low:(double)Ground(v.Position)-o.MaxCut,High:(double)Ground(v.Position)+o.MaxFill));
        foreach(var edge in edges){var line=new Centerline(edge);foreach(var (id,station) in new[]{(edge.From,0f),(edge.To,line.Length)}){
            if(plazaRect.HasPoint(line.PointAt(station)))continue;
            GradingEarthworkLimits.CrossSectionBounds(line,station,edge.Width,Section(edge,line),terrain,constrained,out double lo,out double hi);
            var prior=nodeLimits[id];nodeLimits[id]=(Math.Max(prior.Low,lo),Math.Min(prior.High,hi));}}
        float BoundNode(int id,float h){var limits=nodeLimits[id];if(limits.Low>limits.High)throw new RoadGradingFailure(roads.Nodes.First(n=>n.Id==id).Position,null,"路口相连道路的完整截面挖填约束无公共解");
            if(fixedNodes.Contains(id)&&(h<limits.Low-.001||h>limits.High+.001))throw new RoadGradingFailure(roads.Nodes.First(n=>n.Id==id).Position,null,"固定入口与完整截面挖填约束冲突");return (float)Math.Clamp(h,limits.Low,limits.High);}
        float RestrictNode(int id,float height){if(repairs==null)return height;double low=double.NegativeInfinity,high=double.PositiveInfinity;foreach(var edge in byNode[id])if(repairs.TryGetValue(edge.Id,out var bounds)){int at=edge.From==id?0:bounds.Lower.Length-1;low=Math.Max(low,bounds.Lower[at]);high=Math.Min(high,bounds.Upper[at]);}if(low>high)throw new RoadGradingFailure(roads.Nodes.First(n=>n.Id==id).Position,null,"锁定区约束与节点高程冲突");if(fixedNodes.Contains(id)&&(height<low||height>high))throw new RoadGradingFailure(roads.Nodes.First(n=>n.Id==id).Position,null,"锁定区约束与固定节点冲突");return (float)Math.Clamp(height,low,high);}
        foreach(var node in roads.Nodes){float t=Ground(node.Position);nodeGround[node.Id]=t;float h;
            if(entryNodes.Contains(node.Id)){h=t;fixedNodes.Add(node.Id);}
            else if(plazaRect.HasPoint(node.Position)){h=plaza.HeightAt(node.Position);}
            else if(radius<1)h=t;
            else{double sum=0;int count=0;for(float dz=-radius;dz<=radius;dz+=3)for(float dx=-radius;dx<=radius;dx+=3){if(dx*dx+dz*dz>radius*radius)continue;var p=node.Position+new Vector2(dx,dz);if(!terrain.Contains(p))continue;sum+=Ground(p);count++;}h=count>0?(float)(sum/count):t;}
            h=Math.Clamp(h,t-o.MaxCut,t+o.MaxFill);float gap=PlazaGap(node.Position);
            if(gap<approachRange){float ph=plaza.HeightAt(node.Position);h=Math.Clamp(h,ph-approach*gap,ph+approach*gap);}
            heights[node.Id]=BoundNode(node.Id,RestrictNode(node.Id,h));}
        // Keep node pairs reachable within the grade limit; the terrain itself satisfies it along the route.
        for(int sweep=0;sweep<30;sweep++){bool changed=false;
            foreach(var e in edges){float allowed=maxGrade*e.Length*.9f;float a=heights[e.From],b=heights[e.To];if(Math.Abs(a-b)<=allowed)continue;
                bool fa=fixedNodes.Contains(e.From),fb=fixedNodes.Contains(e.To);if(fa&&fb)continue;changed=true;
                if(fa)heights[e.To]=Math.Clamp(b,a-allowed,a+allowed);else if(fb)heights[e.From]=Math.Clamp(a,b-allowed,b+allowed);
                else{float mid=(a+b)/2,half=allowed/2*Math.Sign(a-b);heights[e.From]=mid+half;heights[e.To]=mid-half;}
                foreach(int n in new[]{e.From,e.To}){if(fixedNodes.Contains(n))continue;var position=roads.Nodes.First(v=>v.Id==n).Position;float h=Math.Clamp(heights[n],nodeGround[n]-o.MaxCut,nodeGround[n]+o.MaxFill);
                    float gap=PlazaGap(position);if(gap<approachRange){float ph=plaza.HeightAt(position);h=Math.Clamp(h,ph-approach*gap,ph+approach*gap);}heights[n]=BoundNode(n,RestrictNode(n,h));}}
            if(!changed)break;}

        // Longitudinal profiles.
        var lines=new Dictionary<int,Centerline>();var profiles=new List<RoadProfile>();var problems=new List<VerticalProblem>();var gates=layout.Town?.Gates.Select(g=>g.Passage).ToArray()??Array.Empty<ParcelRegion>();
        for(int k=0;k<edges.Count;k++){
            if(k%8==0)await Step(.1f+.6f*k/edges.Count,"道路竖向：纵断面");
            var e=edges[k];var line=new Centerline(e);lines[e.Id]=line;
            int n=Math.Max(2,(int)Math.Ceiling(line.Length/1f));float ds=line.Length/n;
            var points=Enumerable.Range(0,n+1).Select(i=>line.PointAt(i*ds)).ToArray();var t=points.Select(p=>(double)Ground(p)).ToArray();
            var target=Smooth(t,o.SmoothingScale/2/ds);double h0=heights[e.From],h1=heights[e.To];double c0=h0-target[0],c1=h1-target[n];
            for(int i=0;i<=n;i++){double u=(double)i/n;target[i]+=c0*(1-u)+c1*u;}
            var lo=t.Select(v=>v-o.MaxCut).ToArray();var hi=t.Select(v=>v+o.MaxFill).ToArray();var section=Section(e,line);
            for(int i=0;i<=n;i++)if(PlazaGap(points[i])>0){GradingEarthworkLimits.CrossSectionBounds(line,i*ds,e.Width,section,terrain,constrained,out double lower,out double upper);lo[i]=Math.Max(lo[i],lower);hi[i]=Math.Min(hi[i],upper);
                if(lo[i]>hi[i])throw new RoadGradingFailure(points[i],e.Id,"完整道路截面在挖填上限内无解");}
            var plazaLow=Enumerable.Repeat(double.NegativeInfinity,n+1).ToArray();var plazaHigh=Enumerable.Repeat(double.PositiveInfinity,n+1).ToArray();
            for(int i=0;i<=n;i++){float gap=PlazaGap(points[i]);if(gap>=approachRange)continue;double ph=plaza.HeightAt(points[i]);plazaLow[i]=ph-approach*gap;plazaHigh[i]=ph+approach*gap;}
            if(repairs!=null&&repairs.TryGetValue(e.Id,out var boxes))for(int i=0;i<=n;i++){lo[i]=Math.Max(lo[i],boxes.Lower[i]);hi[i]=Math.Min(hi[i],boxes.Upper[i]);if(lo[i]>hi[i])throw new RoadGradingFailure(points[i],e.Id,"边坡约束在挖填限制内无解");}
            var runs=new List<(int,int)>();int start=-1;
            for(int i=0;i<=n+1;i++){bool inside=i<=n&&gates.Any(g=>g.Bounds.HasPoint(points[i])&&g.Contains(points[i]));if(inside&&start<0)start=i;if(!inside&&start>=0){runs.Add((Math.Max(0,start-1),Math.Min(n,i)));start=-1;}}
            problems.Add(new(e,line,section,t,target,lo,hi,runs,plazaLow,plazaHigh));
        }
        await Step(.72f,"道路竖向：联合求解严格坡度变化");
        var plazaPoints=GradingEarthworkLimits.Samples(terrain,new ParcelRegion(ParcelGeometry.Rect(plazaRect))).ToArray();
        var plazaOffsets=plazaPoints.Select(p=>Ground(p)-plaza.HeightAt(p)).ToArray();
        var plazaBounds=(Low:(double)plazaOffsets.Max()-constrained.MaxCut,High:(double)plazaOffsets.Min()+constrained.MaxFill);
        var solved=await Task.Run(()=>JointVerticalSolver.Solve(problems,heights,fixedNodes,maxGrade,o.GradeChange,plazaBounds,token,new JointPlaza(plaza,plazaPoints,plazaOffsets.Select(d=>(double)d-constrained.MaxCut).ToArray(),plazaOffsets.Select(d=>(double)d+constrained.MaxFill).ToArray(),o.PlazaSlope/100)),token);
        plaza=plaza with{Height=plaza.Height+solved.PlazaOffset,Gradient=plaza.Gradient+solved.PlazaGradientOffset};
        if(Math.Abs(solved.PlazaOffset)>.001f)notices.Add(new("plaza_adjustment",null,plaza.Origin,$"广场整体高程调整 {solved.PlazaOffset:0.000} m，使相连道路在原坡度变化上限内接顺"));
        foreach(var p in problems)profiles.Add(new(p.Road.Id,p.Road.Width,p.Line.Length,p.Section,p.Ground,solved.Profiles[p.Road.Id],p.StraightRuns,false));
        var profileById=profiles.ToDictionary(p=>p.RoadId);

        // Junction platforms: plane fitted to the arriving cross-sections.
        await Step(.75f,"道路竖向：路口平台");
        var junctions=new Dictionary<int,JunctionRegion>();var samplesByNode=new Dictionary<int,List<(Vector2,float)>>();
        foreach(var node in roads.Nodes){
            if(!byNode.TryGetValue(node.Id,out var incident)||incident.Length<2||entryNodes.Contains(node.Id))continue;
            var shapes=roads.Geometry.Surfaces.Where(s=>s.NodeId==node.Id).Select(s=>s.Shape).ToArray();
            var region=new JunctionRegion{NodeId=node.Id,Shapes=shapes,Center=node.Position,Radius=shapes.Length==0?incident.Max(e=>e.Width)*.75f:0};
            junctions[node.Id]=region;region.UsesPlaza=plazaRect.HasPoint(node.Position);
            var samples=samplesByNode[node.Id]=new();
            foreach(var e in incident){var line=lines[e.Id];var profile=profileById[e.Id];bool atStart=e.From==node.Id;
                float s=0;while(s<line.Length/2&&region.Distance(line.PointAt(atStart?s:line.Length-s))<=0)s+=.5f;float station=atStart?s:line.Length-s;
                var c=line.PointAt(station);var normal=line.NormalAt(station);float z=profile.HeightAt(station);
                foreach(float side in new[]{-1f,0,1f}){float lateral=side*e.Width/2;samples.Add((c+normal*lateral,z+profile.SectionOffset(lateral,o.Crossfall/100)));}}
        }
        // Junctions closer than the blend length share one platform, so neighbouring
        // platforms never meet at a step.
        var ids=junctions.Keys.OrderBy(k=>k).ToArray();var parent=ids.ToDictionary(k=>k,k=>k);
        int Root(int k){while(parent[k]!=k)k=parent[k]=parent[parent[k]];return k;}
        float Gap(JunctionRegion a,JunctionRegion b){
            IEnumerable<Vector2> Points(JunctionRegion r)=>r.Shapes.Length==0?Enumerable.Range(0,16).Select(i=>r.Center+Vector2.FromAngle(i*Mathf.Tau/16)*r.Radius):r.Shapes.SelectMany(x=>x.Rings.SelectMany(ring=>ring));
            return Math.Min(Points(a).Min(b.Distance),Points(b).Min(a.Distance));}
        for(int i=0;i<ids.Length;i++)for(int j=i+1;j<ids.Length;j++){var a=junctions[ids[i]];var b=junctions[ids[j]];
            if(!a.Bounds.Grow(o.BlendLength).Intersects(b.Bounds))continue;if(Gap(a,b)<o.BlendLength)parent[Root(ids[i])]=Root(ids[j]);}
        foreach(var cluster in ids.GroupBy(Root)){
            var members=cluster.OrderBy(k=>k).ToArray();var first=junctions[members[0]];
            if(members.Any(k=>junctions[k].UsesPlaza)){foreach(int k in members){junctions[k].UsesPlaza=true;junctions[k].Plane=plaza;}continue;}
            var origin=members.Aggregate(Vector2.Zero,(sum,k)=>sum+junctions[k].Center)/members.Length;
            var plane=FitPlane("J"+string.Join("+",members),members[0],origin,members.SelectMany(k=>samplesByNode[k]).ToList(),o.JunctionSlope/100,out bool clamped);
            var shapes=members.SelectMany(k=>junctions[k].Shapes).ToArray();
            if(shapes.Length>0)plane=GradingEarthworkLimits.ConstrainPlane(plane,terrain,new ParcelRegion(ParcelGeometry.Union(new Clipper2Lib.Paths64(shapes.SelectMany(s=>s.CopyPaths())))),o.JunctionSlope/100,constrained,token);
            foreach(int k in members)junctions[k].Plane=plane;
            if(clamped)notices.Add(new("junction",byNode[members[0]][0].Id,origin,$"路口 {string.Join("+",members)} 平台坡度受限：{plane.FittedSlope*100:0.0}% → {plane.Slope*100:0.0}%"));
        }

        var result=new RoadGrading(roads,roadRevision,terrain,o,profiles,lines,heights,junctions,plaza,notices);
        await Step(.85f,"道路竖向：挖填统计");
        result.Finish(Summarize(result,notices),notices);
        await Step(.88f,"道路竖向：完整设计面挖填复查");
        result.EarthworkAudit=await Task.Run(()=>GradingEarthworkLimits.Audit(result,token),token);
        if(result.EarthworkAudit.Violations>0){var a=result.EarthworkAudit;float margin=surfaceMargin+a.WorstExcess+.005f;
            if(surfaceAttempt<3&&margin<(o.MaxCut+o.MaxFill)/2){await Step(.89f,$"完整路面超限 {a.WorstExcess:0.000} m：收紧设计后重解 {surfaceAttempt+1}/3");return await GenerateInternal(roads,roadRevision,terrain,o,ctx,repairs,margin,surfaceAttempt+1);}
            throw new RoadGradingFailure(a.WorstPosition,a.WorstRoadId,$"完整设计面 R{a.WorstRoadId} {a.WorstPosition} 超出挖填上限 {a.WorstExcess:0.000} m，{a.Violations} 个复查点不合格");}
        if(surfaceMargin>0)notices.Add(new("earthwork_guard",null,result.PlazaRect.GetCenter(),$"面上复查后重解 {surfaceAttempt} 次，设计预留 {surfaceMargin:0.000} m；用户挖填上限保持不变"));
        await Step(.9f,"地形修整：坡度受限包络");
        result.Graded=await GradedTerrainBuilder.Build(result,ctx);
        if(repairs==null&&result.Graded.RetainingWalls.Count>0){
            var constraints=RoadTerrainConstraints.FromConflicts(result);
            if(constraints.Count>0)try{var retry=await GenerateInternal(roads,roadRevision,terrain,o,ctx,constraints,surfaceMargin,surfaceAttempt);if(retry.Graded!.Earthwork.Conflicts<result.Graded.Earthwork.Conflicts){var repairedNotices=retry.Notices.ToList();repairedNotices.Add(new("terrain_retry",null,retry.PlazaRect.GetCenter(),$"收紧纵断面后，边坡冲突 {result.Graded.Earthwork.Conflicts} → {retry.Graded.Earthwork.Conflicts}"));retry.Finish(retry.Summary,repairedNotices);return retry;}}
                catch(RoadGradingFailure){/* Keep the feasible original design and report its retaining walls. */}
            notices.Add(new("terrain_retry",null,result.PlazaRect.GetCenter(),"收紧纵断面重试后仍有冲突，保留锁定区并登记挡土墙"));
        }
        foreach(var wall in result.Graded.RetainingWalls)notices.Add(new("retaining",null,wall.Center,$"需挡土墙：高差 {wall.Step:0.00} m，约 {wall.Length:0.0} m"));
        result.Finish(result.Summary,notices);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static void Validate(RoadGradingOptions o)
    {
        if(!(o.SmoothingScale>=0&&o.SmoothingScale<=200))throw new ArgumentOutOfRangeException(nameof(o.SmoothingScale),"长程平滑尺度需在 0–200 m");
        if(!(o.GradeChange>0&&o.GradeChange<=50))throw new ArgumentOutOfRangeException(nameof(o.GradeChange),"坡度变化上限需大于 0");
        if(!(o.Crossfall>=0&&o.Crossfall<=8))throw new ArgumentOutOfRangeException(nameof(o.Crossfall),"横坡需在 0–8%");
        if(new[]{o.SmoothingScale,o.GradeChange,o.Crossfall,o.JunctionSlope,o.PlazaSlope,o.MaxCut,o.MaxFill,o.BlendLength,o.CutRatio,o.FillRatio,o.Resolution,o.BankBuffer}.Any(v=>!float.IsFinite(v)))throw new ArgumentException("竖向参数必须是有限数值");
        if(!(o.JunctionSlope>=0&&o.PlazaSlope>=0&&o.MaxCut>=0&&o.MaxFill>=0&&o.BlendLength>0))throw new ArgumentOutOfRangeException(nameof(o),"竖向参数不能为负");
        if(!(o.CutRatio>=.25f&&o.CutRatio<=10&&o.FillRatio>=.25f&&o.FillRatio<=10))throw new ArgumentOutOfRangeException(nameof(o),"边坡坡率需在 1:0.25–1:10");
        if(!(o.Resolution>=.25f&&o.Resolution<=2&&o.BankBuffer>=0))throw new ArgumentOutOfRangeException(nameof(o),"修整精度需在 0.25–2 m");
    }
    // Gaussian low-pass with renormalised weights at the ends.
    internal static double[] Smooth(double[] values,double sigma)
    {
        if(sigma<.5)return (double[])values.Clone();int r=(int)Math.Ceiling(sigma*3);var kernel=Enumerable.Range(-r,2*r+1).Select(i=>Math.Exp(-.5*i*i/(sigma*sigma))).ToArray();
        var result=new double[values.Length];for(int i=0;i<values.Length;i++){double sum=0,w=0;for(int k=-r;k<=r;k++){int j=i+k;if(j<0||j>=values.Length)continue;sum+=values[j]*kernel[k+r];w+=kernel[k+r];}result[i]=sum/w;}
        return result;
    }
    // Profile closest (least squares) to the target under box constraints on heights
    // (cut/fill, pinned ends), first differences (grade) and second differences
    // (grade change; zero inside straight runs). ADMM gives the optimum; a short
    // projection pass then makes the hard limits exact. Grade and bounds are hard; the
    // grade-change limit is a hard constraint; infeasibility never increases it.
    internal static double[]? Solve(double[] target,double[] lo,double[] hi,double h0,double h1,double step,double maxStep,double maxCurvature,List<(int A,int B)> runs,out bool relaxed,out int worst)
    {
        int n=target.Length-1;relaxed=false;worst=0;
        if(h0<lo[0]-1e-6||h0>hi[0]+1e-6)return null;
        if(h1<lo[n]-1e-6||h1>hi[n]+1e-6){worst=n;return null;}
        var inRun=new bool[n+1];foreach(var (a,b) in runs)for(int i=a+1;i<b;i++)inRun[i]=true;
        var low=(double[])lo.Clone();var high=(double[])hi.Clone();low[0]=high[0]=h0;low[n]=high[n]=h1;
        var limit=Enumerable.Repeat(maxCurvature,n+1).ToArray();
        var z=Admm(target,low,high,maxStep,maxCurvature,limit,inRun);
        return Project(z,low,high,maxStep,limit,runs,out worst)?z:null;
    }
    // Final projection: makes the hard limits exact from a nearly feasible profile.
    private static bool Project(double[] z,double[] low,double[] high,double maxStep,double[] limit,List<(int A,int B)> runs,out int worst)
    {
        int n=z.Length-1;worst=0;
        for(int iteration=0;iteration<4000;iteration++){
            for(int pass=0;pass<2;pass++)for(int k=1;k<n;k++){int i=pass==0?k:n-k;double d2=z[i-1]-2*z[i]+z[i+1];if(d2>limit[i])z[i]+=(d2-limit[i])/2;else if(d2<-limit[i])z[i]+=(d2+limit[i])/2;}
            foreach(var (a,b) in runs)for(int i=a+1;i<b;i++)z[i]=z[a]+(z[b]-z[a])*(i-a)/(b-a);
            z[0]=low[0];z[n]=low[n];for(int i=1;i<=n;i++)z[i]=Math.Clamp(z[i],z[i-1]-maxStep,z[i-1]+maxStep);
            z[n]=low[n];for(int i=n-1;i>=0;i--)z[i]=Math.Clamp(z[i],z[i+1]-maxStep,z[i+1]+maxStep);
            for(int i=0;i<=n;i++)z[i]=Math.Clamp(z[i],low[i],high[i]);
            double violation=0;int at=0;
            for(int i=1;i<=n;i++){double v=Math.Abs(z[i]-z[i-1])-maxStep;if(v>violation){violation=v;at=i;}}
            for(int i=1;i<n;i++){double v=Math.Abs(z[i-1]-2*z[i]+z[i+1])-limit[i];if(v>violation){violation=v;at=i;}}
            foreach(var (a,b) in runs)for(int i=a+1;i<b;i++){double v=Math.Abs(z[i]-(z[a]+(z[b]-z[a])*(i-a)/(b-a)));if(v>violation){violation=v;at=i;}}
            worst=at;if(violation<=1e-6)return true;
        }
        return false;
    }
    private static double[] Admm(double[] target,double[] low,double[] high,double maxStep,double maxCurvature,double[] limit,bool[] inRun)
    {
        int count=target.Length,n=count-1;
        // Rows scaled so every box is about ±1: heights (metres), grades, grade changes.
        double gradeScale=1/maxStep,changeScale=1/maxCurvature;const double rho=1;
        double ChangeLimit(int i)=>inRun[i]?0:double.IsPositiveInfinity(limit[i])?1e9:limit[i]/maxCurvature;
        // M = I + rho·SᵀS, pentadiagonal: diag, first and second super-diagonals.
        var d0=new double[count];var d1=new double[count];var d2=new double[count];
        for(int i=0;i<count;i++)d0[i]=1+rho;
        double g2=rho*gradeScale*gradeScale;for(int i=0;i<n;i++){d0[i]+=g2;d0[i+1]+=g2;d1[i]-=g2;}
        double c2=rho*changeScale*changeScale;for(int i=1;i<n;i++){d0[i-1]+=c2;d0[i]+=4*c2;d0[i+1]+=c2;d1[i-1]-=2*c2;d1[i]-=2*c2;d2[i-1]+=c2;}
        // Banded Cholesky M = L·Lᵀ with L[i,i]=l0, L[i,i-1]=l1, L[i,i-2]=l2.
        var l0=new double[count];var l1=new double[count];var l2=new double[count];
        for(int i=0;i<count;i++){l2[i]=i>=2?d2[i-2]/l0[i-2]:0;l1[i]=i>=1?(d1[i-1]-l2[i]*l1[i-1])/l0[i-1]:0;l0[i]=Math.Sqrt(Math.Max(1e-12,d0[i]-l1[i]*l1[i]-l2[i]*l2[i]));}
        var z=target.Select((v,i)=>Math.Clamp(v,low[i],high[i])).ToArray();
        var yh=(double[])z.Clone();var uh=new double[count];var yg=new double[n];var ug=new double[n];var yc=new double[count];var uc=new double[count];
        for(int i=0;i<n;i++)yg[i]=Math.Clamp((z[i+1]-z[i])*gradeScale,-1,1);
        for(int i=1;i<n;i++)yc[i]=Math.Clamp((z[i-1]-2*z[i]+z[i+1])*changeScale,-ChangeLimit(i),ChangeLimit(i));
        var rhs=new double[count];var tmp=new double[count];
        for(int iteration=0;iteration<6000;iteration++){
            for(int i=0;i<count;i++)rhs[i]=target[i]+rho*(yh[i]-uh[i]);
            for(int i=0;i<n;i++){double v=rho*gradeScale*(yg[i]-ug[i]);rhs[i]-=v;rhs[i+1]+=v;}
            for(int i=1;i<n;i++){double v=rho*changeScale*(yc[i]-uc[i]);rhs[i-1]+=v;rhs[i]-=2*v;rhs[i+1]+=v;}
            for(int i=0;i<count;i++){double v=rhs[i];if(i>=1)v-=l1[i]*tmp[i-1];if(i>=2)v-=l2[i]*tmp[i-2];tmp[i]=v/l0[i];}
            for(int i=n;i>=0;i--){double v=tmp[i];if(i+1<=n)v-=l1[i+1]*z[i+1];if(i+2<=n)v-=l2[i+2]*z[i+2];z[i]=v/l0[i];}
            double residual=0;
            for(int i=0;i<count;i++){double w=z[i]+uh[i];double y=Math.Clamp(w,low[i],high[i]);uh[i]=w-y;residual=Math.Max(residual,Math.Abs(z[i]-y));yh[i]=y;}
            for(int i=0;i<n;i++){double s=(z[i+1]-z[i])*gradeScale;double w=s+ug[i];double y=Math.Clamp(w,-1,1);ug[i]=w-y;residual=Math.Max(residual,Math.Abs(s-y)*maxStep);yg[i]=y;}
            for(int i=1;i<n;i++){double s=(z[i-1]-2*z[i]+z[i+1])*changeScale;double w=s+uc[i];double y=Math.Clamp(w,-ChangeLimit(i),ChangeLimit(i));uc[i]=w-y;residual=Math.Max(residual,Math.Abs(s-y)/changeScale);yc[i]=y;}
            if(iteration>20&&residual<1e-7)break;
        }
        return z;
    }
    private static GradePlane FitPlane(string id,int? node,Vector2 origin,List<(Vector2 P,float H)> samples,float maxSlope,out bool clamped)
    {
        // Normal equations for h = a + b·dx + c·dz.
        double s00=samples.Count,s01=0,s02=0,s11=0,s12=0,s22=0,r0=0,r1=0,r2=0;
        foreach(var (p,h) in samples){double x=p.X-origin.X,z=p.Y-origin.Y;s01+=x;s02+=z;s11+=x*x;s12+=x*z;s22+=z*z;r0+=h;r1+=h*x;r2+=h*z;}
        double det=s00*(s11*s22-s12*s12)-s01*(s01*s22-s12*s02)+s02*(s01*s12-s11*s02);
        Vector2 gradient=Vector2.Zero;double a=r0/s00;
        if(Math.Abs(det)>1e-6){a=(r0*(s11*s22-s12*s12)-s01*(r1*s22-s12*r2)+s02*(r1*s12-s11*r2))/det;
            double b=(s00*(r1*s22-r2*s12)-r0*(s01*s22-s12*s02)+s02*(s01*r2-r1*s02))/det;
            double c=(s00*(s11*r2-s12*r1)-s01*(s01*r2-r1*s02)+r0*(s01*s12-s11*s02))/det;gradient=new((float)b,(float)c);}
        float fitted=gradient.Length();clamped=fitted>maxSlope+1e-6f;
        if(clamped){gradient=gradient*(maxSlope/fitted);double sum=0;foreach(var (p,h) in samples)sum+=h-gradient.Dot(p-origin);a=sum/samples.Count;}
        return new(id,node,origin,(float)a,gradient,fitted);
    }
    private static RoadGradingSummary Summarize(RoadGrading g,List<GradingNotice> notices)
    {
        double cut=0,fill=0,area=0;float maxCut=0,maxFill=0;Vector2 cutAt=g.PlazaRect.GetCenter(),fillAt=cutAt;
        var worstBySurface=new Dictionary<int,(float Excess,Vector2 At,bool Cut)>();
        void Sample(Vector2 p,int surface){if(!g.TryDesignHeight(p,out float h))return;float d=h-g.GroundHeight(p);area++;
            if(d<0){cut-=d;if(-d>maxCut){maxCut=-d;cutAt=p;}}else{fill+=d;if(d>maxFill){maxFill=d;fillAt=p;}}
            float excess=d<0?-d-g.Options.MaxCut:d-g.Options.MaxFill;if(excess>.05f&&(!worstBySurface.TryGetValue(surface,out var w)||excess>w.Excess))worstBySurface[surface]=(excess,p,d<0);}
        foreach(var s in g.Roads.Geometry.Surfaces){var tri=s.Shape.Triangles;
            for(int i=0;i+2<tri.Count;i+=3){var a=tri[i];var b=tri[i+1];var c=tri[i+2];var min=a.Min(b).Min(c);var max=a.Max(b).Max(c);float den=(b-a).Cross(c-a);if(Math.Abs(den)<1e-9f)continue;
                for(float z=MathF.Floor(min.Y)+.5f;z<=max.Y;z++)for(float x=MathF.Floor(min.X)+.5f;x<=max.X;x++){var p=new Vector2(x,z);float u=(p-a).Cross(c-a)/den,v=(b-a).Cross(p-a)/den;if(u<0||v<0||u+v>1)continue;if(g.InPlaza(p))continue;Sample(p,s.Id);}}}
        var r=g.PlazaRect;for(float z=MathF.Floor(r.Position.Y)+.5f;z<=r.End.Y;z++)for(float x=MathF.Floor(r.Position.X)+.5f;x<=r.End.X;x++){var p=new Vector2(x,z);if(r.HasPoint(p))Sample(p,-1);}
        foreach(var (surface,w) in worstBySurface.OrderBy(k=>k.Key))notices.Add(new(w.Cut?"cut":"fill",surface>=0?g.Roads.Geometry.Surfaces[surface].RoadId:null,w.At,$"{(w.Cut?"挖深":"填高")}超过上限 {w.Excess:0.00} m（截面或平台）"));
        // Short-range relief: RMS residual from a ±10 m local quadratic fit (Savitzky–Golay),
        // so smooth long-range vertical curves count as zero.
        double groundRough=0,designRough=0;int samples=0;
        foreach(var p in g.Profiles){int m=(int)Math.Round(10/p.Step);if(p.Design.Count<2*m+1||m<2)continue;
            double norm=(2*m-1)*(2*m+1)*(2*m+3);var weights=Enumerable.Range(-m,2*m+1).Select(k=>(3*(3*m*m+3*m-1)-15.0*k*k)/norm).ToArray();
            for(int i=m;i<p.Design.Count-m;i++){double fg=0,fd=0;for(int k=-m;k<=m;k++){fg+=weights[k+m]*p.Ground[i+k];fd+=weights[k+m]*p.Design[i+k];}
                groundRough+=(p.Ground[i]-fg)*(p.Ground[i]-fg);designRough+=(p.Design[i]-fd)*(p.Design[i]-fd);samples++;}}
        return new(cut,fill,maxCut,cutAt,maxFill,fillAt,g.Profiles.Count==0?0:g.Profiles.Max(p=>p.MaxGrade),g.Profiles.Count==0?0:g.Profiles.Max(p=>p.MaxGradeChange),
            g.Profiles.Count(p=>p.GradeChangeRelaxed),notices.Count(n=>n.Kind is "junction" or "plaza"),area,(float)Math.Sqrt(groundRough/Math.Max(1,samples)),(float)Math.Sqrt(designRough/Math.Max(1,samples)));
    }
}

public sealed class RoadGradingNode
{
    public RoadGrading? LastResult {get;private set;}
    public SampleNodeState State {get;private set;}
    public long ResultRevision {get;private set;}
    public string? Error {get;private set;}
    public RoadGradingFailure? Failure {get;private set;}
    private long _version;
    public void Invalidate(){_version++;if(State!=SampleNodeState.Running)State=LastResult==null?SampleNodeState.Empty:SampleNodeState.Stale;}
    public bool IsCurrent(RoadNetwork? roads,long roadRevision,ITerrainSurface? terrain,RoadGradingOptions options)=>State==SampleNodeState.Valid&&LastResult is {} d&&
        ReferenceEquals(roads,d.Roads)&&d.RoadRevision==roadRevision&&ReferenceEquals(terrain,d.Terrain)&&d.Options==options;
    public async Task<RoadGrading> RunAsync(RoadNetwork roads,long roadRevision,ITerrainSurface terrain,RoadGradingOptions options,PcgContext? ctx=null)
    {
        if(State==SampleNodeState.Running)throw new InvalidOperationException("道路竖向节点正在运行");State=SampleNodeState.Running;Error=null;Failure=null;long version=++_version;
        try{var result=await RoadGradingGenerator.Generate(roads,roadRevision,terrain,options,ctx??new());if(version!=_version)throw new InvalidOperationException("运行期间输入已失效");
            LastResult=result;ResultRevision++;State=SampleNodeState.Valid;return result;}
        catch(OperationCanceledException){State=SampleNodeState.Cancelled;Error="已取消";throw;}
        catch(Exception ex){State=SampleNodeState.Failed;Error=ex.Message;Failure=ex as RoadGradingFailure;throw;}
    }
}
