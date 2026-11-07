using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;

public sealed record RoadOptions(float MainWidth=6,float StreetWidth=3,float Spacing=80,float? Angle=null,
    float Jitter=.15f,float MaxGrade=10,float MaxSurfaceSlope=12,int Seed=73129,int SearchBudget=60000,int CandidateBudget=600,bool PlannedTown=false,float CornerRadius=3,float JunctionFlare=2);
public sealed record RoadVertex(int Id,Vector2 Position,float Height,string Role);
public enum StreetRole { GateStreet,MarketStreet,InstitutionStreet,LocalStreet,BackLane }
public sealed record RoadEdge(int Id,int From,int To,ReadOnlyCollection<Vector2> Points,bool Main,float Width,float Length,float MaxGrade,StreetRole Role=StreetRole.LocalStreet,RoadPlanarPath? PlanarPath=null);
public sealed record RoadIssue(Vector2 From,Vector2 To,string Reason);
public sealed class RoadNetwork
{
    public CityEnvironmentData Environment {get;}
    public SettlementLayout Settlement {get;}
    public long EnvironmentRevision {get;}
    public long SettlementRevision {get;}
    public string EnvironmentNodeId => Settlement.InputNodeId;
    public string SettlementNodeId => "settlement_layout";
    public RoadOptions Options {get;}
    public ReadOnlyCollection<RoadVertex> Nodes {get;}
    public ReadOnlyCollection<RoadEdge> Edges {get;}
    public ReadOnlyDictionary<string,int> Anchors {get;}
    public ReadOnlyCollection<ReadOnlyCollection<Vector2>> Footprint {get;}
    public RoadGeometry Geometry {get;}
    public ReadOnlyCollection<RoadIssue> Issues {get;}
    public ReadOnlyCollection<LandscapeParcel> Landscape {get;}
    public int CycleCount=>Math.Max(0,Edges.Count-Nodes.Count+1);
    public double MainLength=>Edges.Where(e=>e.Main).Sum(e=>(double)e.Length);
    public double StreetLength=>Edges.Where(e=>!e.Main).Sum(e=>(double)e.Length);
    internal RoadNetwork(CityEnvironmentData env,SettlementLayout layout,long envRevision,long layoutRevision,RoadOptions options,
        RoadVertex[] vertices,RoadEdge[] edges,Dictionary<string,int> anchors,List<Vector2[]> polygons,List<RoadIssue> issues,RoadGeometry? geometry=null)
    {Environment=env;Settlement=layout;EnvironmentRevision=envRevision;SettlementRevision=layoutRevision;Options=options;
        Nodes=Array.AsReadOnly(vertices);Edges=Array.AsReadOnly(edges);Anchors=new(new Dictionary<string,int>(anchors));
        Geometry=geometry??RoadGeometry.FromLegacy(polygons,Edges);
        Footprint=Array.AsReadOnly(Enumerable.Range(0,Geometry.Area.Triangles.Count/3).Select(i=>Array.AsReadOnly(new[]{Geometry.Area.Triangles[i*3],Geometry.Area.Triangles[i*3+1],Geometry.Area.Triangles[i*3+2]})).ToArray());Issues=Array.AsReadOnly(issues.ToArray());
        var landscape=new List<LandscapeParcel>();if(layout.Region!=null){var roadArea=Geometry.Area.CopyPaths();foreach(var field in layout.Region.Landscape){var remaining=ParcelGeometry.Difference(field.Shape.CopyPaths(),roadArea);if(ParcelGeometry.Area(remaining)>1)landscape.Add(field with{Shape=new ParcelRegion(remaining),CultivationLines=AgriculturalLandscapePlanner.ClipLines(field.CultivationLines??Array.AsReadOnly(Array.Empty<ReadOnlyCollection<Vector2>>()),remaining)});}}Landscape=Array.AsReadOnly(landscape.ToArray());}
    public bool Contains(Vector2 p)=>p.IsFinite()&&Geometry.Area.Contains(p);
    public int[] PathTo(string anchor){
        if(!Anchors.TryGetValue(anchor,out int start))return Array.Empty<int>();int goal=Anchors[Settlement.CoreId];
        var previous=new Dictionary<int,(int Node,int Edge)>();var q=new Queue<int>();q.Enqueue(start);previous[start]=(-1,-1);
        while(q.TryDequeue(out int v)){if(v==goal)break;foreach(var e in Edges.Where(e=>e.From==v||e.To==v)){int next=e.From==v?e.To:e.From;if(previous.TryAdd(next,(v,e.Id)))q.Enqueue(next);}}
        if(!previous.ContainsKey(goal))return Array.Empty<int>();var result=new List<int>();for(int v=goal;v!=start;v=previous[v].Node)result.Add(previous[v].Edge);return result.ToArray();
    }
}
public sealed class RoadNode
{
    public RoadNetwork? LastResult {get;private set;}
    public SampleNodeState State {get;private set;}
    public long ResultRevision {get;private set;}
    public string? Error {get;private set;}
    public ReadOnlyCollection<RoadIssue> FailureIssues {get;private set;}=Array.AsReadOnly(Array.Empty<RoadIssue>());
    private long _version;
    public void Invalidate(){_version++;if(State!=SampleNodeState.Running)State=LastResult==null?SampleNodeState.Empty:SampleNodeState.Stale;}
    public bool IsCurrent(CityEnvironmentData? env,SettlementLayout? layout,long er,long lr,RoadOptions options)=>State==SampleNodeState.Valid&&LastResult is {} d&&ReferenceEquals(env,d.Environment)&&ReferenceEquals(layout,d.Settlement)&&d.EnvironmentRevision==er&&d.SettlementRevision==lr&&d.Options==options;
    public async Task<RoadNetwork> RunAsync(CityEnvironmentData env,SettlementLayout layout,long er,long lr,RoadOptions options,PcgContext? ctx=null){
        if(State==SampleNodeState.Running)throw new InvalidOperationException("道路节点正在运行");State=SampleNodeState.Running;Error=null;FailureIssues=Array.AsReadOnly(Array.Empty<RoadIssue>());long version=++_version;
        try{var result=await RoadGenerator.Generate(env,layout,er,lr,options,ctx??new());if(version!=_version)throw new InvalidOperationException("运行期间输入已失效");LastResult=result;ResultRevision++;State=SampleNodeState.Valid;return result;}
        catch(OperationCanceledException){State=SampleNodeState.Cancelled;Error="已取消";throw;}
        catch(Exception ex){State=SampleNodeState.Failed;Error=ex.Message;if(ex is RoadFailure f)FailureIssues=Array.AsReadOnly(new[]{f.Issue});throw;}
    }
}
public sealed class RoadFailure:InvalidOperationException {public RoadIssue Issue {get;} public RoadFailure(RoadIssue issue):base(issue.Reason){Issue=issue;}}

// All footprint checks use convex polygons against the immutable, included cell union.
public sealed class RoadSpace
{
    private readonly CityEnvironmentData _env;private readonly SettlementLayout _layout;private readonly RoadOptions _options;
    public RoadSpace(CityEnvironmentData env,SettlementLayout layout,RoadOptions options){_env=env;_layout=layout;_options=options;}
    public static Vector2[] Strip(Vector2 a,Vector2 b,float width){var normal=(b-a).Normalized().Orthogonal()*width/2;return new[]{a+normal,b+normal,b-normal,a-normal};}
    public static Vector2[] Pad(Vector2 p,float width)=>Enumerable.Range(0,8).Select(i=>p+Vector2.FromAngle(i*Mathf.Tau/8)*(width/2)).ToArray();
    public static bool Overlap(Vector2[] a,Vector2[] b){
        foreach(var polygon in new[]{a,b})for(int i=0;i<polygon.Length;i++){var axis=(polygon[(i+1)%polygon.Length]-polygon[i]).Orthogonal();float amin=float.PositiveInfinity,amax=float.NegativeInfinity,bmin=amin,bmax=amax;
            foreach(var p in a){float d=p.Dot(axis);amin=Math.Min(amin,d);amax=Math.Max(amax,d);}foreach(var p in b){float d=p.Dot(axis);bmin=Math.Min(bmin,d);bmax=Math.Max(bmax,d);}if(Math.Min(amax,bmax)-Math.Max(amin,bmin)<=.0001f)return false;}
        return true;
    }
    public bool PolygonValid(Vector2[] polygon){
        var min=polygon.Aggregate(new Vector2(float.PositiveInfinity,float.PositiveInfinity),(a,b)=>a.Min(b));var max=polygon.Aggregate(new Vector2(float.NegativeInfinity,float.NegativeInfinity),(a,b)=>a.Max(b));
        if(!_env.Contains(min)||!_env.Contains(max))return false;
        if(_layout.Town is {} town&&town.RoadObstacles.Bounds.Intersects(new Rect2(min,max-min),true)&&ParcelGeometry.Area(ParcelGeometry.Intersection(new Paths64{ParcelGeometry.Path(polygon)},town.RoadObstacles.CopyPaths()))>.002)return false;
        int x0=Math.Max(0,(int)Math.Floor((min.X-_env.Bounds.Position.X)/_env.StepX)),x1=Math.Min(_env.CellsX-1,(int)Math.Floor((max.X-_env.Bounds.Position.X)/_env.StepX));
        int z0=Math.Max(0,(int)Math.Floor((min.Y-_env.Bounds.Position.Y)/_env.StepZ)),z1=Math.Min(_env.CellsZ-1,(int)Math.Floor((max.Y-_env.Bounds.Position.Y)/_env.StepZ));
        for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++){
            var c=_env.Cell(x,z);if(_layout.Cells[z*_env.CellsX+x].State==SettlementCellState.Included&&c.State==BuildCandidateState.Candidate&&c.MaxSlope<=_options.MaxSurfaceSlope)continue;
            var a=_env.VertexPosition(x,z);var b=_env.VertexPosition(x+1,z+1);if(Overlap(polygon,new[]{a,new Vector2(b.X,a.Y),b,new Vector2(a.X,b.Y)}))return false;
        }return true;
    }
    public float Grade(Vector2 a,Vector2 b){
        float length=a.DistanceTo(b);if(length<.001f)return 0;var cuts=new SortedSet<float>{0,1};Vector2 delta=b-a;
        void Cut(float t){if(t>0&&t<1)cuts.Add(t);}
        if(Math.Abs(delta.X)>1e-6)for(int x=0;x<=_env.CellsX;x++)Cut((_env.Bounds.Position.X+x*_env.StepX-a.X)/delta.X);
        if(Math.Abs(delta.Y)>1e-6)for(int z=0;z<=_env.CellsZ;z++)Cut((_env.Bounds.Position.Y+z*_env.StepZ-a.Y)/delta.Y);
        // Each sampled cell has the diagonal u+v=1; split the linear height profile there too.
        var gridCuts=cuts.ToArray();for(int i=1;i<gridCuts.Length;i++){
            float mid=(gridCuts[i]+gridCuts[i-1])/2;Vector2 p=a+delta*mid;int x=Math.Clamp((int)((p.X-_env.Bounds.Position.X)/_env.StepX),0,_env.CellsX-1),z=Math.Clamp((int)((p.Y-_env.Bounds.Position.Y)/_env.StepZ),0,_env.CellsZ-1);
            float denominator=delta.X/_env.StepX+delta.Y/_env.StepZ;if(Math.Abs(denominator)>1e-6){float t=(1-(a.X-_env.Bounds.Position.X)/_env.StepX+x-(a.Y-_env.Bounds.Position.Y)/_env.StepZ+z)/denominator;if(t>gridCuts[i-1]&&t<gridCuts[i])Cut(t);}
        }
        float grade=0,prev=0;_env.TryHeightAt(a,out float ph);foreach(float t in cuts.Skip(1)){if(!_env.TryHeightAt(a+delta*t,out float h))return 90;grade=Math.Max(grade,Mathf.RadToDeg(Mathf.Atan(Math.Abs(h-ph)/Math.Max(.00001f,length*(t-prev)))));prev=t;ph=h;}return grade;
    }
    public bool SegmentValid(Vector2 a,Vector2 b,float width)=>a.DistanceSquaredTo(b)>.0001f&&PolygonValid(Strip(a,b,width))&&Grade(a,b)<=_options.MaxGrade+.0001f;
}

