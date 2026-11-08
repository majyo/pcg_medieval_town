using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Rendering;

public enum CityLayer { Site, Layout, Roads, Parcels, Buildings }

/// <summary>
/// The single preview for every stage. Each layer draws one stage result and is rebuilt only when that
/// result object changes; stale or missing results simply hide their layer.
/// </summary>
public partial class CityView : Node3D
{
    private static readonly Color[] Walls = { new("d9cdb4"), new("cdbb98"), new("e3d9c4"), new("b9a684") };
    private static readonly Color[] Roofs = { new("8c4a36"), new("7a3f2f"), new("9a5a3e"), new("5e5048") };
    private static readonly Color Stone = new("a49c8c"), StoneTop = new("8c8578");

    private readonly Dictionary<CityLayer, Node3D> _layers = new();
    private readonly Dictionary<CityLayer, object?> _sources = new();
    private readonly HashSet<CityLayer> _hidden = new() { CityLayer.Site };
    private Pipeline? _pipeline;

    public void Bind(Pipeline pipeline)
    {
        _pipeline = pipeline;
        foreach (var layer in Enum.GetValues<CityLayer>())
        {
            if (!_layers.ContainsKey(layer))
            {
                _layers[layer] = new Node3D { Name = layer.ToString() };
                AddChild(_layers[layer]);
            }

            _sources[layer] = null;
        }

        Refresh();
    }

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
        if (_pipeline == null)
            return;

        Update(CityLayer.Site, _pipeline.Get<Site>(), BuildSite);
        Update(CityLayer.Layout, _pipeline.Get<TownLayout>(), BuildLayout);
        Update(CityLayer.Roads, _pipeline.Get<RoadGraph>(), BuildRoads);
        Update(CityLayer.Parcels, _pipeline.Get<ParcelSet>(), BuildParcels);
        Update(CityLayer.Buildings, _pipeline.Get<BuildingSet>(), BuildBuildings);
    }

    private void Update<T>(CityLayer layer, T? result, Action<Node3D, T> build) where T : class
    {
        var root = _layers[layer];
        root.Visible = result != null && !_hidden.Contains(layer);
        if (!root.Visible || ReferenceEquals(_sources[layer], result))
            return;

        foreach (var child in root.GetChildren())
            child.QueueFree();
        _sources[layer] = result;
        build(root, result!);
    }

    private float Height(Vector2 p) => _pipeline!.Terrain.HeightAt(p);

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

    private void BuildSite(Node3D root, Site site)
    {
        var kit = new MeshKit();
        float half = site.CellSize / 2;
        for (int k = 0; k < site.Count; k++)
        {
            var color = site.Block[k] switch
            {
                SiteBlock.Water => new Color("2f6f9a"),
                SiteBlock.Bank => new Color("5fa8c8"),
                SiteBlock.Slope => new Color("c8553d"),
                SiteBlock.Forest => new Color("2f6b3a"),
                _ => (Color?)null,
            };
            if (color == null)
                continue;
            var c = site.Center(k);
            Vector3 P(float dx, float dz) => new(c.X + dx, Height(new Vector2(c.X + dx, c.Y + dz)) + 0.6f, c.Y + dz);
            kit.Quad(P(-half, -half), P(half, -half), P(half, half), P(-half, half), color.Value, Vector3.Up);
        }

        Add(root, kit, MeshKit.Material(unshaded: true, alpha: 0.45f), shadows: false);
    }

    private void BuildLayout(Node3D root, TownLayout layout)
    {
        var plaza = new MeshKit();
        plaza.Drape(layout.Plaza.Triangulate(), Height, 0.35f, new Color("cdb995"));
        Add(root, plaza, MeshKit.Material(), shadows: false);

        var wall = new MeshKit();
        var ring = layout.Wall;
        var gates = layout.Gates.Select(g => g.Position).ToList();
        const float gap = 6, thickness = 2.4f, height = 7;
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
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

        foreach (var gate in layout.Gates)
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

    private void BuildRoads(Node3D root, RoadGraph roads)
    {
        var kit = new MeshKit();
        kit.Drape(roads.Footprint.Subtract(_pipeline!.Get<TownLayout>()?.Plaza ?? Region.Empty).Triangulate(),
            Height, 0.3f, new Color("a8916c"));
        Add(root, kit, MeshKit.Material(), shadows: false);

        var lines = new MeshKit(Mesh.PrimitiveType.Lines);
        foreach (var edge in roads.Edges)
        {
            var color = edge.Kind switch
            {
                RoadKind.Main => new Color("7a4e2a"),
                RoadKind.Outer => new Color("6d6450"),
                _ => new Color("8f7454"),
            };
            for (int i = 1; i < edge.Points.Length; i++)
                DrapedLine(lines, edge.Points[i - 1], edge.Points[i], 0.5f, color);
        }

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
            fill.Drape(region.Triangulate(), Height, 0.15f, color, 5);
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
        int steps = Math.Max(1, (int)(a.DistanceTo(b) / 4));
        for (int i = 0; i < steps; i++)
        {
            var p = a.Lerp(b, i / (float)steps);
            var q = a.Lerp(b, (i + 1) / (float)steps);
            kit.Line(new Vector3(p.X, Height(p) + lift, p.Y), new Vector3(q.X, Height(q) + lift, q.Y), color);
        }
    }
}
