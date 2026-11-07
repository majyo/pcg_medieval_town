using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PcgCitySandbox.Pcg;

namespace PcgCitySandbox;

// Graded terrain (03B): coarse cells touched by the grading are replaced by refined
// cells built from the native surface plus the difference raster.
public partial class MapRenderer
{
    public const int RefinedSubdivision = 10;
    private GradedTerrain? _graded; private bool _gradedHeat;
    private ShaderMaterial? _gradedMaterial;
    private readonly StandardMaterial3D _heatMaterial = new() { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
    private readonly Dictionary<TerrainView, Color[]> _viewColors = new();
    private readonly Dictionary<TerrainView, ArrayMesh> _holedMeshes = new();
    private RoadLodRoot? _gradedMesh;
    private int[] _refinedCells = Array.Empty<int>();
    public GradedTerrain? Graded => _graded;
    public bool GradedHeat => _gradedHeat;
    public int RefinedCellCount => _refinedCells.Length;
    public int GradedTriangleCount { get; private set; }
    public int GradedTreeCount { get; private set; }
    public Node3D? GradedMesh => _gradedMesh;

    public (int Samples,int Intrusions,float Worst) VerifyRoadOcclusion()
    {
        if(_gradedMesh==null||_graded==null)return(0,0,0);int samples=0,bad=0;float worst=float.NegativeInfinity;
        foreach(var node in _gradedMesh.GetChildren().OfType<MeshInstance3D>()){
            if(node.CastShadow==GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)continue;
            if(node.Name=="Overview")continue;
            var arrays=node.Mesh.SurfaceGetArrays(0);var p=arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();var ids=arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            for(int i=0;i<ids.Length;i+=3){var a=p[ids[i]];var b=p[ids[i+1]];var c=p[ids[i+2]];
                foreach(var q in new[]{a,b,c,(a+b)/2,(b+c)/2,(c+a)/2,(a+b+c)/3}){
                    var xz=new Vector2(q.X,q.Z);if(_graded.RoadMaskAt(xz)<.01f||!_graded.Design.TryDesignHeight(xz,out float design))continue;
                    samples++;float error=q.Y-design;worst=Math.Max(worst,error);if(error>-.0001f)bad++;
                }
            }
        }
        return(samples,bad,worst);
    }

    // A tree goes if its trunk stands on the road, the shoulder or ground moved by more than 0.1 m.
    public static bool TreeGraded(TreePlacement tree, GradedTerrain graded)
    {
        var p = new Vector2(tree.Position.X, tree.Position.Z); float r = 8 * tree.Scale;
        if(graded.KindAt(p) is GradingCell.Road or GradingCell.Shoulder)return true;
        int x0=(int)MathF.Floor((p.X-r-graded.Origin.X)/graded.Cell),x1=(int)MathF.Ceiling((p.X+r-graded.Origin.X)/graded.Cell);
        int z0=(int)MathF.Floor((p.Y-r-graded.Origin.Y)/graded.Cell),z1=(int)MathF.Ceiling((p.Y+r-graded.Origin.Y)/graded.Cell);
        for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)if(Math.Abs(graded.NodeDelta(x,z))>.1f&&graded.NodePosition(x,z).DistanceSquaredTo(p)<r*r)return true;
        return false;
    }

    public void SetGradedTerrain(GradedTerrain? graded, bool heat = false)
    {
        if (ReferenceEquals(graded, _graded) && heat == _gradedHeat) return;
        bool changed = !ReferenceEquals(graded, _graded); _gradedHeat = heat;
        if(!changed&&CurrentView==TerrainView.Landscape){_gradedMaterial?.SetShaderParameter("grading_heat",heat);return;}
        if (changed)
        {
            ClearGraded(); _graded = graded;
            if (graded != null) _refinedCells = RefinedCells(graded);
            var skirt=GetNodeOrNull<MeshInstance3D>("TerrainEdges");if(skirt!=null){RemoveChild(skirt);skirt.QueueFree();}BuildSkirt();
            bool visible = _forestRoot.Visible; RemoveChild(_forestRoot); _forestRoot.QueueFree(); BuildForest(); _forestRoot.Visible = visible;
        }
        ApplyGraded();
    }

    // Edges where refined cells meet untouched cells or the map border must carry no
    // difference, otherwise the two meshes would separate there.
    public (int Edges, int Mismatches, float Worst) VerifyRefinedBoundary()
    {
        if (_graded == null) return (0, 0, 0);
        int segments = Data.Side - 1; var refined = new HashSet<int>(_refinedCells); int edges = 0, bad = 0; float worst = 0;
        foreach (int cell in _refinedCells)
        {
            int cx = cell % segments, cz = cell / segments; var origin = Data.GridPosition(cx, cz);
            foreach (var (dx, dz, a, b) in new[] { (-1, 0, Vector2.Zero, new Vector2(0, 1)), (1, 0, new Vector2(1, 0), Vector2.One), (0, -1, Vector2.Zero, new Vector2(1, 0)), (0, 1, new Vector2(0, 1), Vector2.One) })
            {
                int nx = cx + dx, nz = cz + dz; bool inside = nx >= 0 && nz >= 0 && nx < segments && nz < segments;
                if (!inside || refined.Contains(nz * segments + nx)) continue; edges++;
                for (int k = 0; k <= RefinedSubdivision; k++)
                { var p = origin + a.Lerp(b, (float)k / RefinedSubdivision) * Data.Spacing; float d = Math.Abs(_graded.BedHeightAt(p)-Data.HeightAt(p)); worst = Math.Max(worst, d); if (d > 1e-5f) bad++; }
            }
        }
        return (edges, bad, worst);
    }

    private void ClearGraded()
    {
        foreach (var mesh in _holedMeshes.Values) mesh.Dispose(); _holedMeshes.Clear();
        if (_gradedMesh != null) { RemoveChild(_gradedMesh); SceneMeshCleanup.DisposeMeshes(_gradedMesh);_gradedMesh.QueueFree(); _gradedMesh = null; }
        _refinedCells = Array.Empty<int>(); GradedTriangleCount = 0; _graded = null;
    }

    // Cells reached by the bilinear support of any changed node. Along the edges shared
    // with untouched cells the difference is zero, so refined vertices lie exactly on the
    // coarse edge and no gap opens.
    private int[] RefinedCells(GradedTerrain graded)
    {
        return graded.ModifiedNativeCells(Data).OrderBy(c=>c).ToArray();
    }

    private void ApplyGraded()
    {
        if (_ground == null) return;
        if (_gradedMesh != null) { RemoveChild(_gradedMesh); SceneMeshCleanup.DisposeMeshes(_gradedMesh);_gradedMesh.QueueFree(); _gradedMesh = null; }
        if (_graded == null) { if (_viewMeshes.TryGetValue(CurrentView, out var plain)) _ground.Mesh = plain; return; }
        if (!_viewColors.ContainsKey(CurrentView)) return;
        _ground.Mesh = HoledMesh(CurrentView);
        if (_gradedMaterial == null) { _gradedMaterial = (ShaderMaterial)_naturalMaterial.Duplicate(); _gradedMaterial.SetShaderParameter("grading_mask", true); }
        _gradedMaterial.SetShaderParameter("grading_heat", _gradedHeat);
        Material material = CurrentView == TerrainView.Landscape ? _gradedMaterial : _gradedHeat ? _heatMaterial : _landMaterial;
        _gradedMesh = new RoadLodRoot { Name = "GradedTerrain",BatchOnly=true };GradedTriangleCount=0;
        int segments=Data.Side-1;
        foreach(var group in _refinedCells.GroupBy(c=>(c%segments/24,c/segments/24))){
            var mesh=RefinedMesh(CurrentView,_gradedHeat,group.ToArray());
            _gradedMesh.AddChild(new MeshInstance3D{Name=$"Near_{group.Key.Item1}_{group.Key.Item2}",Mesh=mesh,MaterialOverride=material,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});}
        var overviewVertices=new List<Vector3>();var overviewNormals=new List<Vector3>();var overviewColors=new List<Color>();var overviewIndices=new List<int>();
        foreach(var child in _gradedMesh.GetChildren().OfType<MeshInstance3D>()){var a=child.Mesh.SurfaceGetArrays(0);int start=overviewVertices.Count;overviewVertices.AddRange(a[(int)Mesh.ArrayType.Vertex].AsVector3Array());overviewNormals.AddRange(a[(int)Mesh.ArrayType.Normal].AsVector3Array());overviewColors.AddRange(a[(int)Mesh.ArrayType.Color].AsColorArray());overviewIndices.AddRange(a[(int)Mesh.ArrayType.Index].AsInt32Array().Select(i=>i+start));}
        var overviewArrays=new Godot.Collections.Array();overviewArrays.Resize((int)Mesh.ArrayType.Max);overviewArrays[(int)Mesh.ArrayType.Vertex]=overviewVertices.ToArray();overviewArrays[(int)Mesh.ArrayType.Normal]=overviewNormals.ToArray();overviewArrays[(int)Mesh.ArrayType.Color]=overviewColors.ToArray();overviewArrays[(int)Mesh.ArrayType.Index]=overviewIndices.ToArray();var overview=new ArrayMesh();overview.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,overviewArrays);
        _gradedMesh.AddChild(new MeshInstance3D{Name="Overview",Mesh=overview,MaterialOverride=material,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
        // Cast terrain shadows at native density instead of redrawing fine road-bed
        // triangles in every directional-light cascade.
        int nearTriangles=GradedTriangleCount;
        _gradedMesh.AddChild(new MeshInstance3D{Name="ShadowProxy",Mesh=RefinedMesh(CurrentView,false,_refinedCells,1),MaterialOverride=material,CastShadow=GeometryInstance3D.ShadowCastingSetting.ShadowsOnly});
        GradedTriangleCount=nearTriangles;
        AddChild(_gradedMesh);
    }

    private ArrayMesh HoledMesh(TerrainView view)
    {
        if (_holedMeshes.TryGetValue(view, out var cached)) return cached;
        int segments = Data.Side - 1; var hole = new bool[segments * segments]; foreach (int c in _refinedCells) hole[c] = true;
        var indices = new List<int>(_indices.Length);
        for (int cell = 0; cell < segments * segments; cell++) if (!hole[cell]) for (int k = 0; k < 6; k++) indices.Add(_indices[cell * 6 + k]);
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices; arrays[(int)Mesh.ArrayType.Normal] = Data.Normals;
        arrays[(int)Mesh.ArrayType.Color] = _viewColors[view]; arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); _holedMeshes[view] = mesh; return mesh;
    }

    // Vertex colour: in the landscape view it carries the grading mask for the shader
    // (r cut, g fill, b road/shoulder); otherwise the view colour or cut/fill heat.
    private ArrayMesh RefinedMesh(TerrainView view, bool heat, int[] cells,int subdivision=RefinedSubdivision)
    {
        var g = _graded!; int segments = Data.Side - 1, n = subdivision, side = n + 1; var colors = _viewColors[view];
        var vertices = new Vector3[cells.Length * side * side]; var normals = new Vector3[vertices.Length]; var tint = new Color[vertices.Length];
        var cellIndices = new int[cells.Length][];
        Parallel.For(0, cells.Length, c =>
        {
            int cell = cells[c], cx = cell % segments, cz = cell / segments, a = Data.Index(cx, cz), b = a + 1, cc = a + Data.Side, d = cc + 1;
            var origin = Data.GridPosition(cx, cz); int baseVertex = c * side * side;
            for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++)
            {
                float u = (float)i / n, v = (float)j / n; var p = origin + new Vector2(u, v) * Data.Spacing; int k = baseVertex + i + j * side;
                float delta = g.DeltaAt(p);
                vertices[k] = new Vector3(p.X, g.BedHeightAt(p), p.Y);
                // Same split as the coarse cell: triangle (a,b,c) for u+v ≤ 1, else (b,d,c).
                Vector3 coarse; Color color;
                if (u + v <= 1) { coarse = Data.Normals[a] * (1 - u - v) + Data.Normals[b] * u + Data.Normals[cc] * v; color = colors[a] * (1 - u - v) + colors[b] * u + colors[cc] * v; }
                else { coarse = Data.Normals[d] * (u + v - 1) + Data.Normals[cc] * (1 - u) + Data.Normals[b] * (1 - v); color = colors[d] * (u + v - 1) + colors[cc] * (1 - u) + colors[b] * (1 - v); }
                // Add the gradient of the difference to the interpolated terrain normal,
                // so shading matches untouched cells where the difference is zero.
                const float e = .25f;
                float gx = (g.DeltaAt(p + new Vector2(e, 0)) - g.DeltaAt(p - new Vector2(e, 0))) / (2 * e), gz = (g.DeltaAt(p + new Vector2(0, e)) - g.DeltaAt(p - new Vector2(0, e))) / (2 * e);
                coarse = coarse.Normalized(); float ny = Math.Max(.05f, coarse.Y);
                normals[k] = new Vector3(coarse.X / ny - gx, 1, coarse.Z / ny - gz).Normalized();
                // Landscape view: mask for the shader (cut, fill, road bed), magnitudes saturating at 0.6 m.
                tint[k] = view == TerrainView.Landscape ? new Color(Math.Clamp(-delta / RoadGradingPreview.Saturation, 0, 1), Math.Clamp(delta / RoadGradingPreview.Saturation, 0, 1), g.RoadMaskAt(p), 1)
                    : heat ? (Math.Abs(delta) < .005f ? color : RoadGradingPreview.CutFill(delta)) : color;
            }
            // Flat interiors can use a fan while retaining every boundary vertex.
            // Compare against all fine-grid heights and material masks first, so
            // this cannot erase a narrow road bed or break a neighbouring chunk.
            var local=new List<int>(n*n*6);
            int At(int x,int y)=>baseVertex+x+y*side;
            void Quad(int x,int y){int va=At(x,y),vb=At(x+1,y),vc=At(x,y+1),vd=At(x+1,y+1);local.AddRange(new[]{va,vb,vc,vb,vd,vc});}
            bool Fan(int x0,int y0,int w,int h){
                if(w<2||h<2)return false;int mx=x0+w/2,my=y0+h/2,center=At(mx,my);var boundary=new List<(int X,int Y)>();
                for(int x=x0;x<x0+w;x++)boundary.Add((x,y0));for(int y=y0;y<y0+h;y++)boundary.Add((x0+w,y));
                for(int x=x0+w;x>x0;x--)boundary.Add((x,y0+h));for(int y=y0+h;y>y0;y--)boundary.Add((x0,y));
                Vector2 XY(int id)=>new((id-baseVertex)%side,(id-baseVertex)/side);
                bool Fits(List<int> candidate){
                    for(int i=0;i<candidate.Count;i+=3){int va=candidate[i],vb=candidate[i+1],vc=candidate[i+2];var pa=XY(va);var pb=XY(vb);var pc=XY(vc);float den=(pb-pa).Cross(pc-pa);if(den<=0)return false;
                        for(int y=y0;y<=y0+h;y++)for(int x=x0;x<=x0+w;x++){var point=new Vector2(x,y);float u=(point-pa).Cross(pc-pa)/den,v=(pb-pa).Cross(point-pa)/den;if(u<-.0001f||v<-.0001f||u+v>1.0001f)continue;int at=At(x,y);
                            float height=vertices[va].Y*(1-u-v)+vertices[vb].Y*u+vertices[vc].Y*v;var mask=tint[va]*(1-u-v)+tint[vb]*u+tint[vc]*v;var actual=tint[at];
                            if(Math.Abs(height-vertices[at].Y)>.008f||Math.Abs(mask.R-actual.R)>.08f||Math.Abs(mask.G-actual.G)>.08f||Math.Abs(mask.B-actual.B)>.08f)return false;}
                    }return true;
                }
                var polygon=boundary.Select(v=>At(v.X,v.Y)).ToList();var ears=new List<int>();
                while(polygon.Count>3){bool removed=false;
                    for(int i=0;i<polygon.Count;i++){int va=polygon[(i+polygon.Count-1)%polygon.Count],vb=polygon[i],vc=polygon[(i+1)%polygon.Count];var pa=XY(va);var pb=XY(vb);var pc=XY(vc);float den=(pb-pa).Cross(pc-pa);if(den<=0)continue;bool blocked=false;
                        foreach(int q in polygon){if(q==va||q==vb||q==vc)continue;var point=XY(q);float u=(point-pa).Cross(pc-pa)/den,v=(pb-pa).Cross(point-pa)/den;if(u>=-.0001f&&v>=-.0001f&&u+v<=1.0001f){blocked=true;break;}}
                        if(blocked)continue;ears.AddRange(new[]{va,vb,vc});polygon.RemoveAt(i);removed=true;break;
                    }
                    if(!removed)break;
                }
                if(polygon.Count==3){ears.AddRange(polygon);if(Fits(ears)){local.AddRange(ears);return true;}}
                var fan=new List<int>();for(int edge=0;edge<boundary.Count;edge++){var a=boundary[edge];var b=boundary[(edge+1)%boundary.Count];fan.AddRange(new[]{At(a.X,a.Y),At(b.X,b.Y),center});}
                if(!Fits(fan))return false;local.AddRange(fan);return true;
            }
            void Tile(int x,int y,int w,int h){
                if(Fan(x,y,w,h))return;
                if(w<3||h<3){for(int yy=y;yy<y+h;yy++)for(int xx=x;xx<x+w;xx++)Quad(xx,yy);return;}
                int a=w/2,b=h/2;Tile(x,y,a,b);Tile(x+a,y,w-a,b);Tile(x,y+b,a,h-b);Tile(x+a,y+b,w-a,h-b);
            }
            Tile(0,0,n,n);cellIndices[c]=local.ToArray();
        });
        var indices=cellIndices.SelectMany(i=>i).ToArray();var used=indices.Distinct().ToArray();var remap=new int[vertices.Length];for(int i=0;i<used.Length;i++)remap[used[i]]=i;
        for(int i=0;i<indices.Length;i++)indices[i]=remap[indices[i]];vertices=used.Select(i=>vertices[i]).ToArray();normals=used.Select(i=>normals[i]).ToArray();tint=used.Select(i=>tint[i]).ToArray();
        GradedTriangleCount += indices.Length / 3;
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices; arrays[(int)Mesh.ArrayType.Normal] = normals; arrays[(int)Mesh.ArrayType.Color] = tint; arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }
}
