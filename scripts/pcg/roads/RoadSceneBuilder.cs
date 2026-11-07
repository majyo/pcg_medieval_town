using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PcgCitySandbox.Pcg;

public sealed record RoadFaceSource(int SurfaceId,int RoadId,int? NodeId,RoadPaving Paving);
public sealed class RoadChunk
{
    public Vector2I Key {get;}
    public List<Vector3> Positions {get;}=new();
    public List<Vector2> Uv {get;}=new();
    public List<float> EdgeDistances {get;}=new();
    public List<RoadFaceSource> Faces {get;}=new();
    public RoadChunk(Vector2I key){Key=key;}
    public ArrayMesh Mesh(Vector3 origin=default)
    {
        var arrays=new Godot.Collections.Array();arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        var shared=new Dictionary<Vector3,Vector3>();
        for(int i=0;i<Positions.Count;i+=3){var n=(Positions[i+2]-Positions[i]).Cross(Positions[i+1]-Positions[i]);for(int j=0;j<3;j++)shared[Positions[i+j]]=shared.GetValueOrDefault(Positions[i+j])+n;}
        var unique=new Dictionary<(Vector3,Vector2,int),int>();var p=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var types=new List<Vector2>();var indices=new int[Positions.Count];
        for(int i=0;i<Positions.Count;i++){int type=(int)Faces[i/3].Paving;var key=(Positions[i],Uv[i],type);if(!unique.TryGetValue(key,out int at)){unique[key]=at=p.Count;p.Add(Positions[i]-origin);normals.Add(shared[Positions[i]].Normalized());uv.Add(Uv[i]);types.Add(new(type,EdgeDistances.Count>i?EdgeDistances[i]:1));}indices[i]=at;}
        arrays[(int)Godot.Mesh.ArrayType.Vertex]=p.ToArray();arrays[(int)Godot.Mesh.ArrayType.Normal]=normals.ToArray();
        arrays[(int)Godot.Mesh.ArrayType.TexUV]=uv.ToArray();arrays[(int)Godot.Mesh.ArrayType.TexUV2]=types.ToArray();arrays[(int)Godot.Mesh.ArrayType.Index]=indices;
        var mesh=new ArrayMesh();if(Positions.Count>0)mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles,arrays);return mesh;
    }
}
public sealed class RoadSceneData
{
    public RoadGrading Grading {get;}
    public RoadConstructionOptions Options {get;}
    public IReadOnlyList<RoadChunk> Near {get;}
    public IReadOnlyList<RoadChunk> Far {get;}
    public ParcelRegion Area {get;}
    public int Triangles=>Near.Sum(c=>c.Faces.Count);
    internal RoadSceneData(RoadGrading grading,RoadConstructionOptions options,List<RoadChunk> near,List<RoadChunk> far,ParcelRegion area)
    {Grading=grading;Options=options;Near=near.AsReadOnly();Far=far.AsReadOnly();Area=area;}
}

public static class RoadSceneBuilder
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoadGrading,Dictionary<RoadConstructionOptions,RoadSceneData>> Cache=new();
    public static RoadPaving Classify(RoadNetwork roads,RoadEdge edge,RoadConstructionOptions o)
    {
        if(edge.Role==StreetRole.BackLane)return o.BackLane;
        bool inside=roads.Settlement.Town?.Interior.Contains(edge.Points[edge.Points.Count/2])??false;
        if(inside&&edge.Role==StreetRole.MarketStreet)return o.MarketStreet;
        if(inside&&edge.Role==StreetRole.GateStreet)return o.GateStreet;
        return inside||roads.Settlement.Region?.DistrictAt(edge.Points[edge.Points.Count/2])!=null?o.TownStreet:o.RuralRoad;
    }
    public static RoadSceneData Build(RoadGrading g,RoadConstructionOptions o,CancellationToken cancellation=default)
    {
        if(!float.IsFinite(o.NearSpacing)||o.NearSpacing<.25f||o.NearSpacing>1||!float.IsFinite(o.FarSpacing)||o.FarSpacing<o.NearSpacing||o.FarSpacing>4||
            !float.IsFinite(o.ChunkSize)||o.ChunkSize<32||o.ChunkSize>256||!float.IsFinite(o.DetailDistance)||o.DetailDistance<32||
            Math.Abs(o.ChunkSize/o.NearSpacing-MathF.Round(o.ChunkSize/o.NearSpacing))>.001f||Math.Abs(o.ChunkSize/o.FarSpacing-MathF.Round(o.ChunkSize/o.FarSpacing))>.001f)
            throw new ArgumentException("路面精度、分块或可见距离无效");
        if(g.Graded==null)throw new ArgumentException("03B 尚未发布修整地形");
        cancellation.ThrowIfCancellationRequested();var cached=Cache.GetOrCreateValue(g);
        var geometryKey=o with{Collision=true,Navigation=true,MaterialKey="",DetailDistance=280};
        if(cached.TryGetValue(geometryKey,out var existing))return new(g,o,existing.Near.ToList(),existing.Far.ToList(),existing.Area);
        var byId=g.Roads.Edges.ToDictionary(e=>e.Id);
        var plaza=ParcelGeometry.Rect(g.PlazaRect);
        var gates=ParcelGeometry.Union(new Paths64(g.Roads.Settlement.Town?.Gates.SelectMany(x=>x.Passage.CopyPaths())??Enumerable.Empty<Path64>()));
        // Gate paving remains within the authoritative road footprint; no new road is invented.
        gates=ParcelGeometry.Intersection(gates,g.Roads.Geometry.Area.CopyPaths());
        var special=ParcelGeometry.Union(new Paths64(plaza.Concat(gates)));
        var regions=new List<(ParcelRegion Shape,RoadFaceSource Source)>();
        foreach(var s in g.Roads.Geometry.Surfaces){
            var paving=Classify(g.Roads,byId[s.RoadId],o);
            if(s.NodeId is int node)paving=g.Roads.Edges.Where(e=>e.From==node||e.To==node).Select(e=>Classify(g.Roads,e,o)).DefaultIfEmpty(paving).OrderByDescending(p=>p switch{RoadPaving.Stone=>4,RoadPaving.Cobble=>3,RoadPaving.Earth=>2,RoadPaving.RuttedEarth=>1,_=>0}).First();
            var rest=ParcelGeometry.Difference(s.Shape.CopyPaths(),special);
            if(ParcelGeometry.Area(rest)>.00001)regions.Add((new(rest),new(s.Id,s.RoadId,s.NodeId,paving)));
            var gate=ParcelGeometry.Difference(ParcelGeometry.Intersection(s.Shape.CopyPaths(),gates),plaza);
            if(ParcelGeometry.Area(gate)>.00001)regions.Add((new(gate),new(s.Id,s.RoadId,s.NodeId,o.Gate)));
        }
        regions.Add((new(plaza),new(-1,-1,null,o.Plaza)));
        var area=new ParcelRegion(ParcelGeometry.Union(new Paths64(g.Roads.Geometry.Area.CopyPaths().Concat(plaza))));
        var borders=new Dictionary<Vector2I,List<(Vector2 A,Vector2 B)>>();
        foreach(var ring in area.Rings)for(int i=0;i<ring.Count;i++){var a=ring[i];var b=ring[(i+1)%ring.Count];int steps=Math.Max(1,(int)Math.Ceiling(a.DistanceTo(b)/8));var bins=new HashSet<Vector2I>();
            for(int k=0;k<=steps;k++){var p=a.Lerp(b,(float)k/steps);var cell=new Vector2I((int)MathF.Floor(p.X/8),(int)MathF.Floor(p.Y/8));for(int z=-1;z<=1;z++)for(int x=-1;x<=1;x++)bins.Add(cell+new Vector2I(x,z));}
            foreach(var bin in bins){if(!borders.TryGetValue(bin,out var edges))borders[bin]=edges=new();edges.Add((a,b));}}
        var edgeDistances=new Dictionary<Vector2,float>();
        float EdgeDistance(Vector2 p){if(edgeDistances.TryGetValue(p,out float d))return d;d=1;var key=new Vector2I((int)MathF.Floor(p.X/8),(int)MathF.Floor(p.Y/8));if(borders.TryGetValue(key,out var edges))foreach(var (a,b) in edges)d=Math.Min(d,BuildingGeometry.DistanceToSegment(p,a,b));edgeDistances[p]=d;return d;}
        List<RoadChunk> Mesh(float step)
        {
            var chunks=new Dictionary<Vector2I,RoadChunk>();var heights=new Dictionary<Vector2,float>();int count=0;
            float Height(Vector2 p){if(!heights.TryGetValue(p,out float h)){if(!g.TryDesignHeightNear(p,.02f,out h))throw new InvalidOperationException("正式路面顶点缺少设计高程");heights[p]=h;}return h;}
            void Emit(RoadChunk chunk,RoadFaceSource source,Vector2 a,Vector2 b,Vector2 c,int depth)
            {
                float ha=Height(a),hb=Height(b),hc=Height(c);var ab=(a+b)/2;var bc=(b+c)/2;var ca=(c+a)/2;
                float error=Math.Max(Math.Abs(Height(ab)-(ha+hb)/2),Math.Max(Math.Abs(Height(bc)-(hb+hc)/2),Math.Max(Math.Abs(Height(ca)-(hc+ha)/2),Math.Abs(Height((a+b+c)/3)-(ha+hb+hc)/3))));
                if(depth<5&&error>(step==o.NearSpacing?.0024f:.02f)){
                    Emit(chunk,source,a,ab,ca,depth+1);Emit(chunk,source,ab,b,bc,depth+1);Emit(chunk,source,ca,bc,c,depth+1);Emit(chunk,source,ab,bc,ca,depth+1);return;}
                foreach(var p in new[]{a,b,c}){chunk.Positions.Add(new(p.X,Height(p),p.Y));
                    Vector2 uv=p;if(source.RoadId>=0&&source.NodeId==null){var loc=g.Locate(source.RoadId,p);uv=new(loc.Station,loc.Offset/byId[source.RoadId].Width);}chunk.Uv.Add(uv);chunk.EdgeDistances.Add(EdgeDistance(p));}
                chunk.Faces.Add(source);
            }
            foreach(var (shape,source) in regions)foreach(var poly in GridPolygons(shape,step,cancellation)){
                if(++count>3000000)throw new InvalidOperationException("路面网格超过 300 万面片预算，请降低精度");
                var center=poly.Aggregate(Vector2.Zero,(a,b)=>a+b)/poly.Count;
                var key=new Vector2I((int)MathF.Floor(center.X/o.ChunkSize),(int)MathF.Floor(center.Y/o.ChunkSize));
                if(!chunks.TryGetValue(key,out var chunk))chunks[key]=chunk=new(key);
                for(int i=1;i+1<poly.Count;i++){
                    var a=poly[0];var b=poly[i];var c=poly[i+1];float cross=(b-a).Cross(c-a);if(Math.Abs(cross)<1e-9f)continue;if(cross<0)(b,c)=(c,b);
                    Emit(chunk,source,a,b,c,0);
                }
            }
            return chunks.OrderBy(c=>c.Key.Y).ThenBy(c=>c.Key.X).Select(c=>c.Value).ToList();
        }
        var result=new RoadSceneData(g,o,Mesh(o.NearSpacing),Mesh(o.FarSpacing),area);cancellation.ThrowIfCancellationRequested();cached[geometryKey]=result;return result;
    }

    // Clip every source triangle to the same world grid. Shared source edges and
    // grid cuts produce identical vertices independently of the processing order.
    internal static IEnumerable<List<Vector2>> GridPolygons(ParcelRegion shape,float step,CancellationToken token=default)
    {
        var triangles=shape.Triangles;
        for(int i=0;i<triangles.Count;i+=3){token.ThrowIfCancellationRequested();
            var tri=new List<Vector2>{triangles[i],triangles[i+1],triangles[i+2]};var min=tri.Aggregate((a,b)=>a.Min(b));var max=tri.Aggregate((a,b)=>a.Max(b));
            for(int z=(int)MathF.Floor(min.Y/step);z<=(int)MathF.Floor(max.Y/step);z++){
                var row=Clip(Clip(tri,1,z*step,true),1,(z+1)*step,false);if(row.Count<3)continue;
                for(int x=(int)MathF.Floor(row.Min(p=>p.X)/step);x<=(int)MathF.Floor(row.Max(p=>p.X)/step);x++){
                var poly=Clip(Clip(row,0,x*step,true),0,(x+1)*step,false);
                if(poly.Count>=3)yield return poly;
            }}
        }
    }
    private static List<Vector2> Clip(List<Vector2> input,int axis,float bound,bool greater)
    {
        var result=new List<Vector2>(6);if(input.Count==0)return result;
        float V(Vector2 p)=>axis==0?p.X:p.Y;
        for(int i=0;i<input.Count;i++){var a=input[i];var b=input[(i+1)%input.Count];double da=V(a)-bound,db=V(b)-bound;bool inside=greater?da>=0:da<=0;
            if(inside)result.Add(a);
            if(inside!=(greater?db>=0:db<=0)){double t=da/(da-db);var p=new Vector2((float)(a.X+(b.X-a.X)*t),(float)(a.Y+(b.Y-a.Y)*t));if(axis==0)p.X=bound;else p.Y=bound;result.Add(p);}}
        return result.Distinct().ToList();
    }

    public static Node3D Instantiate(RoadSceneData data,Material? material=null)
    {
        var root=new RoadLodRoot{Name="Roads",DetailDistance=data.Options.DetailDistance};material??=RoadSurfaceMaterial.Create();var o=data.Options;
        root.SetMeta("terrain_fingerprint",data.Grading.Graded!.Fingerprint);root.SetMeta("material_key",o.MaterialKey);
        void Lod(IEnumerable<RoadChunk> chunks,bool near){foreach(var chunk in chunks){
            var origin=new Vector3((chunk.Key.X+.5f)*o.ChunkSize,0,(chunk.Key.Y+.5f)*o.ChunkSize);
            var node=new MeshInstance3D{Name=$"{(near?"Near":"Far")}_{chunk.Key.X}_{chunk.Key.Y}",Position=origin,Mesh=chunk.Mesh(origin),MaterialOverride=material,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeBegin=near?0:o.DetailDistance,VisibilityRangeEnd=near?o.DetailDistance:0};
            node.SetMeta("road_ids",chunk.Faces.Select(f=>f.RoadId).ToArray());node.SetMeta("node_ids",chunk.Faces.Select(f=>f.NodeId??-1).ToArray());node.SetMeta("paving",chunk.Faces.Select(f=>(int)f.Paving).ToArray());root.AddChild(node);
            if(near&&o.Collision){var body=new StaticBody3D{Name="Collision"};node.AddChild(body);body.AddChild(new CollisionShape3D{Shape=new ConcavePolygonShape3D{Data=chunk.Positions.Select(p=>p-origin).ToArray()}});}
        }}
        Lod(data.Near,true);Lod(data.Far,false);
        var overview=new RoadChunk(Vector2I.Zero);foreach(var chunk in data.Far){overview.Positions.AddRange(chunk.Positions);overview.Uv.AddRange(chunk.Uv);overview.EdgeDistances.AddRange(chunk.EdgeDistances);overview.Faces.AddRange(chunk.Faces);}
        var overviewNode=new MeshInstance3D{Name="Overview",Mesh=overview.Mesh(),MaterialOverride=material,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off};
        overviewNode.SetMeta("road_ids",overview.Faces.Select(f=>f.RoadId).ToArray());overviewNode.SetMeta("node_ids",overview.Faces.Select(f=>f.NodeId??-1).ToArray());overviewNode.SetMeta("paving",overview.Faces.Select(f=>(int)f.Paving).ToArray());root.AddChild(overviewNode);
        // Only the outer boundary receives a skirt; internal material seams stay flat.
        var skirt=new CityGeometry();var g=data.Grading;
        foreach(var ring in data.Area.Rings)for(int j=0;j<ring.Count;j++){var a=ring[j];var b=ring[(j+1)%ring.Count];int n=Math.Max(1,(int)Math.Ceiling(a.DistanceTo(b)/o.NearSpacing));
            for(int k=0;k<n;k++){var p=a.Lerp(b,(float)k/n);var q=a.Lerp(b,(float)(k+1)/n);g.TryDesignHeightNear(p,.03f,out float h);g.TryDesignHeightNear(q,.03f,out float hh);
                var x=new Vector3(p.X,h,p.Y);var y=new Vector3(q.X,hh,q.Y);var normal=new Vector3(q.Y-p.Y,0,p.X-q.X).Normalized();skirt.Quad(x,y,y-Vector3.Up*.15f,x-Vector3.Up*.15f,normal,new("746d59"),"road_skirt","skirt");}}
        root.AddChild(new MeshInstance3D{Name="EdgeSkirt",Mesh=skirt.ToMesh(),MaterialOverride=new StandardMaterial3D{VertexColorUseAsAlbedo=true,Roughness=1},CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
        return root;
    }
}
