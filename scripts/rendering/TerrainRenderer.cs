using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.Rendering;

public enum TerrainView { Landscape, Elevation, Slope, Suitability }

/// <summary>
/// Ground (Terrain3D via <see cref="GroundSurface"/>), map skirt, river surface and simple trees.
/// </summary>
public partial class TerrainRenderer : Node3D
{
    private TemperateMapData _data = null!;
    private GroundSurface _ground = null!;
    private MeshInstance3D _water = null!;
    private Node3D _forest = new() { Name = "Forest" };
    private Func<Vector2, bool>? _treeMask;

    public TerrainView View { get; private set; }

    public void Build(TemperateMapData data)
    {
        _data = data;
        _ground = new GroundSurface { Name = "Ground" };
        AddChild(_ground);
        _ground.Build(data, GroundMasks);
        BuildSkirt();
        BuildWater();
        AddChild(_forest);
        BuildForest();
    }

    public void SetView(TerrainView view)
    {
        if (view == View)
            return;
        View = view;
        if (view == TerrainView.Landscape)
            _ground.SetMasks(GroundMasks);
        else
            _ground.SetColors(i => GroundColor(view, i));
    }

    /// <summary>Centers the ground's finest level of detail on <paramref name="focus"/>.</summary>
    public void Follow(Vector3 focus) => _ground.Follow(focus);

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

    /// <summary>
    /// What the painterly ground shader needs at a point: R = forest cover, G = height above the river surface
    /// (0 at the water, 1 from 3 m up), B = elevation relative to the map's range.
    /// </summary>
    private Color GroundMasks(Vector2 p)
    {
        var d = _data;
        var sample = d.Sample(p);
        var river = d.NearestRiver(p, 120);
        float aboveWater = float.IsPositiveInfinity(river.Distance) ? 1 : Mathf.Clamp((sample.Height - river.WaterHeight) / 3, 0, 1);
        return new Color(sample.Forest, aboveWater, Mathf.InverseLerp(d.MinHeight, d.MaxHeight, sample.Height));
    }

    /// <summary>Analysis-view color at map grid vertex <paramref name="i"/>.</summary>
    private Color GroundColor(TerrainView view, int i)
    {
        var d = _data;
        float h = Mathf.InverseLerp(d.MinHeight, d.MaxHeight, d.Heights[i]);
        return view switch
        {
            TerrainView.Elevation => new Color("315e70").Lerp(new Color("92b78e"), Mathf.Clamp(h * 2, 0, 1))
                .Lerp(new Color("e6d3a0"), Mathf.Clamp((h - 0.5f) * 2, 0, 1)),
            TerrainView.Slope => new Color("7db39e").Lerp(new Color("e8c36f"), Mathf.Clamp(d.Slopes[i] / 8, 0, 1))
                .Lerp(new Color("be6251"), Mathf.Clamp((d.Slopes[i] - 8) / 12, 0, 1)),
            TerrainView.Suitability => d.Water[i] ? new Color("315f74")
                : new Color("ad7860").Lerp(new Color("90bc84"), d.Suitability[i]),
            _ => throw new ArgumentOutOfRangeException(nameof(view), "地表视图由着色器按遮罩绘制。"),
        };
    }

    private void BuildSkirt()
    {
        // Follow the height grid's edge vertex by vertex so the skirt meets the ground exactly.
        var kit = new MeshKit();
        int n = _data.HeightSide - 1;
        Vector3 Edge(int x, int z)
        {
            var p = _data.HeightPosition(x, z);
            return new Vector3(p.X, _data.HeightGrid[_data.HeightIndex(x, z)], p.Y);
        }

        var edge = new List<Vector3>();
        for (int i = 0; i < n; i++)
            edge.Add(Edge(i, 0));
        for (int i = 0; i < n; i++)
            edge.Add(Edge(n, i));
        for (int i = n; i > 0; i--)
            edge.Add(Edge(i, n));
        for (int i = n; i > 0; i--)
            edge.Add(Edge(0, i));
        for (int i = 0; i < edge.Count; i++)
        {
            var a = edge[i];
            var b = edge[(i + 1) % edge.Count];
            kit.Quad(a, b, new Vector3(b.X, -22, b.Z), new Vector3(a.X, -22, a.Z), new Color("6b6a55"),
                new Vector3(a.X, 0, a.Z));
        }

        // One-sided, seen only from outside: the ground leaves a thin gap at the map edge that would otherwise
        // show the skirt's inner face.
        var material = MeshKit.Material(cull: BaseMaterial3D.CullModeEnum.Front);
        AddChild(new MeshInstance3D { Name = "Skirt", Mesh = kit.Commit(), MaterialOverride = material });
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
