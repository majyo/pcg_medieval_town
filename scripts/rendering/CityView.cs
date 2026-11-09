using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Rendering;

public enum CityLayer { Contours, Site, Layout, Roads, Blocks, Parcels, Buildings }

/// <summary>
/// The single preview of the town graph. Each layer draws one or more keys and is rebuilt only when one of those
/// values changes; a stale or missing value simply hides its layer. Which node wrote a key does not matter here.
/// </summary>
public partial class CityView : Node3D
{
    private static readonly Color[] Walls = { new("d9cdb4"), new("cdbb98"), new("e3d9c4"), new("b9a684") };
    private static readonly Color[] Roofs = { new("8c4a36"), new("7a3f2f"), new("9a5a3e"), new("5e5048") };
    private static readonly Color Stone = new("a49c8c"), StoneTop = new("8c8578");

    private readonly Dictionary<CityLayer, Node3D> _layers = new();
    private readonly Dictionary<CityLayer, object?[]> _sources = new();
    private readonly HashSet<CityLayer> _hidden = new() { CityLayer.Contours, CityLayer.Site, CityLayer.Blocks };
    private CityGraph? _graph;
    private float _contourInterval = 2;

    /// <summary>Height between contour lines in meters; every fifth line is drawn wider.</summary>
    public float ContourInterval
    {
        get => _contourInterval;
        set
        {
            if (Mathf.IsEqualApprox(value, _contourInterval))
                return;
            _contourInterval = value;
            _sources[CityLayer.Contours] = Array.Empty<object?>();
            Refresh();
        }
    }

    public void Bind(CityGraph graph)
    {
        _graph = graph;
        foreach (var layer in Enum.GetValues<CityLayer>())
        {
            if (!_layers.ContainsKey(layer))
            {
                _layers[layer] = new Node3D { Name = layer.ToString() };
                AddChild(_layers[layer]);
            }

            _sources[layer] = Array.Empty<object?>();
        }

        Refresh();
    }

    /// <summary>Overlay color of a cell that cannot be built on, for the reason given.</summary>
    public static Color SiteColor(SiteBlock block) => block switch
    {
        SiteBlock.Water => new Color("2f6f9a"),
        SiteBlock.Bank => new Color("5fa8c8"),
        SiteBlock.Slope => new Color("c8553d"),
        _ => new Color("2f6b3a"),
    };

    /// <summary>Center-line color of a road of the given kind.</summary>
    public static Color RoadColor(RoadKind kind) => kind switch
    {
        RoadKind.Main => new Color("7a4e2a"),
        RoadKind.Outer => new Color("6d6450"),
        _ => new Color("8f7454"),
    };

    public bool IsVisible(CityLayer layer) => !_hidden.Contains(layer);

    public void SetVisible(CityLayer layer, bool visible)
    {
        if (visible)
            _hidden.Remove(layer);
        else
            _hidden.Add(layer);
        Refresh();
    }

    public void Refresh()
    {
        if (_graph == null)
            return;

        var height = _graph.Get(Keys.Height);
        var site = _graph.Get(Keys.Site);
        var plaza = _graph.Get(Keys.Plaza);
        var wall = _graph.Get(Keys.Wall);
        var gates = _graph.Get(Keys.Gates);
        var roads = _graph.Get(Keys.Roads);
        var blocks = _graph.Get(Keys.Blocks);
        var parcels = _graph.Get(Keys.Parcels);
        var buildings = _graph.Get(Keys.Buildings);
        Update(CityLayer.Contours, root => BuildContours(root, height!), height);
        Update(CityLayer.Site, root => BuildSite(root, site!), site);
        Update(CityLayer.Layout, root => BuildLayout(root, plaza!, wall!, gates!), plaza, wall, gates);
        Update(CityLayer.Roads, root => BuildRoads(root, roads!, plaza), roads);
        Update(CityLayer.Blocks, root => BuildBlocks(root, blocks!), blocks);
        Update(CityLayer.Parcels, root => BuildParcels(root, parcels!), parcels);
        Update(CityLayer.Buildings, root => BuildBuildings(root, buildings!), buildings);
    }

    /// <summary>Shows the layer when all its keys have values; rebuilds it when any of them changed.</summary>
    private void Update(CityLayer layer, Action<Node3D> build, params object?[] sources)
    {
        var root = _layers[layer];
        root.Visible = sources.All(s => s != null) && !_hidden.Contains(layer);
        var old = _sources[layer];
        if (!root.Visible || old.Length == sources.Length && old.Zip(sources).All(p => ReferenceEquals(p.First, p.Second)))
            return;

        foreach (var child in root.GetChildren())
            child.QueueFree();
        _sources[layer] = sources;
        build(root);
    }

    private float Height(Vector2 p) => _graph!.Terrain.HeightAt(p);

    private static void Add(Node3D root, MeshKit kit, Material material, bool shadows = true)
    {
        var mesh = kit.Commit();
        if (mesh == null)
            return;
        root.AddChild(new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>
    /// Contours of the sampled heights every <see cref="ContourInterval"/>, every fifth wider: flat ribbons lifted just above the
    /// ground at both ends of each segment. Segments are shorter than a cell diagonal, so they follow the ground.
    /// </summary>
    private void BuildContours(Node3D root, ScalarField height)
    {
        float interval = _contourInterval;
        const float lift = 0.4f;
        var segments = Contours.Extract(height, interval);
        var vertices = new Vector3[segments.Count * 4];
        var colors = new Color[segments.Count * 4];
        var indices = new int[segments.Count * 6];
        Color minor = new("4a3b2a"), major = new("2e2418");
        System.Threading.Tasks.Parallel.For(0, segments.Count, i =>
        {
            var s = segments[i];
            bool isMajor = (int)MathF.Round(s.Level / interval) % 5 == 0;
            var along = s.B - s.A;
            var side = along.LengthSquared() < 1e-8f ? Vector2.Zero : along.Normalized().Orthogonal() * (isMajor ? 0.6f : 0.25f);
            Vector3 P(Vector2 p) => new(p.X, Height(p) + lift, p.Y);
            int v = i * 4;
            vertices[v] = P(s.A + side);
            vertices[v + 1] = P(s.B + side);
            vertices[v + 2] = P(s.B - side);
            vertices[v + 3] = P(s.A - side);
            var color = isMajor ? major : minor;
            colors[v] = colors[v + 1] = colors[v + 2] = colors[v + 3] = color;
            int t = i * 6;
            (indices[t], indices[t + 1], indices[t + 2]) = (v, v + 1, v + 2);
            (indices[t + 3], indices[t + 4], indices[t + 5]) = (v, v + 2, v + 3);
        });
        var mesh = MeshKit.Indexed(vertices, colors, indices);
        if (mesh == null)
            return;
        root.AddChild(new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = MeshKit.Material(unshaded: true, alpha: 0.6f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    private void BuildSite(Node3D root, Site site)
    {
        var kit = new MeshKit();
        float half = site.CellSize / 2;
        for (int k = 0; k < site.Count; k++)
        {
            if (site.Block[k] == SiteBlock.None)
                continue;
            var color = SiteColor(site.Block[k]);
            var c = site.Center(k);
            Vector3 P(float dx, float dz) => new(c.X + dx, Height(new Vector2(c.X + dx, c.Y + dz)) + 0.6f, c.Y + dz);
            kit.Quad(P(-half, -half), P(half, -half), P(half, half), P(-half, half), color, Vector3.Up);
        }

        Add(root, kit, MeshKit.Material(unshaded: true, alpha: 0.45f), shadows: false);
    }

    private void BuildLayout(Node3D root, Plaza square, WallRing ring, GateSet gateSet)
    {
        var plaza = new MeshKit();
        plaza.Drape(square.Area.Triangulate(), Height, 0.35f, new Color("cdb995"));
        Add(root, plaza, MeshKit.Material(), shadows: false);

        var wall = new MeshKit();
        var points = ring.Ring;
        var gates = gateSet.Gates.Select(g => g.Position).ToList();
        const float gap = 6, thickness = 2.4f, height = 7;
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            float length = a.DistanceTo(b);
            float start = gates.Contains(a) ? gap : 0;
            float end = length - (gates.Contains(b) ? gap : 0);
            var along = (b - a) / length;
            var side = along.Orthogonal() * thickness / 2;
            for (float t = start; t < end - 0.01f; t += 6)
            {
                var p = a + along * t;
                var q = a + along * Math.Min(end, t + 6);
                float bottom = Math.Min(Height(p), Height(q)) - 1;
                float top = Math.Max(Height(p), Height(q)) + height;
                wall.Prism(new[] { p + side, q + side, q - side, p - side }, bottom, top, Stone, StoneTop);
            }

            if (i % 4 == 0 && !gates.Contains(a))
                Tower(wall, a, along, 5, height + 3);
        }

        foreach (var gate in gateSet.Gates)
        {
            var tangent = gate.Outward.Orthogonal();
            Tower(wall, gate.Position + tangent * (gap + 2.5f), gate.Outward, 6, height + 5);
            Tower(wall, gate.Position - tangent * (gap + 2.5f), gate.Outward, 6, height + 5);
        }

        Add(root, wall, MeshKit.Material());
    }

    private void Tower(MeshKit kit, Vector2 center, Vector2 facing, float size, float height)
    {
        var u = facing.Normalized() * size / 2;
        var v = u.Orthogonal();
        var corners = new[] { center + u + v, center - u + v, center - u - v, center + u - v };
        float ground = corners.Min(Height);
        kit.Prism(corners, ground - 1, corners.Max(Height) + height, Stone.Darkened(0.05f), StoneTop);
    }

    private void BuildRoads(Node3D root, RoadGraph roads, Plaza? plaza)
    {
        var kit = new MeshKit();
        kit.Drape(roads.Footprint.Subtract(plaza?.Area ?? Region.Empty).Triangulate(),
            Height, 0.3f, new Color("a8916c"));
        Add(root, kit, MeshKit.Material(), shadows: false);

        var lines = new MeshKit(Mesh.PrimitiveType.Lines);
        foreach (var edge in roads.Edges)
        {
            var color = RoadColor(edge.Kind);
            for (int i = 1; i < edge.Points.Length; i++)
                DrapedLine(lines, edge.Points[i - 1], edge.Points[i], 0.5f, color);
        }

        Add(root, lines, MeshKit.Material(unshaded: true), shadows: false);
    }

    /// <summary>Each street block a different pale tint, outlined; the developable land they come from shows through.</summary>
    private void BuildBlocks(Node3D root, BlockSet blocks)
    {
        var fill = new MeshKit();
        var lines = new MeshKit(Mesh.PrimitiveType.Lines);
        var tints = new[] { new Color("d7b98a"), new Color("b8c98f"), new Color("9fc2b8"), new Color("c9a6a0"), new Color("c4c48c") };
        for (int b = 0; b < blocks.Blocks.Count; b++)
        {
            var block = blocks.Blocks[b];
            fill.Drape(block.Triangulate(), Height, 0.12f, tints[b % tints.Length]);
            foreach (var ring in block.Rings)
            {
                for (int i = 0; i < ring.Length; i++)
                    DrapedLine(lines, ring[i], ring[(i + 1) % ring.Length], 0.3f, new Color("4a3f2c"));
            }
        }

        Add(root, fill, MeshKit.Material(unshaded: true, alpha: 0.55f), shadows: false);
        Add(root, lines, MeshKit.Material(unshaded: true), shadows: false);
    }

    private void BuildParcels(Node3D root, ParcelSet parcels)
    {
        var fill = new MeshKit();
        var lines = new MeshKit(Mesh.PrimitiveType.Lines);
        var rng = new Random(1);
        foreach (var parcel in parcels.Parcels)
        {
            var region = Region.Polygon(parcel.Ring);
            var color = new Color("98a46c").Lerp(new Color("b3aa78"), rng.NextSingle());
            fill.Drape(region.Triangulate(), Height, 0.15f, color);
            for (int i = 0; i < parcel.Ring.Length; i++)
                DrapedLine(lines, parcel.Ring[i], parcel.Ring[(i + 1) % parcel.Ring.Length], 0.35f, new Color("5c5a3c"));
        }

        Add(root, fill, MeshKit.Material(), shadows: false);
        Add(root, lines, MeshKit.Material(unshaded: true), shadows: false);
    }

    private void BuildBuildings(Node3D root, BuildingSet set)
    {
        var walls = new MeshKit();
        foreach (var b in set.Buildings)
        {
            var wall = Walls[b.Palette];
            var roof = Roofs[b.Palette];
            var c = b.Corners;
            for (int i = 0; i < 4; i++)
            {
                var p = c[i];
                var q = c[(i + 1) % 4];
                var outward = (p + q) / 2 - (c[0] + c[2]) / 2;
                walls.Quad(new(p.X, b.Base, p.Y), new(q.X, b.Base, q.Y), new(q.X, b.Eaves, q.Y), new(p.X, b.Eaves, p.Y),
                    wall, new Vector3(outward.X, 0, outward.Y));
            }

            Vector3 E(Vector2 p) => new(p.X, b.Eaves, p.Y);
            Vector3 R(Vector2 p) => new(p.X, b.Ridge, p.Y);
            Vector3 Out(Vector2 p) => new(p.X - (c[0].X + c[2].X) / 2, 0, p.Y - (c[0].Y + c[2].Y) / 2);
            // Corners: 0 front-left, 1 front-right, 2 back-right, 3 back-left.
            if (b.RidgeToStreet)
            {
                var front = (c[0] + c[1]) / 2;
                var back = (c[2] + c[3]) / 2;
                walls.Quad(E(c[0]), R(front), R(back), E(c[3]), roof, Vector3.Up);
                walls.Quad(E(c[1]), E(c[2]), R(back), R(front), roof, Vector3.Up);
                walls.Triangle(E(c[0]), E(c[1]), R(front), wall, Out(front));
                walls.Triangle(E(c[3]), E(c[2]), R(back), wall, Out(back));
            }
            else
            {
                var left = (c[0] + c[3]) / 2;
                var right = (c[1] + c[2]) / 2;
                walls.Quad(E(c[0]), E(c[1]), R(right), R(left), roof, Vector3.Up);
                walls.Quad(E(c[3]), R(left), R(right), E(c[2]), roof, Vector3.Up);
                walls.Triangle(E(c[0]), E(c[3]), R(left), wall, Out(left));
                walls.Triangle(E(c[1]), E(c[2]), R(right), wall, Out(right));
            }
        }

        Add(root, walls, MeshKit.Material());
    }

    private void DrapedLine(MeshKit kit, Vector2 a, Vector2 b, float lift, Color color)
    {
        int steps = Math.Max(1, (int)(a.DistanceTo(b) / 1.5f));
        for (int i = 0; i < steps; i++)
        {
            var p = a.Lerp(b, i / (float)steps);
            var q = a.Lerp(b, (i + 1) / (float)steps);
            kit.Line(new Vector3(p.X, Height(p) + lift, p.Y), new Vector3(q.X, Height(q) + lift, q.Y), color);
        }
    }
}
