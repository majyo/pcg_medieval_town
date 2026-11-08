using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.Rendering;

public enum TerrainView { Landscape, Elevation, Slope, Suitability }

/// <summary>Ground, map skirt, river surface and simple trees. Vertex colors only; no custom shaders.</summary>
public partial class TerrainRenderer : Node3D
{
    private TemperateMapData _data = null!;
    private MeshInstance3D _ground = null!;
    private MeshInstance3D _water = null!;
    private Node3D _forest = new() { Name = "Forest" };
    private Vector3[] _vertices = Array.Empty<Vector3>();
    private int[] _indices = Array.Empty<int>();
    private readonly Dictionary<TerrainView, ArrayMesh> _meshes = new();
    private Func<Vector2, bool>? _treeMask;

    public TerrainView View { get; private set; }

    public void Build(TemperateMapData data)
    {
        _data = data;
        _vertices = new Vector3[data.Heights.Length];
        for (int z = 0; z < data.Side; z++)
            for (int x = 0; x < data.Side; x++)
            {
                var p = data.GridPosition(x, z);
                _vertices[data.Index(x, z)] = new Vector3(p.X, data.Heights[data.Index(x, z)], p.Y);
            }

        int segments = data.Side - 1;
        _indices = new int[segments * segments * 6];
        int i = 0;
        for (int z = 0; z < segments; z++)
            for (int x = 0; x < segments; x++)
            {
                int a = data.Index(x, z), b = a + 1, c = a + data.Side, d = c + 1;
                _indices[i++] = a;
                _indices[i++] = b;
                _indices[i++] = c;
                _indices[i++] = b;
                _indices[i++] = d;
                _indices[i++] = c;
            }

        _ground = new MeshInstance3D { Name = "Ground", MaterialOverride = MeshKit.Material() };
        AddChild(_ground);
        SetView(TerrainView.Landscape);
        BuildSkirt();
        BuildWater();
        AddChild(_forest);
        BuildForest();
    }

    public void SetView(TerrainView view)
    {
        View = view;
        if (!_meshes.TryGetValue(view, out var mesh))
            _meshes[view] = mesh = GroundMesh(view);
        _ground.Mesh = mesh;
    }

    public void ShowForest(bool visible) => _forest.Visible = visible;
    public void ShowWater(bool visible) => _water.Visible = visible;

    /// <summary>Hides trees where <paramref name="hidden"/> returns true (inside the town, on roads).</summary>
    public void SetTreeMask(Func<Vector2, bool>? hidden)
    {
        _treeMask = hidden;
        foreach (var child in _forest.GetChildren())
            child.QueueFree();
        BuildForest();
    }

    private ArrayMesh GroundMesh(TerrainView view)
    {
        var d = _data;
        var colors = new Color[d.Heights.Length];
        Color meadow = new("849965"), forest = new("526d44"), bank = new("b8ae82");
        for (int i = 0; i < colors.Length; i++)
        {
            float h = Mathf.InverseLerp(d.MinHeight, d.MaxHeight, d.Heights[i]);
            colors[i] = view switch
            {
                TerrainView.Elevation => new Color("315e70").Lerp(new Color("92b78e"), Mathf.Clamp(h * 2, 0, 1))
                    .Lerp(new Color("e6d3a0"), Mathf.Clamp((h - 0.5f) * 2, 0, 1)),
                TerrainView.Slope => new Color("7db39e").Lerp(new Color("e8c36f"), Mathf.Clamp(d.Slopes[i] / 8, 0, 1))
                    .Lerp(new Color("be6251"), Mathf.Clamp((d.Slopes[i] - 8) / 12, 0, 1)),
                TerrainView.Suitability => d.Water[i] ? new Color("315f74")
                    : new Color("ad7860").Lerp(new Color("90bc84"), d.Suitability[i]),
                _ => meadow.Lerp(new Color("a5ae77"), h * 0.5f)
                    .Lerp(forest, Mathf.Clamp(d.Forest[i] * 0.85f, 0, 0.65f))
                    .Lerp(bank, Mathf.Clamp(1 - (d.RiverDistances[i] - d.Settings.RiverWidthMeters * 0.5f) / 17, 0, 0.75f)),
            };
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices;
        arrays[(int)Mesh.ArrayType.Normal] = d.Normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = _indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private void BuildSkirt()
    {
        var kit = new MeshKit();
        int n = _data.Side - 1;
        var edge = new List<Vector3>();
        for (int i = 0; i < n; i++)
            edge.Add(_vertices[_data.Index(i, 0)]);
        for (int i = 0; i < n; i++)
            edge.Add(_vertices[_data.Index(n, i)]);
        for (int i = n; i > 0; i--)
            edge.Add(_vertices[_data.Index(i, n)]);
        for (int i = n; i > 0; i--)
            edge.Add(_vertices[_data.Index(0, i)]);
        for (int i = 0; i < edge.Count; i++)
        {
            var a = edge[i];
            var b = edge[(i + 1) % edge.Count];
            kit.Quad(a, b, new Vector3(b.X, -22, b.Z), new Vector3(a.X, -22, a.Z), new Color("6b6a55"),
                new Vector3(a.X, 0, a.Z));
        }

        AddChild(new MeshInstance3D { Name = "Skirt", Mesh = kit.Commit(), MaterialOverride = MeshKit.Material() });
    }

    private void BuildWater()
    {
        var kit = new MeshKit();
        var river = _data.River;
        for (int i = 1; i < river.Count; i++)
        {
            var a = river[i - 1];
            var b = river[i];
            var normal = (b.Position - a.Position).Normalized().Orthogonal();
            Vector3 P(RiverPoint r, float side)
            {
                var p = r.Position + normal * r.HalfWidth * side * 1.08f;
                return new Vector3(p.X, r.WaterHeight, p.Y);
            }

            kit.Quad(P(a, -1), P(b, -1), P(b, 1), P(a, 1), new Color("4d7f93"), Vector3.Up);
        }

        _water = new MeshInstance3D
        {
            Name = "River",
            Mesh = kit.Commit(),
            MaterialOverride = MeshKit.Material(alpha: 0.82f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_water);
    }

    private void BuildForest()
    {
        var trees = _data.Trees.Where(t => _treeMask == null || !_treeMask(new Vector2(t.Position.X, t.Position.Z))).ToArray();
        for (int species = 0; species < 3; species++)
        {
            var group = trees.Where(t => t.Species == species).ToArray();
            var multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = TreeMesh(species),
                InstanceCount = group.Length,
            };
            for (int i = 0; i < group.Length; i++)
            {
                var t = group[i];
                var basis = new Basis(Vector3.Up, t.Rotation).Scaled(Vector3.One * t.Scale);
                multimesh.SetInstanceTransform(i, new Transform3D(basis, t.Position));
                multimesh.SetInstanceColor(i, new Color(t.Tint, t.Tint, t.Tint));
            }

            _forest.AddChild(new MultiMeshInstance3D { Multimesh = multimesh });
        }
    }

    private static ArrayMesh TreeMesh(int species)
    {
        var kit = new MeshKit();
        Cone(kit, Vector3.Zero, 0.65f, 0.32f, 9, 6, new Color("68513a"));
        if (species == 2)
        {
            Cone(kit, new Vector3(0, 5, 0), 4.4f, 0, 8, 7, new Color("3f6350"));
            Cone(kit, new Vector3(0, 9, 0), 3.3f, 0, 7.5f, 7, new Color("4c7457"));
        }
        else
        {
            var leaf = new Color(species == 0 ? "638849" : "82954e");
            Cone(kit, new Vector3(0, 5, 0), 5.2f, 3.4f, 4, 7, leaf);
            Cone(kit, new Vector3(0, 9, 0), 3.4f, 0, 6, 7, leaf.Lightened(0.05f));
        }

        var mesh = kit.Commit()!;
        mesh.SurfaceSetMaterial(0, MeshKit.Material());
        return mesh;
    }

    private static void Cone(MeshKit kit, Vector3 origin, float lower, float upper, float height, int sides, Color color)
    {
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.Tau / sides, b = (i + 1) * Mathf.Tau / sides;
            var p = origin + new Vector3(Mathf.Cos(a) * lower, 0, Mathf.Sin(a) * lower);
            var q = origin + new Vector3(Mathf.Cos(b) * lower, 0, Mathf.Sin(b) * lower);
            var r = origin + new Vector3(Mathf.Cos(a) * upper, height, Mathf.Sin(a) * upper);
            var s = origin + new Vector3(Mathf.Cos(b) * upper, height, Mathf.Sin(b) * upper);
            var outward = new Vector3(Mathf.Cos((a + b) / 2), 0.3f, Mathf.Sin((a + b) / 2));
            kit.Triangle(p, q, r, color, outward);
            if (upper > 0)
                kit.Triangle(q, s, r, color, outward);
        }
    }
}
