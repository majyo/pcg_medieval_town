using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using PcgCitySandbox.Pcg;

namespace PcgCitySandbox;

public enum TerrainView { Landscape, Elevation, Slope, Suitability }

public partial class MapRenderer : Node3D
{
    public TemperateMapData Data { get; private set; } = null!;
    private MeshInstance3D _ground = null!;
    private Node3D _forestRoot = null!;
    private ParcelRegion? _vegetationMask;
    public int VisibleTreeCount {get;private set;}
    public int FilteredTreeCount=>Data.Trees.Count-VisibleTreeCount;
    private MeshInstance3D _water = null!;
    private Vector3[] _vertices = Array.Empty<Vector3>();
    private int[] _indices = Array.Empty<int>();
    private readonly StandardMaterial3D _landMaterial = new()
    { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
    private readonly Dictionary<TerrainView, ArrayMesh> _viewMeshes = new();
    private ShaderMaterial _naturalMaterial = null!, _waterMaterial = null!;
    public TerrainVisualData VisualData { get; private set; } = null!;
    public RiverVisualMesh RiverGeometry { get; private set; } = null!;
    public TerrainView CurrentView { get; private set; }
    public TerrainVisualQuality VisualQuality { get; private set; } = TerrainVisualQuality.Standard;
    private bool _planningContext;
    // Natural-material parameters are mirrored onto the graded-terrain material.
    private void Param(string name, Variant value) { _naturalMaterial.SetShaderParameter(name, value); _gradedMaterial?.SetShaderParameter(name, value); }
    public void SetPlanningContext(bool planning)
    {
        if (planning == _planningContext) return; _planningContext = planning;
        Param("planning_fade", planning ? .6f : 0f);
        _waterMaterial.SetShaderParameter("planning_fade", planning ? .6f : 0f);
    }

    public void Build(TemperateMapData data)
    {
        Data = data;
        VisualData = new TerrainVisualData(data);
        var textures = TerrainVisualTextures.Shared;
        _naturalMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        _naturalMaterial.SetShaderParameter("map_size", data.Settings.SizeMeters);
        _naturalMaterial.SetShaderParameter("height_range", new Vector2(data.MinHeight, data.MaxHeight));
        _naturalMaterial.SetShaderParameter("natural_control", VisualData.Natural);
        _naturalMaterial.SetShaderParameter("river_control", VisualData.River);
        _naturalMaterial.SetShaderParameter("surface_normals", textures.NormalsRoughness);
        _vertices = new Vector3[data.Heights.Length];
        for (int z = 0; z < data.Side; z++)
        for (int x = 0; x < data.Side; x++)
        {
            int i = data.Index(x, z); Vector2 p = data.GridPosition(x, z);
            _vertices[i] = new Vector3(p.X, data.Heights[i], p.Y);
        }
        int segments = data.Side - 1;
        _indices = new int[segments * segments * 6];
        int k = 0;
        for (int z = 0; z < segments; z++)
        for (int x = 0; x < segments; x++)
        {
            int a = data.Index(x, z), b = a + 1, c = a + data.Side, d = c + 1;
            // Godot front faces use clockwise winding.
            _indices[k++] = a; _indices[k++] = b; _indices[k++] = c;
            _indices[k++] = b; _indices[k++] = d; _indices[k++] = c;
        }
        _ground = new MeshInstance3D { Name = "Terrain", MaterialOverride = _landMaterial };
        AddChild(_ground);
        SetView(TerrainView.Landscape);
        BuildSkirt(); BuildWater(); BuildForest();
        ApplyVisualSettings(GD.Load<TerrainVisualSettings>("res://configs/terrain_visual.tres"));
        GD.Print($"TERRAIN_VISUAL_READY control_ms={VisualData.BuildMilliseconds:F1} texture_bytes={VisualData.TextureBytes + textures.Bytes} river_triangles={RiverGeometry.Indices.Length / 3}");
    }

    public void SetView(TerrainView view)
    {
        if(_ground.Mesh!=null&&CurrentView==view)return;
        CurrentView = view;
        _ground.MaterialOverride = view == TerrainView.Landscape ? _naturalMaterial : _landMaterial;
        if (_viewMeshes.TryGetValue(view, out var cached)) { _ground.Mesh = cached; ApplyGraded(); return; }
        var colors = new Color[Data.Heights.Length];
        Color meadow = new("849965"), forestFloor = new("526d44"), bank = new("b8ae82");
        using var variation = new FastNoiseLite { Seed = Data.Settings.Seed, Frequency = 0.006f, FractalType = FastNoiseLite.FractalTypeEnum.None };
        for (int i = 0; i < colors.Length; i++)
        {
            Vector3 p = _vertices[i];
            float h = Mathf.InverseLerp(Data.MinHeight, Data.MaxHeight, Data.Heights[i]);
            colors[i] = view switch
            {
                TerrainView.Elevation => new Color("315e70").Lerp(new Color("92b78e"), Mathf.Clamp(h * 2, 0, 1))
                    .Lerp(new Color("e6d3a0"), Mathf.Clamp((h - 0.5f) * 2, 0, 1)),
                TerrainView.Slope => new Color("7db39e").Lerp(new Color("e8c36f"), Mathf.Clamp(Data.Slopes[i] / 8, 0, 1))
                    .Lerp(new Color("be6251"), Mathf.Clamp((Data.Slopes[i] - 8) / 12, 0, 1)),
                TerrainView.Suitability => Data.Water[i] ? new Color("315f74") : new Color("ad7860").Lerp(new Color("90bc84"), Data.Suitability[i]),
                _ => meadow.Lerp(new Color("a5ae77"), h * 0.5f)
                    .Lerp(forestFloor, Mathf.Clamp(Data.Forest[i] * 0.85f, 0, 0.65f))
                    .Lerp(bank, Mathf.Clamp(1 - (Data.RiverDistances[i] - Data.Settings.RiverWidthMeters * 0.5f) / 17, 0, 0.75f))
                    * (0.97f + 0.045f * variation.GetNoise2D(p.X, p.Z))
            };
        }
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices;
        arrays[(int)Mesh.ArrayType.Normal] = Data.Normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = _indices;
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _ground.Mesh = mesh;
        _viewMeshes.Add(view, mesh); _viewColors[view] = colors;
        ApplyGraded();
    }

    public void ApplyVisualSettings(TerrainVisualSettings settings)
    {
        settings.Validate();
        foreach (var pair in new[] { ("meadow", settings.Meadow), ("dry_grass", settings.DryGrass), ("forest_floor", settings.ForestFloor), ("soil", settings.Soil), ("wet_mud", settings.WetMud), ("gravel", settings.Gravel) })
            Param(pair.Item1, pair.Item2);
        Param("macro_variation", settings.MacroVariation);
        Param("detail_strength", settings.DetailStrength);
        Param("normal_strength", settings.NormalStrength);
        Param("bank_width", settings.BankWidth);
        _waterMaterial.SetShaderParameter("flow_speed", settings.FlowSpeed);
        SetVisualQuality(settings.Quality);
    }
    public void SetVisualQuality(TerrainVisualQuality quality)
    {
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        VisualQuality = quality; Param("quality", (int)quality); _waterMaterial.SetShaderParameter("quality", (int)quality);
    }
    public override void _ExitTree()
    {
        foreach (var mesh in _viewMeshes.Values) mesh.Dispose();
        _viewMeshes.Clear(); ClearGraded(); _gradedMaterial?.Dispose(); _heatMaterial.Dispose(); VisualData?.Dispose(); _naturalMaterial?.Dispose(); _waterMaterial?.Dispose(); _landMaterial.Dispose();
    }

    public void ShowForest(bool visible) => _forestRoot.Visible = visible;
    public void SetVegetationMask(ParcelRegion? mask){if(ReferenceEquals(mask,_vegetationMask))return;bool visible=_forestRoot.Visible;_vegetationMask=mask;RemoveChild(_forestRoot);_forestRoot.QueueFree();BuildForest();_forestRoot.Visible=visible;}
    public static bool TreeMasked(TreePlacement tree,ParcelRegion mask){var p=new Vector2(tree.Position.X,tree.Position.Z);float radius=8*tree.Scale;if(!mask.Bounds.Grow(radius).HasPoint(p))return false;if(mask.Contains(p))return true;
        foreach(var ring in mask.Rings)for(int i=0;i<ring.Count;i++)if(BuildingGeometry.DistanceToSegment(p,ring[i],ring[(i+1)%ring.Count])<radius)return true;return false;}
    public void ShowWater(bool visible) => _water.Visible = visible;

    private void BuildSkirt()
    {
        using var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
        Color top = new("817c60"), bottom = new("4e574d");
        for (int side = 0; side < 4; side++)
        for (int i = 0; i < Data.Side - 1; i++)
        {
            (int ax, int az, int bx, int bz) = side switch
            {
                0 => (i, 0, i + 1, 0),
                1 => (Data.Side - 1, i, Data.Side - 1, i + 1),
                2 => (i + 1, Data.Side - 1, i, Data.Side - 1),
                _ => (0, i + 1, 0, i)
            };
            int cuts=_graded==null?1:RefinedSubdivision;var from=_vertices[Data.Index(ax,az)];var to=_vertices[Data.Index(bx,bz)];
            for(int part=0;part<cuts;part++){
            Vector3 a=from.Lerp(to,(float)part/cuts),b=from.Lerp(to,(float)(part+1)/cuts);
            if(_graded!=null){a.Y=_graded.BedHeightAt(new(a.X,a.Z));b.Y=_graded.BedHeightAt(new(b.X,b.Z));}
            Vector3 c = new(a.X, -22, a.Z), d = new(b.X, -22, b.Z);
            void SideTriangle(Vector3 v1, Vector3 v2, Vector3 v3)
            {
                st.SetNormal((v3 - v1).Cross(v2 - v1).Normalized());
                foreach (var v in new[] { v1, v2, v3 }) { st.SetColor(v.Y == -22 ? bottom : top); st.AddVertex(v); }
            }
            SideTriangle(a, c, b); SideTriangle(b, c, d);
            }
        }
        var material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        AddChild(new MeshInstance3D { Name = "TerrainEdges", Mesh = st.Commit(), MaterialOverride = material });
    }

    private void BuildWater()
    {
        RiverGeometry = new RiverVisualMesh(Data);
        _waterMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/river.gdshader") };
        _waterMaterial.SetShaderParameter("ground_heights", VisualData.Heights);
        _waterMaterial.SetShaderParameter("map_size", Data.Settings.SizeMeters);
        _waterMaterial.SetShaderParameter("grid_side", Data.Side);
        _waterMaterial.SetShaderParameter("wave_normals", TerrainVisualTextures.Shared.NormalsRoughness);
        _waterMaterial.SetShaderParameter("wave_patterns", TerrainVisualTextures.Shared.Patterns);
        _water = new MeshInstance3D
        {
            Name = "River", Mesh = RiverGeometry.ToMesh(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _waterMaterial
        };
        AddChild(_water);
    }

    private void BuildForest()
    {
        _forestRoot = new Node3D { Name = "Forests" }; AddChild(_forestRoot);
        var retained=Data.Trees.Where(t=>(_vegetationMask==null||!TreeMasked(t,_vegetationMask))&&(_graded==null||!TreeGraded(t,_graded))).ToArray();VisibleTreeCount=retained.Length;
        GradedTreeCount=_graded==null?0:Data.Trees.Count(t=>(_vegetationMask==null||!TreeMasked(t,_vegetationMask))&&TreeGraded(t,_graded));
        for (int species = 0; species < 3; species++)
        {
            var trees = retained.Where(t => t.Species == species).ToArray();
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true, Mesh = TreeMesh(species), InstanceCount = trees.Length
            };
            for (int i = 0; i < trees.Length; i++)
            {
                var t = trees[i];
                var basis = new Basis(Vector3.Up, t.Rotation).Scaled(Vector3.One * t.Scale);
                var position = t.Position; if (_graded != null) position.Y += _graded.HeightAt(new Vector2(position.X, position.Z))-Data.HeightAt(new Vector2(position.X, position.Z));
                mm.SetInstanceTransform(i, new Transform3D(basis, position));
                mm.SetInstanceColor(i, new Color(t.Tint, t.Tint, t.Tint, 1));
            }
            _forestRoot.AddChild(new MultiMeshInstance3D { Name = $"TreeSpecies{species}", Multimesh = mm });
        }
    }

    private static ArrayMesh TreeMesh(int species)
    {
        using var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
        Cone(st, new Vector3(0, 0, 0), 0.65f, 0.32f, 9, 6, new Color("68513a"));
        if (species == 2)
        {
            Cone(st, new Vector3(0, 5, 0), 4.4f, 0, 8, 7, new Color("3f6350"));
            Cone(st, new Vector3(0, 9, 0), 3.3f, 0, 7.5f, 7, new Color("4c7457"));
        }
        else
        {
            Color leaf = new(species == 0 ? "638849" : "82954e");
            Crown(st, new Vector3(0, 11, 0), new Vector3(5.4f, 5.7f, 5), leaf);
            Crown(st, new Vector3(-3.2f, 9, 0.8f), new Vector3(3.8f, 3.9f, 3.7f), leaf.Darkened(0.07f));
            Crown(st, new Vector3(2.9f, 9.7f, -1.5f), new Vector3(3.5f, 4.1f, 3.5f), leaf.Lightened(0.04f));
        }
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1 });
        return mesh;
    }

    private static void Cone(SurfaceTool st, Vector3 origin, float lower, float upper, float height, int sides, Color color)
    {
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.Tau / sides, b = (i + 1) * Mathf.Tau / sides;
            Vector3 p = origin + new Vector3(Mathf.Cos(a) * lower, 0, Mathf.Sin(a) * lower);
            Vector3 q = origin + new Vector3(Mathf.Cos(b) * lower, 0, Mathf.Sin(b) * lower);
            Vector3 r = origin + new Vector3(Mathf.Cos(a) * upper, height, Mathf.Sin(a) * upper);
            Vector3 s = origin + new Vector3(Mathf.Cos(b) * upper, height, Mathf.Sin(b) * upper);
            AddTriangle(st, p, q, r, color); if (upper > 0) AddTriangle(st, q, s, r, color);
        }
    }

    private static void Crown(SurfaceTool st, Vector3 center, Vector3 scale, Color color)
    {
        float t = (1 + Mathf.Sqrt(5)) / 2;
        Vector3[] v = { new(-1,t,0),new(1,t,0),new(-1,-t,0),new(1,-t,0),new(0,-1,t),new(0,1,t),
            new(0,-1,-t),new(0,1,-t),new(t,0,-1),new(t,0,1),new(-t,0,-1),new(-t,0,1) };
        int[] f = {0,11,5,0,5,1,0,1,7,0,7,10,0,10,11,1,5,9,5,11,4,11,10,2,10,7,6,7,1,8,
            3,9,4,3,4,2,3,2,6,3,6,8,3,8,9,4,9,5,2,4,11,6,2,10,8,6,7,9,8,1};
        for (int i = 0; i < f.Length; i += 3)
            AddTriangle(st, center + v[f[i]].Normalized() * scale, center + v[f[i + 2]].Normalized() * scale,
                center + v[f[i + 1]].Normalized() * scale, color);
    }

    private static void AddTriangle(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        st.SetColor(color); st.SetNormal((c - a).Cross(b - a).Normalized());
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
    }
}
