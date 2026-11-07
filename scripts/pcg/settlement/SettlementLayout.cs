using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;

public sealed record SettlementOptions(Vector2 Center, float Radius = 300, Vector2? ManualCore = null,
    int EntryCount = 3, float PlazaSize = 40, double MinimumArea = 20000, int Seed = 73129,
    string ManualEntries = "", bool Walls = true, float WallInset = 22, float WallWidth = 4, float WallBuffer = 4, float GateWidth = 22, bool CivicPrecincts = true, bool Regional = true, float CoreRadius = 210, float SuburbLength = 480, float SuburbWidth = 140, int GateCount = 2);
public enum SettlementCellState { Outside, Edge, Restricted, Unknown, Disconnected, Included }
public readonly record struct LayoutCell(SettlementCellState State, EnvironmentRestriction Reasons, float Score, bool Reserved, float Flatness=0, float LocalSpace=0, float Centrality=0, float? VegetationPreference=null,float? GradingRisk=null);
public readonly record struct LayoutEdge(int Cell, Vector2 A, Vector2 B, Vector2 Outward, bool Outer);
public readonly record struct LayoutEntry(string Id, Vector2 Position, float Height, LayoutEdge Edge);
public sealed class SettlementLayout
{
    public RegionalTownPlan? Region {get;internal set;}
    public MedievalLayout? Town { get; internal set; }
    public CityEnvironmentData Environment { get; }
    public string InputNodeId { get; }
    public long InputRevision { get; }
    public SettlementOptions Options { get; }
    public string CoreId => "main_core";
    public string PlazaId => "core_plaza";
    public bool PlazaAllowsRoads => true;
    public bool EntriesRequireConnection => true;
    public Vector2 Core { get; }
    public float CoreHeight { get; }
    public Rect2 Plaza => new(Core - Vector2.One * Options.PlazaSize/2, Vector2.One * Options.PlazaSize);
    public ReadOnlyCollection<LayoutCell> Cells { get; }
    public ReadOnlyCollection<LayoutEdge> Boundary { get; }
    public ReadOnlyCollection<LayoutEntry> Entries { get; }
    public ReadOnlyCollection<LayoutEdge> EntryCandidates { get; }
    public ReadOnlyCollection<string> Warnings { get; }
    public double RoutingLandArea => Cells.Count(c=>c.State == SettlementCellState.Included)*Environment.CellArea;
    public double Area => Region?.DevelopmentArea.Area??RoutingLandArea;
    public double ReservedArea => Cells.Count(c=>c.Reserved)*Environment.CellArea;
    public bool Contains(Vector2 p) => TryCell(p,out var c) && c.State==SettlementCellState.Included;
    public bool TryCell(Vector2 p,out LayoutCell cell)
    {
        cell=default; if(!Environment.Contains(p)) return false;
        int x=Math.Min((int)((p.X-Environment.Bounds.Position.X)/Environment.StepX),Environment.CellsX-1);
        int z=Math.Min((int)((p.Y-Environment.Bounds.Position.Y)/Environment.StepZ),Environment.CellsZ-1);
        cell=Cells[z*Environment.CellsX+x]; return true;
    }
    internal SettlementLayout(CityEnvironmentData env,string node,long revision,SettlementOptions options,Vector2 core,
        LayoutCell[] cells,LayoutEdge[] edges,LayoutEntry[] entries,LayoutEdge[] candidates,string[] warnings)
    {
        Environment=env;InputNodeId=node;InputRevision=revision;Options=options;Core=core;
        if(!env.TryHeightAt(core,out var h)) throw new InvalidOperationException("核心缺少地面高度"); CoreHeight=h;
        Cells=Array.AsReadOnly(cells);Boundary=Array.AsReadOnly(edges);Entries=Array.AsReadOnly(entries);
        EntryCandidates=Array.AsReadOnly(candidates);Warnings=Array.AsReadOnly(warnings);
    }
}
public sealed class SettlementLayoutNode
{
    public const string TypeId="settlement_layout";
    public SettlementLayout? LastResult {get;private set;}
    public SampleNodeState State {get;private set;}
    public long ResultRevision {get;private set;}
    public string? Error {get;private set;}
    private long _generation;
    public void Invalidate(){_generation++; if(State!=SampleNodeState.Running) State=LastResult==null?SampleNodeState.Empty:SampleNodeState.Stale;}
    public bool IsCurrent(CityEnvironmentData? env,long revision,SettlementOptions options) => State==SampleNodeState.Valid && LastResult is {} d && ReferenceEquals(d.Environment,env)&&d.InputRevision==revision&&d.Options==options;
    public async Task<SettlementLayout> RunAsync(CityEnvironmentData env,long revision,SettlementOptions options,PcgContext? context=null,string inputNodeId="environment_sample")
    {
        if(State==SampleNodeState.Running)throw new InvalidOperationException("聚落布局正在运行");
        State=SampleNodeState.Running;Error=null;long generation=++_generation;
        try{
            var result=await SettlementGenerator.Generate(env,inputNodeId,revision,options,context??new());
            if(generation!=_generation)throw new InvalidOperationException("输入已变化，请重新生成");
            LastResult=result;ResultRevision++;State=SampleNodeState.Valid;return result;
        }catch(OperationCanceledException){State=SampleNodeState.Cancelled;Error="已取消";throw;}
        catch(Exception e){State=SampleNodeState.Failed;Error=e.Message;throw;}
    }
}
public static class SettlementGenerator
{
    public static async Task<SettlementLayout> Generate(CityEnvironmentData e,string node,long revision,SettlementOptions o,PcgContext ctx)
    {
        if(o.Regional)return await RegionalTownGenerator.Generate(e,node,revision,o,ctx);
        if(!o.Center.IsFinite()||!float.IsFinite(o.Radius)||o.Radius<=0||!float.IsFinite(o.PlazaSize)||o.PlazaSize<=0||
            !double.IsFinite(o.MinimumArea)||o.MinimumArea<=0||o.EntryCount<1||o.EntryCount>4||o.ManualCore is Vector2 m&&!m.IsFinite())
            throw new ArgumentException("布局参数无效");
        if(!float.IsFinite(o.WallInset)||!float.IsFinite(o.WallWidth)||!float.IsFinite(o.WallBuffer)||!float.IsFinite(o.GateWidth)||o.WallWidth<=0||o.WallBuffer<0||o.WallInset<o.WallWidth/2+o.WallBuffer||o.GateWidth<8)throw new ArgumentException("城墙退让、走廊或城门宽度无效");
        async Task Yield(float p,string message){ctx.Cancellation.ThrowIfCancellationRequested();if(ctx.Yield!=null)await ctx.Yield(new(p,message));ctx.Cancellation.ThrowIfCancellationRequested();}
        int nx=e.CellsX,nz=e.CellsZ,n=nx*nz;
        var cells=new LayoutCell[n];var groups=Enumerable.Repeat(-1,n).ToArray();var sizes=new List<int>();
        Vector2 Center(int k)=>e.VertexPosition(k%nx,k/nx)+new Vector2(e.StepX,e.StepZ)/2;
        bool InCircle(Vector2 p)=>p.DistanceSquaredTo(o.Center)<=o.Radius*o.Radius;
        IEnumerable<int> Neighbors(int k){int x=k%nx,z=k/nx;if(x>0)yield return k-1;if(x<nx-1)yield return k+1;if(z>0)yield return k-nx;if(z<nz-1)yield return k+nx;}
        for(int z=0;z<nz;z++){
            for(int x=0;x<nx;x++){
                int k=z*nx+x;var c=e.Cell(x,z);var a=e.VertexPosition(x,z);var b=e.VertexPosition(x+1,z+1);
                bool full=InCircle(a)&&InCircle(b)&&InCircle(new(a.X,b.Y))&&InCircle(new(b.X,a.Y));
                var state=!full?(InCircle(Center(k))?SettlementCellState.Edge:SettlementCellState.Outside):c.State==BuildCandidateState.Candidate?SettlementCellState.Disconnected:c.State==BuildCandidateState.Blocked?SettlementCellState.Restricted:SettlementCellState.Unknown;
                cells[k]=new(state,c.Reasons,-1,false);
            }
            if(z%16==0)await Yield(.15f*z/nz,"筛选土地");
        }
        for(int k=0;k<n;k++)if(cells[k].State==SettlementCellState.Disconnected&&groups[k]<0){
            int id=sizes.Count;var q=new Queue<int>();q.Enqueue(k);groups[k]=id;int count=0;
            while(q.TryDequeue(out int j)){count++;foreach(int a in Neighbors(j))if(groups[a]<0&&cells[a].State==SettlementCellState.Disconnected){groups[a]=id;q.Enqueue(a);}if(count%4096==0)await Yield(.2f,"分析连通区域");}
            sizes.Add(count);
        }
        var integral=new int[(nx+1)*(nz+1)];
        for(int z=0;z<nz;z++)for(int x=0;x<nx;x++){int t=(z+1)*(nx+1)+x+1;integral[t]=(groups[z*nx+x]>=0?1:0)+integral[t-1]+integral[t-nx-1]-integral[t-nx-2];}
        List<int> PlazaCells(Vector2 p){
            var r=new Rect2(p-Vector2.One*o.PlazaSize/2,Vector2.One*o.PlazaSize);var list=new List<int>();
            if(!e.Contains(r.Position)||!e.Contains(r.End))return list;
            int x0=Math.Max(0,(int)Math.Floor((r.Position.X-e.Bounds.Position.X)/e.StepX));
            int z0=Math.Max(0,(int)Math.Floor((r.Position.Y-e.Bounds.Position.Y)/e.StepZ));
            int x1=Math.Min(nx-1,(int)Math.Floor((r.End.X-e.Bounds.Position.X)/e.StepX));
            int z1=Math.Min(nz-1,(int)Math.Floor((r.End.Y-e.Bounds.Position.Y)/e.StepZ));
            for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)list.Add(z*nx+x);return list;
        }
        int Index(Vector2 p)=>Math.Min((int)((p.Y-e.Bounds.Position.Y)/e.StepZ),nz-1)*nx+Math.Min((int)((p.X-e.Bounds.Position.X)/e.StepX),nx-1);
        bool Feasible(Vector2 p){
            if(!e.Contains(p))return false;int g=groups[Index(p)];if(g<0||sizes[g]*e.CellArea<o.MinimumArea)return false;
            var r=new Rect2(p-Vector2.One*o.PlazaSize/2,Vector2.One*o.PlazaSize);
            if(!e.Contains(r.Position)||!e.Contains(r.End))return false;
            int x0=(int)Math.Floor((r.Position.X-e.Bounds.Position.X)/e.StepX),z0=(int)Math.Floor((r.Position.Y-e.Bounds.Position.Y)/e.StepZ);
            int x1=Math.Min(nx,(int)Math.Floor((r.End.X-e.Bounds.Position.X)/e.StepX)+1),z1=Math.Min(nz,(int)Math.Floor((r.End.Y-e.Bounds.Position.Y)/e.StepZ)+1);
            int count=integral[z1*(nx+1)+x1]-integral[z0*(nx+1)+x1]-integral[z1*(nx+1)+x0]+integral[z0*(nx+1)+x0];
            return count==(x1-x0)*(z1-z0);
        }
        bool vegetation=Enumerable.Range(0,n).Where(k=>groups[k]>=0).All(k=>e.Cell(k%nx,k/nx).Vegetation.HasValue);
        uint Tie(int k){unchecked{uint v=(uint)(k^o.Seed);v^=v>>16;v*=0x7feb352d;v^=v>>15;return v;}}
        Vector2? core=o.ManualCore;float best=-1,bestRisk=float.PositiveInfinity;uint bestTie=uint.MaxValue;
        // A plaza must connect to surrounding streets. A single flat cell can sit
        // between much steeper cells and is insufficient evidence of a good site.
        // This is an upstream siting preference, not a change to 03B's hard limits.
        var slopeSquares=new double[(nx+1)*(nz+1)];
        for(int z=0;z<nz;z++)for(int x=0;x<nx;x++){
            double slope=e.Cell(x,z).MaxSlope??90;
            slopeSquares[(z+1)*(nx+1)+x+1]=slope*slope+slopeSquares[z*(nx+1)+x+1]+slopeSquares[(z+1)*(nx+1)+x]-slopeSquares[z*(nx+1)+x];
        }
        float SiteSlope(int x,int z){
            float radius=o.PlazaSize/2+30;int rx=(int)Math.Ceiling(radius/e.StepX),rz=(int)Math.Ceiling(radius/e.StepZ);
            int ax=Math.Max(0,x-rx),bx=Math.Min(nx,x+rx+1),az=Math.Max(0,z-rz),bz=Math.Min(nz,z+rz+1);
            double square=slopeSquares[bz*(nx+1)+bx]-slopeSquares[az*(nx+1)+bx]-slopeSquares[bz*(nx+1)+ax]+slopeSquares[az*(nx+1)+ax];
            return (float)Math.Sqrt(Math.Max(0,square)/((bx-ax)*(bz-az)));
        }
        for(int k=0;k<n;k++){
            if(k%1024==0)await Yield(.4f,"选择主核心");
            if(groups[k]<0)continue;var p=Center(k);if(!Feasible(p))continue;
            int x=k%nx,z=k/nx,rx=(int)Math.Ceiling(60/e.StepX),rz=(int)Math.Ceiling(60/e.StepZ);
            int x0=Math.Max(0,x-rx),x1=Math.Min(nx,x+rx+1),z0=Math.Max(0,z-rz),z1=Math.Min(nz,z+rz+1);
            float local=(float)(integral[z1*(nx+1)+x1]-integral[z0*(nx+1)+x1]-integral[z1*(nx+1)+x0]+integral[z0*(nx+1)+x0])/((x1-x0)*(z1-z0));
            var ec=e.Cell(x,z);float flatness=1-Math.Clamp(SiteSlope(x,z)/Math.Max(1,e.Options.MaxGroundSlope),0,1);
            float score=.35f*flatness+.35f*local+.20f*(1-p.DistanceTo(o.Center)/o.Radius);
            score=vegetation?score+.1f*(1-ec.Vegetation!.Value):score/.9f;
            cells[k]=cells[k] with{Score=score,Flatness=flatness,LocalSpace=local,Centrality=1-p.DistanceTo(o.Center)/o.Radius,VegetationPreference=vegetation?1-ec.Vegetation!.Value:null};uint tie=Tie(k);
            float risk=PlazaSiting.Risk(e,p,o.PlazaSize);cells[k]=cells[k] with{GradingRisk=risk};
            if(!o.ManualCore.HasValue){
                if(risk<bestRisk-.0001f||Math.Abs(risk-bestRisk)<=.0001f&&(score>best||score==best&&tie<bestTie)){core=p;best=score;bestRisk=risk;bestTie=tie;}}
            if(k%128==0)await Yield(.4f,"选择主核心");
        }
        if(core==null||!Feasible(core.Value))throw new InvalidOperationException("核心或广场不可用：请调整范围、位置、广场尺寸或最小面积");
        int chosen=groups[Index(core.Value)];var plaza=PlazaCells(core.Value).ToHashSet();
        for(int k=0;k<n;k++)if(groups[k]==chosen)cells[k]=cells[k] with{State=SettlementCellState.Included,Reserved=plaza.Contains(k)};
        // Flood complement from map perimeter: internal holes never become entry boundaries.
        var exterior=new bool[n];var flood=new Queue<int>();
        void Seed(int k){if(!exterior[k]&&groups[k]!=chosen){exterior[k]=true;flood.Enqueue(k);}}
        for(int x=0;x<nx;x++){Seed(x);Seed((nz-1)*nx+x);}for(int z=0;z<nz;z++){Seed(z*nx);Seed(z*nx+nx-1);}
        int visited=0;while(flood.TryDequeue(out int k)){foreach(int j in Neighbors(k))Seed(j);if(++visited%4096==0)await Yield(.65f,"整理区域边界");}
        var edges=new List<LayoutEdge>();var candidates=new List<LayoutEdge>();
        foreach(int k in Enumerable.Range(0,n).Where(k=>groups[k]==chosen)){
            int x=k%nx,z=k/nx;var a=e.VertexPosition(x,z);var b=e.VertexPosition(x+1,z+1);
            foreach(var (dx,dz,pa,pb) in new[]{(-1,0,a,new Vector2(a.X,b.Y)),(1,0,new Vector2(b.X,a.Y),b),(0,-1,a,new Vector2(b.X,a.Y)),(0,1,new Vector2(a.X,b.Y),b)}){
                int xx=x+dx,zz=z+dz;bool covered=xx>=0&&xx<nx&&zz>=0&&zz<nz;
                if(covered&&groups[zz*nx+xx]==chosen)continue;
                bool outer=!covered||exterior[zz*nx+xx];var edge=new LayoutEdge(k,pa,pb,new(dx,dz),outer);edges.Add(edge);
                if(!outer)continue;bool ok=true,reached=false;int count=0;
                for(int step=1;step<=3;step++){
                    xx=x+dx*step;zz=z+dz*step;
                    if(xx<0||xx>=nx||zz<0||zz>=nz||e.Cell(xx,zz).State!=BuildCandidateState.Candidate){ok=false;break;}
                    int j=zz*nx+xx;if(groups[j]>=0&&groups[j]!=chosen){ok=false;break;}
                    count++;reached|=!InCircle(Center(j));if(count>=2&&reached)break;
                }
                if(ok&&count>=2&&reached)candidates.Add(edge);
            }
        }
        await Yield(.8f,"选择对外入口");
        var selected=new List<LayoutEdge>();var warnings=new List<string>();
        if(!string.IsNullOrWhiteSpace(o.ManualEntries)){
            foreach(string pair in o.ManualEntries.Split(';',StringSplitOptions.RemoveEmptyEntries)){
                var parts=pair.Split(',');if(parts.Length!=2||!float.TryParse(parts[0],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float x)||!float.TryParse(parts[1],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float z))throw new ArgumentException("入口格式：X,Z;X,Z");
                Vector2 p=new(x,z);var found=candidates.Where(b=>((b.A+b.B)/2).DistanceTo(p)<.05f).ToArray();
                if(found.Length==0||selected.Contains(found[0]))throw new InvalidOperationException("手动入口不是有效且唯一的边界中点，请用地图吸附选择");selected.Add(found[0]);
            }
            if(selected.Count<1||selected.Count>4)throw new ArgumentException("手动入口数量须为 1–4");
        }else{
            foreach(var edge in candidates.OrderBy(b=>e.Cell(b.Cell%nx,b.Cell/nx).MaxSlope).ThenBy(b=>Tie(b.Cell)).ThenBy(b=>b.Outward.X).ThenBy(b=>b.Outward.Y)){
                Vector2 p=(edge.A+edge.B)/2;
                if(selected.Any(b=>{var q=(b.A+b.B)/2;return p.DistanceTo(q)<60||Math.Abs((p-core.Value).AngleTo(q-core.Value))<Mathf.Pi/3;}))continue;
                selected.Add(edge);if(selected.Count==o.EntryCount)break;
            }
            if(selected.Count<o.EntryCount)warnings.Add($"入口不足：期望 {o.EntryCount}，实际 {selected.Count}");
        }
        if(selected.Count==0)throw new InvalidOperationException("没有合法对外入口，请扩大采样范围或调整规划范围");
        if(!e.Contains(o.Center-Vector2.One*o.Radius)||!e.Contains(o.Center+Vector2.One*o.Radius))warnings.Add("规划圆超出采样覆盖，外侧区域未纳入");
        if(!vegetation)warnings.Add("植被数据不完整，选址已停用植被评分");
        var entries=selected.Select((b,i)=>{var p=(b.A+b.B)/2;if(!e.TryHeightAt(p,out float h))throw new InvalidOperationException("入口高度未知");return new LayoutEntry($"E{i+1}",p,h,b);}).ToArray();
        await Yield(.9f,"布置城墙、城门与公共院区");
        var result=new SettlementLayout(e,node,revision,o,core.Value,cells,edges.ToArray(),entries,candidates.ToArray(),warnings.ToArray());
        if(o.Walls||o.CivicPrecincts)result.Town=MedievalPlanner.Build(result);
        await Yield(1,"布局检查完成");return result;
    }
}
