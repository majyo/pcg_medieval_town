using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
namespace PcgCitySandbox.Pcg;

public enum GradingCell : byte { None=0, Band=1, Road=2, Shoulder=3, Lock=4, Conflict=5 }
public sealed record RetainingWall(int Id,Vector2 Center,Vector2 A,Vector2 B,float Step,float Length,int Nodes);
public sealed record GradingEarthwork(double RoadCut,double RoadFill,double SlopeCut,double SlopeFill,double ModifiedArea,float BandReach,int ModifiedNodes,int Tiles,int Conflicts);

// Terrain after the road design is written in: the native surface plus a sparse
// height-difference raster. Inside the road area and plaza the exact design is returned.
public sealed class GradedTerrain:ITerrainSurface
{
    public const int TileSize=64;
    internal sealed class Tile{public readonly float[] Delta=new float[TileSize*TileSize];public readonly byte[] Kind=new byte[TileSize*TileSize];public readonly float[] Z=new float[TileSize*TileSize];}
    public RoadGrading Design {get;}
    internal ParcelRegion? WallLock {get;}
    public ITerrainSurface Source {get;}
    public string Fingerprint {get;private set;}
    public Rect2 Bounds=>Source.Bounds;
    public float Spacing=>Cell;
    public float Cell {get;}
    public Vector2 Origin {get;}
    public ReadOnlyCollection<RetainingWall> RetainingWalls {get;internal set;}=Array.AsReadOnly(Array.Empty<RetainingWall>());
    public GradingEarthwork Earthwork {get;internal set;}=null!;
    internal readonly Dictionary<(int,int),Tile> Tiles=new();
    internal GradedTerrain(RoadGrading design,ITerrainSurface source,float cell){Design=design;Source=source;Cell=cell;Origin=source.Bounds.Position;Fingerprint=$"{source.Fingerprint}/graded/{cell:0.##}";
        if(design.Roads.Settlement.Town is {} town&&town.WallFootprint.Area>0)WallLock=new ParcelRegion(ParcelGeometry.Offset(town.WallFootprint.CopyPaths(),.001f),triangulate:false);
    }
    internal void FingerprintFromContent(){Fingerprint=Source.Fingerprint+"/graded-v4/"+TerrainSurface.Digest(w=>{w.Write(Cell);w.Write(System.Text.Json.JsonSerializer.Serialize(Design.Options));foreach(var e in Design.Roads.Edges){w.Write(e.Id);w.Write(e.Width);foreach(var p in e.Points){w.Write(p.X);w.Write(p.Y);}if(e.PlanarPath is {} path)foreach(var span in path.Spans){w.Write(span.Curved);foreach(var p in new[]{span.A,span.B,span.C}){w.Write(p.X);w.Write(p.Y);}}}foreach(var p in Design.Profiles)foreach(double h in p.Elevations)w.Write(h);foreach(var n in Nodes()){w.Write(n.I);w.Write(n.J);w.Write(n.Delta);w.Write((byte)n.Kind);}});}
    private readonly Dictionary<TemperateMapData,HashSet<int>> _nativeCells=new();
    public HashSet<int> ModifiedNativeCells(TemperateMapData map){
        if(_nativeCells.TryGetValue(map,out var cached))return cached;var cells=new HashSet<int>();int segments=map.Side-1;
        foreach(var n in Nodes()){if(Math.Abs(n.Delta)<1e-6f&&n.Kind!=GradingCell.Road)continue;var p=NodePosition(n.I,n.J);
            int X(float v)=>Math.Clamp((int)Math.Floor((v+map.Half)/map.Spacing),0,segments-1);
            for(int z=X(p.Y-Cell-.81f);z<=X(p.Y+Cell+.81f);z++)for(int x=X(p.X-Cell-.81f);x<=X(p.X+Cell+.81f);x++)cells.Add(z*segments+x);}
        _nativeCells[map]=cells;return cells;
    }
    public bool Contains(Vector2 p)=>Source.Contains(p);
    public float WaterClearance(Vector2 p)=>Source.WaterClearance(p);
    public bool IsLocked(Vector2 p){
        if(Source.WaterClearance(p)<Design.Options.BankBuffer)return true;
        // WallFootprint already excludes the gate passages. Its 1 mm conservative
        // query margin absorbs float round-off at clipped wall vertices.
        return WallLock!=null&&WallLock.Bounds.HasPoint(p)&&WallLock.Contains(p);
    }
    public float HeightAt(Vector2 p)
    {
        if(IsLocked(p))return Source.HeightAt(p);
        float raster=RenderHeightAt(p);if(KindAt(p)==GradingCell.None)return Design.TryDesignHeight(p,out float exact)?exact:raster;
        float reach=Cell*1.5f;
        if(!Design.TryDesignHeightNear(p,reach,out float design,out float distance))return raster;
        float t=Math.Clamp((distance-.01f)/(reach-.01f),0,1);t=t*t*(3-2*t);
        return Mathf.Lerp(design,raster,t);
    }
    // Rendered terrain: native surface plus the bilinear difference raster.
    public float RenderHeightAt(Vector2 p)=>Source.HeightAt(p)+DeltaAt(p);
    // Display/collision bed under the separate road mesh. Query heights for planning
    // and placement remain on the finished surface.
    public float BedHeightAt(Vector2 p)
    {
        if(IsLocked(p))return Source.HeightAt(p);
        if(Design.TryDesignHeight(p,out float h))return h-.03f;
        if(Design.TryDesignHeightNear(p,.8f,out h))return Math.Min(HeightAt(p),h-.03f);
        return HeightAt(p);
    }
    public Vector2 NodePosition(int i,int j)=>Origin+new Vector2(i,j)*Cell;
    public float DeltaAt(Vector2 p)
    {
        if(IsLocked(p))return 0;
        float gx=(p.X-Origin.X)/Cell,gz=(p.Y-Origin.Y)/Cell;int i=(int)MathF.Floor(gx),j=(int)MathF.Floor(gz);float u=gx-i,v=gz-j;
        return (1-v)*((1-u)*NodeDelta(i,j)+u*NodeDelta(i+1,j))+v*((1-u)*NodeDelta(i,j+1)+u*NodeDelta(i+1,j+1));
    }
    internal Tile? TileOf(int i,int j,out int index){var key=(FloorDiv(i),FloorDiv(j));index=(i-key.Item1*TileSize)+(j-key.Item2*TileSize)*TileSize;return Tiles.GetValueOrDefault(key);}
    internal static int FloorDiv(int v)=>v>=0?v/TileSize:-((-v+TileSize-1)/TileSize);
    // Road-bed weight (road 1, shoulder 0.5), bilinear between nodes, for materials.
    public float RoadMaskAt(Vector2 p)
    {
        if(IsLocked(p))return 0;
        float gx=(p.X-Origin.X)/Cell,gz=(p.Y-Origin.Y)/Cell;int i=(int)MathF.Floor(gx),j=(int)MathF.Floor(gz);float u=gx-i,v=gz-j;
        float W(int a,int b)=>NodeKind(a,b) switch{GradingCell.Road=>1,GradingCell.Shoulder=>.5f,_=>0};
        return (1-v)*((1-u)*W(i,j)+u*W(i+1,j))+v*((1-u)*W(i,j+1)+u*W(i+1,j+1));
    }
    public float NodeDelta(int i,int j)=>TileOf(i,j,out int k) is {} t?t.Delta[k]:0;
    public GradingCell NodeKind(int i,int j)=>TileOf(i,j,out int k) is {} t?(GradingCell)t.Kind[k]:GradingCell.None;
    public GradingCell KindAt(Vector2 p)=>NodeKind((int)MathF.Round((p.X-Origin.X)/Cell),(int)MathF.Round((p.Y-Origin.Y)/Cell));
    // Every stored node with its index, difference and role.
    public IEnumerable<(int I,int J,float Delta,GradingCell Kind)> Nodes()
    {
        foreach(var ((ti,tj),tile) in Tiles.OrderBy(t=>t.Key))for(int k=0;k<TileSize*TileSize;k++)if(tile.Kind[k]!=0)yield return(ti*TileSize+k%TileSize,tj*TileSize+k/TileSize,tile.Delta[k],(GradingCell)tile.Kind[k]);
    }
}

public static class GradedTerrainBuilder
{
    public static async Task<GradedTerrain> Build(RoadGrading g,PcgContext ctx)
    {
        var o=g.Options;var source=g.Terrain;float cell=o.Resolution;float kc=1/o.CutRatio,kf=1/o.FillRatio;var token=ctx.Cancellation;
        var result=new GradedTerrain(g,source,cell);var tiles=result.Tiles;
        GradedTerrain.Tile Ensure(int i,int j,out int k){var key=(GradedTerrain.FloorDiv(i),GradedTerrain.FloorDiv(j));k=(i-key.Item1*GradedTerrain.TileSize)+(j-key.Item2*GradedTerrain.TileSize)*GradedTerrain.TileSize;
            if(!tiles.TryGetValue(key,out var t))tiles[key]=t=new();return t;}
        (int,int) Node(Vector2 p)=>((int)MathF.Round((p.X-result.Origin.X)/cell),(int)MathF.Round((p.Y-result.Origin.Y)/cell));
        var bounds=source.Bounds;int maxI=(int)MathF.Round(bounds.Size.X/cell),maxJ=(int)MathF.Round(bounds.Size.Y/cell);
        async Task Step(float f,string m){token.ThrowIfCancellationRequested();if(ctx.Yield!=null)await ctx.Yield(new(f,m));}

        // 1. Road and plaza nodes take the design height.
        var roadNodes=new HashSet<(int,int)>();
        void Cover(Vector2 a,Vector2 b,Vector2 c){var min=a.Min(b).Min(c);var max=a.Max(b).Max(c);float den=(b-a).Cross(c-a);if(Math.Abs(den)<1e-9f)return;
            var (i0,j0)=Node(min);var (i1,j1)=Node(max);
            for(int j=j0-1;j<=j1+1;j++)for(int i=i0-1;i<=i1+1;i++){var p=result.NodePosition(i,j);float u=(p-a).Cross(c-a)/den,v=(b-a).Cross(p-a)/den;if(u>=-1e-5f&&v>=-1e-5f&&u+v<=1+1e-5f)roadNodes.Add((i,j));}}
        foreach(var s in g.Roads.Geometry.Surfaces){var tri=s.Shape.Triangles;for(int k=0;k+2<tri.Count;k+=3)Cover(tri[k],tri[k+1],tri[k+2]);}
        var plaza=g.PlazaRect;Cover(plaza.Position,new(plaza.End.X,plaza.Position.Y),plaza.End);Cover(plaza.Position,plaza.End,new(plaza.Position.X,plaza.End.Y));
        var roadList=roadNodes.Where(n=>n.Item1>=0&&n.Item2>=0&&n.Item1<=maxI&&n.Item2<=maxJ).OrderBy(n=>n).ToArray();var roadZ=new float[roadList.Length];var roadOk=new bool[roadList.Length];
        Parallel.For(0,roadList.Length,new ParallelOptions{CancellationToken=token},k=>{roadOk[k]=g.TryDesignHeightNear(result.NodePosition(roadList[k].Item1,roadList[k].Item2),cell,out roadZ[k]);});
        for(int k=0;k<roadList.Length;k++)if(roadOk[k]){var t=Ensure(roadList[k].Item1,roadList[k].Item2,out int idx);t.Kind[idx]=(byte)GradingCell.Road;t.Z[idx]=roadZ[k];}
        await Step(.92f,"地形修整：路肩");

        // 2. Shoulder: nodes just outside the road keep the road-edge height, so the
        // terrain meets the road level instead of interpolating across its edge.
        float shoulder=cell*1.5f;int reach=(int)Math.Ceiling(shoulder/cell);var shoulderSet=new HashSet<(int,int)>();
        foreach(var (i,j) in roadList)for(int dj=-reach;dj<=reach;dj++)for(int di=-reach;di<=reach;di++){if(di*di+dj*dj>reach*reach)continue;var n=(i+di,j+dj);
            if(n.Item1<0||n.Item2<0||n.Item1>maxI||n.Item2>maxJ||roadNodes.Contains(n))continue;shoulderSet.Add(n);}
        var shoulderList=shoulderSet.OrderBy(n=>n).ToArray();var shoulderZ=new float[shoulderList.Length];var shoulderOk=new bool[shoulderList.Length];
        Parallel.For(0,shoulderList.Length,new ParallelOptions{CancellationToken=token},k=>{shoulderOk[k]=g.TryDesignHeightNear(result.NodePosition(shoulderList[k].Item1,shoulderList[k].Item2),shoulder,out shoulderZ[k]);});
        for(int k=0;k<shoulderList.Length;k++)if(shoulderOk[k]){var t=Ensure(shoulderList[k].Item1,shoulderList[k].Item2,out int idx);t.Kind[idx]=(byte)GradingCell.Shoulder;t.Z[idx]=shoulderZ[k];}
        // Roads leaving the map ramp back to the native terrain over the last metres, so
        // the map edge (and its skirt) keeps the original heights.
        const float edgeRamp=3;
        foreach(var ((ti,tj),t) in tiles)for(int k=0;k<t.Kind.Length;k++){if(t.Kind[k]==0)continue;int i=ti*GradedTerrain.TileSize+k%GradedTerrain.TileSize,j=tj*GradedTerrain.TileSize+k/GradedTerrain.TileSize;
            float edge=Math.Min(Math.Min(i,maxI-i),Math.Min(j,maxJ-j))*cell-cell;if(edge>=edgeRamp)continue;var p=result.NodePosition(i,j);t.Z[k]=Mathf.Lerp(source.HeightAt(p),t.Z[k],Math.Clamp(edge/edgeRamp,0,1));}

        // 3. Reach of the slopes from the largest difference at a fixed node.
        float up=0,down=0;
        foreach(var ((ti,tj),t) in tiles)for(int k=0;k<t.Kind.Length;k++){if(t.Kind[k]==0)continue;var p=result.NodePosition(ti*GradedTerrain.TileSize+k%GradedTerrain.TileSize,tj*GradedTerrain.TileSize+k/GradedTerrain.TileSize);float d=t.Z[k]-source.HeightAt(p);if(d>up)up=d;if(-d>down)down=-d;}
        float reachMeters=Math.Max(up/kf,down/kc)+cell;int window=Math.Max(1,(int)Math.Ceiling(reachMeters/cell));
        foreach(var key in tiles.Keys.ToArray())for(int dj=-1;dj<=1;dj++)for(int di=-1;di<=1;di++){var n=(key.Item1+di,key.Item2+dj);if(!tiles.ContainsKey(n))tiles[n]=new();}
        await Step(.94f,"地形修整：锁定区");

        // 4. Locks: water with a bank buffer, the wall body outside gate passages, and the map border.
        var wall=result.WallLock;
        var keys=tiles.Keys.OrderBy(k=>k).ToArray();if(keys.Length*(long)GradedTerrain.TileSize*GradedTerrain.TileSize>12000000||window>64)throw new InvalidOperationException("修整栅格或边坡搜索超过本次预算，请降低精度或边坡宽度");
        var lockViolations=new System.Collections.Concurrent.ConcurrentBag<Vector2>();
        Parallel.ForEach(keys,new ParallelOptions{CancellationToken=token},key=>{var t=tiles[key];
            var center=result.NodePosition(key.Item1*GradedTerrain.TileSize+GradedTerrain.TileSize/2,key.Item2*GradedTerrain.TileSize+GradedTerrain.TileSize/2);float half=GradedTerrain.TileSize*cell*.75f;
            bool nearWater=source.WaterClearance(center)<half+o.BankBuffer+5;bool nearWall=wall!=null&&wall.Bounds.Grow(half).HasPoint(center);
            for(int k=0;k<t.Kind.Length;k++){int i=key.Item1*GradedTerrain.TileSize+k%GradedTerrain.TileSize,j=key.Item2*GradedTerrain.TileSize+k/GradedTerrain.TileSize;var p=result.NodePosition(i,j);
                bool locked=i<=1||j<=1||i>=maxI-1||j>=maxJ-1;
                if(!locked&&nearWater)locked=source.WaterClearance(p)<o.BankBuffer;
                if(!locked&&nearWall&&wall!.Bounds.Grow(.001f).HasPoint(p)&&wall.Contains(p))locked=true;
                if(locked){if(t.Kind[k]==(byte)GradingCell.Road&&Math.Abs(t.Z[k]-source.HeightAt(p))>.01f)lockViolations.Add(p);t.Kind[k]=(byte)GradingCell.Lock;t.Z[k]=source.HeightAt(p);}}});
        if(!lockViolations.IsEmpty){var p=lockViolations.OrderBy(p=>p.X).ThenBy(p=>p.Y).First();throw new RoadGradingFailure(p,g.SurfaceAt(p)?.RoadId,"道路设计侵入水域或城墙锁定区，请调整 03 路线");}
        await Step(.96f,"地形修整：边坡");

        // 5. Slope-limited envelope. For every free node within reach of a road source:
        // U = min(z + cut·d), L = max(z − fill·d) over road, shoulder and lock sources;
        // the graded height is the original height clamped to [L, U].
        var offsets=new List<(int Di,int Dj,float D)>();for(int dj=-window;dj<=window;dj++)for(int di=-window;di<=window;di++){float d=MathF.Sqrt(di*di+dj*dj)*cell;if(d<=reachMeters&&(di!=0||dj!=0))offsets.Add((di,dj,d));}
        var ordered=offsets.OrderBy(x=>x.D).ToArray();int conflicts=0;
        var conflictNodes=new System.Collections.Concurrent.ConcurrentDictionary<(int,int),float>();
        Parallel.ForEach(keys,new ParallelOptions{CancellationToken=token},key=>{
            int size=GradedTerrain.TileSize,haloSize=size+2*window;var kind=new byte[haloSize*haloSize];var z=new float[haloSize*haloSize];bool anyRoad=false;
            for(int dj=-1;dj<=1;dj++)for(int di=-1;di<=1;di++){if(!tiles.TryGetValue((key.Item1+di,key.Item2+dj),out var n))continue;
                for(int k=0;k<size*size;k++){int x=k%size+di*size+window,y=k/size+dj*size+window;if(x<0||y<0||x>=haloSize||y>=haloSize)continue;kind[x+y*haloSize]=n.Kind[k];z[x+y*haloSize]=n.Z[k];if(n.Kind[k] is (byte)GradingCell.Road or (byte)GradingCell.Shoulder)anyRoad=true;}}
            if(!anyRoad)return;var t=tiles[key];
            for(int k=0;k<size*size;k++){
                int x=k%size+window,y=k/size+window;byte own=kind[x+y*haloSize];
                int i=key.Item1*size+k%size,j=key.Item2*size+k/size;var p=result.NodePosition(i,j);
                if(own is (byte)GradingCell.Road or (byte)GradingCell.Shoulder){t.Delta[k]=t.Z[k]-source.HeightAt(p);continue;}
                if(own==(byte)GradingCell.Lock){t.Delta[k]=0;continue;}
                float upper=float.PositiveInfinity,lower=float.NegativeInfinity;bool near=false;
                foreach(var (di,dj,d) in ordered){byte s=kind[x+di+(y+dj)*haloSize];if(s<(byte)GradingCell.Road||s>(byte)GradingCell.Lock)continue;if(s is (byte)GradingCell.Road or (byte)GradingCell.Shoulder)near=true;float q=z[x+di+(y+dj)*haloSize];upper=Math.Min(upper,q+kc*d);lower=Math.Max(lower,q-kf*d);}
                if(!near||i<0||j<0||i>maxI||j>maxJ)continue;
                float original=source.HeightAt(p);float h;
                if(lower>upper+1e-4f){h=(lower+upper)/2;t.Kind[k]=(byte)GradingCell.Conflict;conflictNodes[(i,j)]=lower-upper;System.Threading.Interlocked.Increment(ref conflicts);}
                else{h=Math.Clamp(original,lower,upper);t.Kind[k]=(byte)GradingCell.Band;}
                t.Delta[k]=h-original;}});
        foreach(var key in tiles.Where(t=>t.Value.Kind.All(k=>k==0)).Select(t=>t.Key).ToArray())tiles.Remove(key);

        // 6. Retaining walls: connected conflict nodes.
        var walls=new List<RetainingWall>();var seen=new HashSet<(int,int)>();
        foreach(var start in conflictNodes.Keys.OrderBy(k=>k)){if(!seen.Add(start))continue;var group=new List<(int,int)>{start};var queue=new Queue<(int,int)>();queue.Enqueue(start);
            while(queue.TryDequeue(out var n))for(int dj=-1;dj<=1;dj++)for(int di=-1;di<=1;di++){var m=(n.Item1+di,n.Item2+dj);if(conflictNodes.ContainsKey(m)&&seen.Add(m)){group.Add(m);queue.Enqueue(m);}}
            var points=group.Select(n=>result.NodePosition(n.Item1,n.Item2)).ToArray();var a=points[0];var b=points.OrderByDescending(p=>p.DistanceSquaredTo(a)).First();a=points.OrderByDescending(p=>p.DistanceSquaredTo(b)).First();
            walls.Add(new(walls.Count,points.Aggregate(Vector2.Zero,(s,p)=>s+p)/points.Length,a,b,group.Max(n=>conflictNodes[n]),a.DistanceTo(b)+cell,group.Count));}
        result.RetainingWalls=Array.AsReadOnly(walls.ToArray());

        double roadCut=0,roadFill=0,slopeCut=0,slopeFill=0,modified=0;int modifiedNodes=0;float area=cell*cell;
        foreach(var (_,_,delta,kind) in result.Nodes()){if(Math.Abs(delta)>.01f){modified+=area;modifiedNodes++;}
            bool road=kind is GradingCell.Road;if(delta<0){if(road)roadCut-=delta*area;else slopeCut-=delta*area;}else{if(road)roadFill+=delta*area;else slopeFill+=delta*area;}}
        result.FingerprintFromContent();
        result.Earthwork=new(roadCut,roadFill,slopeCut,slopeFill,modified,reachMeters,modifiedNodes,tiles.Count,conflicts);
        return result;
    }
}
