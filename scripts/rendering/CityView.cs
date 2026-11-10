using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Rendering;

/// <summary>Named sets of keys the result view can show or hide together.</summary>
public enum CityLayer { Contours, Region, Layout, Roads, Blocks, Parcels, Buildings }

/// <summary>
/// One value to draw: its key, the version shown and, for a node that edits the key, the version it started from.
/// <paramref name="Detail"/> adds what only matters when inspecting a node (e.g. which entry each gate leads to).
/// </summary>
public sealed record Shown(IKey Key, object Value, object? Previous = null, bool Detail = false);

/// <summary>
/// Draws town graph values over the terrain, one key at a time. Every key that is not a field painted on the ground
/// has a view here; <see cref="Show"/> displays exactly the values it is given (the outputs of one node, or the final
/// town) and rebuilds a key's mesh only when its value changes. Which node wrote a value does not matter here.
/// </summary>
public partial class CityView : Node3D
{
    private static readonly Color[] Walls = { new("d9cdb4"), new("cdbb98"), new("e3d9c4"), new("b9a684") };
    private static readonly Color[] Roofs = { new("8c4a36"), new("7a3f2f"), new("9a5a3e"), new("5e5048") };
    private static readonly Color Stone = new("a49c8c"), StoneTop = new("8c8578");
    private static readonly object DetailMark = new();
    private static readonly Color PlazaColor = new("cdb995"), InheritedColor = new("8d897d");
    private static readonly Color ChurchyardColor = new("a9b98a"), ChurchWall = new("e6dcc6"), ChurchRoof = new("5e5048");

    /// <summary>Colors of the regional frame: through routes, crossings, map entries and the town's origin.</summary>
    public static readonly Color RouteColor = new("8a5a32"), CrossingColor = new("5a3b22"), EntryColor = new("b5523b"),
        OriginColor = new("e0a72e");

    private readonly Dictionary<IKey, View> _views;
    private readonly Dictionary<IKey, Node3D> _roots = new();
    private readonly Dictionary<IKey, object?[]> _built = new();
    private IReadOnlyList<Shown> _shown = Array.Empty<Shown>();
    private CityGraph? _graph;
    private float _contourInterval = 2;

    public CityView()
    {
        _views = new Dictionary<IKey, View>
        {
            [Keys.Height] = new((root, s, _) => BuildContours(root, (ScalarField)s.Value),
                _ => new FieldLegend($"等高线 · 每 {_contourInterval:0.#} m，每 5 条加粗", null, "", "", null)),
            [Keys.Crossings] = new(
                (root, s, other) => BuildCrossings(root, (CrossingSet)s.Value,
                    other(Keys.Routes) as RouteNetwork ?? _graph!.Final(Keys.Routes) as RouteNetwork, s.Detail),
                s => Categories("渡口", (CrossingColor, s.Detail ? "渡口（细线：过境道路没有用到）" : "桥")),
                Uses: [Keys.Routes], Context: [Keys.Routes]),
            [Keys.Routes] = new((root, s, _) => BuildRoutes(root, (RouteNetwork)s.Value),
                _ => Categories("过境道路", (RouteColor, "过境道路"), (EntryColor, "图边入口"))),
            [Keys.Origin] = new((root, s, _) => BuildOrigin(root, (TownOrigin)s.Value, s.Detail),
                _ => Categories("起源点", (OriginColor, "起源点"))),
            [Keys.Plaza] = new((root, s, _) => BuildPlaza(root, (Plaza)s.Value),
                _ => Categories("市场", (PlazaColor, "市场"))),
            [Keys.Landmarks] = new((root, s, _) => BuildLandmarks(root, (LandmarkSet)s.Value),
                _ => Categories("地标", (ChurchyardColor, "教堂墓园用地"), (ChurchRoof, "教堂（钟塔在西端，祭坛朝东）"))),
            [Keys.Wall] = new((root, s, other) => BuildWall(root, (WallRing)s.Value, other(Keys.Gates) as GateSet),
                _ => Categories("城墙", (Stone, "城墙与墙塔")), Uses: [Keys.Gates]),
            [Keys.Gates] = new((root, s, _) => BuildGates(root, (GateSet)s.Value, s.Detail),
                s => s.Detail ? Categories("城门", (EntryColor, "城门，细线连向它的图边入口")) : null),
            [Keys.RoadLinks] = new((root, s, _) => BuildLinks(root, (RoadLinks)s.Value, s.Previous as RoadLinks),
                LinksLegend, Context: [Keys.Grid]),
            [Keys.Roads] = new((root, s, other) => BuildRoads(root, (RoadGraph)s.Value, other(Keys.Plaza) as Plaza),
                _ => Categories("道路", (RoadColor(RoadKind.Main), "主街"), (RoadColor(RoadKind.Street), "街巷"),
                    (RoadColor(RoadKind.Outer), "城外道路"), (RoadColor(RoadKind.Track), "便道（通往作坊的土路）")),
                Uses: [Keys.Plaza]),
            [Keys.Blocks] = new((root, s, _) => BuildBlocks(root, (BlockSet)s.Value),
                _ => new FieldLegend("街区 · 每块一种颜色", null, "", "", null)),
            [Keys.Parcels] = new((root, s, _) => BuildParcels(root, (ParcelSet)s.Value),
                s => Categories("地块 · 按用途", ((ParcelSet)s.Value).Parcels.Select(p => p.Use).Distinct().OrderBy(u => u)
                    .Select(u => (FieldStyles.LandColors[(int)u], u.Label())).ToArray())),
            [Keys.Buildings] = new((root, s, _) => BuildBuildings(root, (BuildingSet)s.Value), _ => null),
        };
    }

    /// <summary>Height between contour lines in meters; every fifth line is drawn wider.</summary>
    public float ContourInterval
    {
        get => _contourInterval;
        set
        {
            if (Mathf.IsEqualApprox(value, _contourInterval))
                return;
            _contourInterval = value;
            _built.Remove(Keys.Height);
            Show(_shown);
        }
    }

    /// <summary>The keys a result-view layer stands for.</summary>
    public static IReadOnlyList<IKey> LayerKeys(CityLayer layer) => layer switch
    {
        CityLayer.Contours => [Keys.Height],
        CityLayer.Region => [Keys.Crossings, Keys.Routes, Keys.Origin],
        CityLayer.Layout => [Keys.Plaza, Keys.Landmarks, Keys.Wall, Keys.Gates],
        CityLayer.Roads => [Keys.Roads, Keys.Crossings],
        CityLayer.Blocks => [Keys.Blocks],
        CityLayer.Parcels => [Keys.Parcels],
        _ => [Keys.Buildings],
    };

    /// <summary>Whether the key is drawn here (otherwise it can only be painted on the ground, or not shown at all).</summary>
    public bool Draws(IKey key) => _views.ContainsKey(key);

    /// <summary>Center-line color of a road of the given kind.</summary>
    public static Color RoadColor(RoadKind kind) => kind switch
    {
        RoadKind.Main => new Color("c0392b"),
        RoadKind.Outer => new Color("2e86c1"),
        RoadKind.Track => new Color("7d7a4e"),
        _ => new Color("8f6a3a"),
    };

    public void Bind(CityGraph graph)
    {
        _graph = graph;
        foreach (var key in _views.Keys)
        {
            if (!_roots.ContainsKey(key))
            {
                _roots[key] = new Node3D { Name = key.Name.Replace('.', '_') };
                AddChild(_roots[key]);
            }
        }

        _built.Clear();
        Show(_shown);
    }

    /// <summary>Draws exactly these values (keys without a view here are ignored) and hides everything else.</summary>
    public void Show(IReadOnlyList<Shown> shown)
    {
        _shown = shown;
        if (_graph == null)
            return;
        var lookup = shown.Where(s => Draws(s.Key)).ToDictionary(s => s.Key, s => s.Value);
        object? Other(IKey key) => lookup.TryGetValue(key, out var value) ? value : null;
        foreach (var (key, view) in _views)
        {
            var root = _roots[key];
            var item = shown.FirstOrDefault(s => s.Key == key);
            root.Visible = item != null;
            if (item == null)
                continue;
            var sources = new[] { item.Value, item.Previous, item.Detail ? DetailMark : null }.Concat(view.Uses.Select(Other))
                .Concat(view.Context.Select(_graph.Final)).ToArray();
            bool same = _built.TryGetValue(key, out var old) && old.Length == sources.Length &&
                old.Zip(sources).All(p => ReferenceEquals(p.First, p.Second));
            if (same)
                continue;
            foreach (var child in root.GetChildren())
                child.QueueFree();
            _built[key] = sources;
            view.Build(root, item, Other);
        }
    }

    /// <summary>What the colors of a drawn value mean, or null when it needs no explanation.</summary>
    public FieldLegend? Legend(Shown shown) => _views.TryGetValue(shown.Key, out var view) ? view.Legend(shown) : null;

    private static FieldLegend Categories(string caption, params (Color, string)[] categories) =>
        new(caption, null, "", "", categories);

    private sealed record View(Action<Node3D, Shown, Func<IKey, object?>> Build, Func<Shown, FieldLegend?> Legend,
        IKey[]? Uses = null, IKey[]? Context = null)
    {
        /// <summary>Other shown keys the drawing depends on (e.g. the wall leaves gaps at shown gates).</summary>
        public IKey[] Uses { get; } = Uses ?? Array.Empty<IKey>();

        /// <summary>Final values the drawing needs whether shown or not (e.g. the grid that link cells refer to).</summary>
        public IKey[] Context { get; } = Context ?? Array.Empty<IKey>();
    }

    private float Height(Vector2 p) => _graph!.Terrain.HeightAt(p);

    private static void Add(Node3D root, MeshKit kit, Material material, bool shadows = true) =>
        Add(root, kit.Commit(), material, shadows);

    private static void Add(Node3D root, ArrayMesh? mesh, Material material, bool shadows = true)
    {
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
        Add(root, MeshKit.Indexed(vertices, colors, indices), MeshKit.Material(unshaded: true, alpha: 0.6f), shadows: false);
    }

    /// <summary>
    /// A plank deck as wide as a road at each crossing a through route uses. When inspecting, the other crossings get a
    /// thin outline and every crossing a ring, so they can be found on the whole map; in the result they are left out.
    /// </summary>
    private void BuildCrossings(Node3D root, CrossingSet crossingSet, RouteNetwork? network, bool detail)
    {
        if (detail)
            Rings(root, crossingSet.Crossings.Select(c => c.Center), 40, CrossingColor);
        const float deckWidth = 6;
        var used = network?.UsedCrossings.ToHashSet();
        var solids = new MeshKit();
        foreach (var crossing in crossingSet.Crossings)
        {
            bool inUse = used == null || used.Contains(crossing.Id);
            if (!inUse && !detail)
                continue;
            var along = (crossing.BankB - crossing.BankA).Normalized();
            var side = along.Orthogonal() * (inUse ? deckWidth : 1.5f) / 2;
            float deck = Math.Max(Height(crossing.BankA), Height(crossing.BankB)) + 0.4f;
            var corners = new[] { crossing.BankA + side, crossing.BankB + side, crossing.BankB - side, crossing.BankA - side };
            solids.Prism(corners, deck - 0.8f, deck, CrossingColor, CrossingColor.Lightened(0.2f));
        }

        Add(root, solids, MeshKit.Material());
    }

    /// <summary>Wide route ribbons on the ground, drawn to read from the whole map, and a post at each map-edge entry.</summary>
    private void BuildRoutes(Node3D root, RouteNetwork network)
    {
        const float width = 7;
        var ribbons = new MeshKit();
        ribbons.Drape(Region.Lines(network.Routes.Select(r => ((IReadOnlyList<Vector2>)r.Points, width))).Triangulate(),
            Height, 0.6f, RouteColor, maxEdge: 4);
        Add(root, ribbons, MeshKit.Material(unshaded: true), shadows: false);
        var posts = new MeshKit();
        foreach (var entry in network.Entries)
            Mast(posts, entry.Position, 4, 14, EntryColor);
        Add(root, posts, MeshKit.Material());
    }

    private void BuildOrigin(Node3D root, TownOrigin origin, bool detail)
    {
        if (detail)
            Rings(root, new[] { origin.Position }, 50, OriginColor);
        var mast = new MeshKit();
        Mast(mast, origin.Position, 6, 32, OriginColor);
        Add(root, mast, MeshKit.Material());
    }

    /// <summary>A ground ring of the given radius around each point, wide enough to see from the whole-map view.</summary>
    private void Rings(Node3D root, IEnumerable<Vector2> centers, float radius, Color color)
    {
        var lines = centers.Select(c => ((IReadOnlyList<Vector2>)Enumerable.Range(0, 49)
            .Select(i => c + Vector2.FromAngle(i * MathF.Tau / 48) * radius).ToArray(), 6f));
        var kit = new MeshKit();
        kit.Drape(Region.Lines(lines).Triangulate(), Height, 0.8f, color, maxEdge: 4);
        Add(root, kit, MeshKit.Material(unshaded: true), shadows: false);
    }

    private void Mast(MeshKit kit, Vector2 at, float size, float height, Color color)
    {
        float h = size / 2;
        var corners = new[] { at + new Vector2(-h, -h), at + new Vector2(h, -h), at + new Vector2(h, h), at + new Vector2(-h, h) };
        float ground = corners.Min(Height);
        kit.Prism(corners, ground - 1, ground + height, color, color.Lightened(0.25f));
    }

    private void BuildPlaza(Node3D root, Plaza square)
    {
        var plaza = new MeshKit();
        plaza.Drape(square.Area.Triangulate(), Height, 0.35f, PlazaColor);
        Add(root, plaza, MeshKit.Material(), shadows: false);
    }

    /// <summary>
    /// Each landmark plot as a green churchyard with its church: a gabled nave along the plot's axis, the altar end
    /// toward <see cref="Landmark.Axis"/>, and a bell tower at the other end.
    /// </summary>
    private void BuildLandmarks(Node3D root, LandmarkSet set)
    {
        var yard = new MeshKit();
        var church = new MeshKit();
        foreach (var landmark in set.Landmarks)
        {
            yard.Drape(landmark.Plot.Triangulate(), Height, 0.3f, ChurchyardColor);
            var size = landmark.Plot.Bounds.Size;
            bool alongX = MathF.Abs(landmark.Axis.X) >= MathF.Abs(landmark.Axis.Y);
            float length = (alongX ? size.X : size.Y) * 0.62f, width = MathF.Min((alongX ? size.Y : size.X) * 0.45f, 12);
            var u = landmark.Axis.Normalized();
            var v = u.Orthogonal();
            var nave = landmark.Center + u * length * 0.08f;
            var corners = new[]
            {
                nave - u * length / 2 - v * width / 2, nave + u * length / 2 - v * width / 2,
                nave + u * length / 2 + v * width / 2, nave - u * length / 2 + v * width / 2,
            };
            float ground = corners.Min(Height), eaves = corners.Max(Height) + 10, ridge = eaves + width * 0.6f;
            church.Prism(corners, ground - 1, eaves, ChurchWall, ChurchWall);
            Vector3 E(Vector2 p) => new(p.X, eaves, p.Y);
            Vector3 R(Vector2 p) => new(p.X, ridge, p.Y);
            Vector2 west = nave - u * length / 2, east = nave + u * length / 2;
            church.Quad(E(corners[0]), E(corners[1]), R(east), R(west), ChurchRoof, Vector3.Up);
            church.Quad(E(corners[2]), E(corners[3]), R(west), R(east), ChurchRoof, Vector3.Up);
            church.Triangle(E(corners[1]), E(corners[2]), R(east), ChurchWall, new Vector3(u.X, 0, u.Y));
            church.Triangle(E(corners[3]), E(corners[0]), R(west), ChurchWall, new Vector3(-u.X, 0, -u.Y));
            float tower = width * 0.85f;
            var foot = west - u * (tower / 2 - 1);
            var towerCorners = new[]
            {
                foot - u * tower / 2 - v * tower / 2, foot + u * tower / 2 - v * tower / 2,
                foot + u * tower / 2 + v * tower / 2, foot - u * tower / 2 + v * tower / 2,
            };
            church.Prism(towerCorners, towerCorners.Min(Height) - 1, ridge + 12, Stone, ChurchRoof);
        }

        Add(root, yard, MeshKit.Material(), shadows: false);
        Add(root, church, MeshKit.Material());
    }

    /// <summary>The wall with a tower every fourth ring point; where gates are shown too, it leaves a gap at each.</summary>
    private void BuildWall(Node3D root, WallRing ring, GateSet? gateSet)
    {
        var wall = new MeshKit();
        var points = ring.Ring;
        var gates = gateSet?.Gates.Select(g => g.Position).ToList() ?? new List<Vector2>();
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

        Add(root, wall, MeshKit.Material());
    }

    /// <summary>
    /// Two towers flanking each gate; in detail, also a post at the gate and a thin line to the map-edge entry its
    /// outer road aims for.
    /// </summary>
    private void BuildGates(Node3D root, GateSet gateSet, bool detail)
    {
        const float gap = 6, height = 7;
        var towers = new MeshKit();
        var lines = new MeshKit(Mesh.PrimitiveType.Lines);
        foreach (var gate in gateSet.Gates)
        {
            var tangent = gate.Outward.Orthogonal();
            Tower(towers, gate.Position + tangent * (gap + 2.5f), gate.Outward, 6, height + 5);
            Tower(towers, gate.Position - tangent * (gap + 2.5f), gate.Outward, 6, height + 5);
            if (!detail)
                continue;
            Mast(towers, gate.Position, 2, height + 9, EntryColor);
            DrapedLine(lines, gate.Position, gate.Entry, 1.5f, EntryColor);
        }

        Add(root, towers, MeshKit.Material());
        Add(root, lines, MeshKit.Material(unshaded: true), shadows: false);
    }

    private void Tower(MeshKit kit, Vector2 center, Vector2 facing, float size, float height)
    {
        var u = facing.Normalized() * size / 2;
        var v = u.Orthogonal();
        var corners = new[] { center + u + v, center - u + v, center - u - v, center + u - v };
        float ground = corners.Min(Height);
        kit.Prism(corners, ground - 1, corners.Max(Height) + height, Stone.Darkened(0.05f), StoneTop);
    }

    /// <summary>
    /// Each grid link as a short ribbon between cell centers, colored by road class. When the value is an edit of
    /// <paramref name="previous"/>, the links already there are gray and only the ones this node added are colored.
    /// </summary>
    private void BuildLinks(Node3D root, RoadLinks links, RoadLinks? previous)
    {
        if (_graph!.Final(Keys.Grid) is not SiteGrid grid)
            return;
        var kit = new MeshKit();
        foreach (var ((a, b), kind) in links.Links)
        {
            bool inherited = previous != null && previous.TryGet(a, b, out _);
            var p = grid.Center(a);
            var q = grid.Center(b);
            var side = (q - p).Normalized().Orthogonal() * (inherited ? 1.2f : 2.2f);
            float lift = inherited ? 0.5f : 0.7f;
            Vector3 P(Vector2 v) => new(v.X, Height(v) + lift, v.Y);
            kit.Quad(P(p + side), P(q + side), P(q - side), P(p - side), inherited ? InheritedColor : RoadColor(kind), Vector3.Up);
        }

        Add(root, kit, MeshKit.Material(unshaded: true), shadows: false);
    }

    private FieldLegend LinksLegend(Shown shown)
    {
        var categories = new List<(Color, string)>
        {
            (RoadColor(RoadKind.Main), "主街"), (RoadColor(RoadKind.Outer), "城外道路"), (RoadColor(RoadKind.Street), "街巷"),
            (RoadColor(RoadKind.Track), "便道"),
        };
        if (shown.Previous != null)
            categories.Add((InheritedColor, "上游已有的连线"));
        return new FieldLegend(shown.Previous != null ? "道路连线 · 彩色为本节点新加的" : "道路连线", null, "", "", categories);
    }

    private void BuildRoads(Node3D root, RoadGraph roads, Plaza? plaza)
    {
        var kit = new MeshKit();
        // Tracks are dirt, drawn darker; where one meets a paved road the paving wins.
        Region Bands(Func<RoadEdge, bool> which) =>
            Region.Lines(roads.Edges.Where(which).Select(e => ((IReadOnlyList<Vector2>)e.Points, e.Width)));
        var surface = roads.Footprint.Subtract(plaza?.Area ?? Region.Empty);
        var dirt = surface.Intersect(Bands(e => e.Kind == RoadKind.Track)).Subtract(Bands(e => e.Kind != RoadKind.Track));
        kit.Drape(surface.Subtract(dirt).Triangulate(), Height, 0.3f, new Color("a8916c"));
        kit.Drape(dirt.Triangulate(), Height, 0.3f, new Color("8a7b5c"));
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
            var color = FieldStyles.LandColors[(int)parcel.Use].Lerp(new Color("f2ecd8"), 0.1f + 0.25f * rng.NextSingle());
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
