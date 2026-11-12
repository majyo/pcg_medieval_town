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
    private static readonly Color CastleYard = new("b9ad96"), HallWall = new("e3cfa2"), FriaryYard = new("b6c493");
    private static readonly Color LeperYard = new("a3b47e"), Hedge = new("5f7a3f"), Mound = new("8f7a5a"), Timber = new("4a3a2c");

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
                s => LandmarkLegend((LandmarkSet)s.Value)),
            [Keys.Wall] = new((root, s, other) => BuildWall(root, (WallRing)s.Value, other(Keys.Gates) as GateSet,
                    other(Keys.Landmarks) as LandmarkSet),
                _ => Categories("城墙", (Stone, "城墙与墙塔")), Uses: [Keys.Gates, Keys.Landmarks]),
            [Keys.Gates] = new((root, s, _) => BuildGates(root, (GateSet)s.Value, s.Detail),
                s => s.Detail ? Categories("城门", (EntryColor, "城门，细线连向它的图边入口")) : null),
            [Keys.RoadLinks] = new((root, s, _) => BuildLinks(root, (RoadLinks)s.Value, s.Previous as RoadLinks),
                LinksLegend, Context: [Keys.Grid]),
            [Keys.Roads] = new((root, s, other) => BuildRoads(root, (RoadGraph)s.Value, other(Keys.Plaza) as Plaza),
                _ => Categories("道路", (RoadColor(RoadKind.Main), "主街"), (RoadColor(RoadKind.Street), "次街"),
                    (RoadColor(RoadKind.Lane), "巷（尽头路和它的分叉）"), (RoadColor(RoadKind.Outer), "城外道路"),
                    (RoadColor(RoadKind.Track), "便道（通往作坊和城外地标的土路）")),
                Uses: [Keys.Plaza]),
            [Keys.StreetPlan] = new((root, s, _) => BuildPlan(root, (StreetPlan)s.Value),
                _ => Categories("规划街巷", (PlanTint, "规划区（三期、四期成片的地）"), (GuideColor, "引导线（过境道路）"),
                    (RoadColor(RoadKind.Street), "平行街"), (CrossColor, "横街"))),
            [Keys.Squares] = new((root, s, _) => BuildSquares(root, (SquareSet)s.Value),
                _ => Categories("小广场", (PlazaColor, "门内广场、教堂前庭、城堡门前"))),
            [Keys.Blocks] = new((root, s, _) => BuildBlocks(root, (BlockSet)s.Value),
                _ => new FieldLegend("街区 · 每块一种颜色", null, "", "", null)),
            [Keys.Parcels] = new((root, s, _) => BuildParcels(root, (ParcelSet)s.Value),
                s => Categories("地块 · 按用途", ((ParcelSet)s.Value).Parcels.Select(p => p.Use).Distinct().OrderBy(u => u)
                    .Select(u => (FieldStyles.LandColors[(int)u], u.Label()))
                    .Concat(((ParcelSet)s.Value).Parcels.Any(p => p.Kind == ParcelKind.Corner)
                        ? [(CornerColor, "街角地块（侧街边加画一道）"), (CourtColor, "客栈大院")] : Array.Empty<(Color, string)>())
                    .ToArray())),
            [Keys.Bridges] = new((root, s, _) => BuildBridges(root, (BridgeSet)s.Value),
                _ => Categories("桥", (CrossingColor, "桥面"), (Stone, "桥台与桥墩"))),
            [Keys.Terraces] = new((root, s, _) => BuildTerraces(root, (TerraceSet)s.Value),
                _ => Categories("台地", (TerraceColors[0], "台地的水平部分（按高度每 0.5 m 换一色，画在台地的高度上）"),
                    (TerraceReach, "地块可以延伸到的后院（细线）"), (SteepColor, "超过上限的台阶"))),
            [Keys.Grading] = new((root, s, _) => BuildGrading(root, (GradePlan)s.Value, s.Detail),
                s => s.Detail ? Categories("修整", (SteepColor, "陡坡段（压到坡度上限以内要挖填太深，保留原坡）"),
                    (Stone, "挡土墙")) : null),
            [Keys.Buildings] = new((root, s, _) => BuildBuildings(root, (BuildingSet)s.Value), _ => null),
            [Keys.Fixtures] = new((root, s, _) => BuildFixtures(root, (FixtureSet)s.Value),
                _ => Categories("公共设施", (Stone, "水井（石砌井台）"))),
            [Keys.Yards] = new((root, s, _) => BuildYards(root, (YardSet)s.Value),
                _ => Categories("院落", (Stone, "院墙（城内）"), (HedgeGreen, "篱笆（城外）"), (ShedWall, "后屋"),
                    (GardenGreen, "菜园"))),
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
        CityLayer.Roads => [Keys.Roads, Keys.Bridges],
        CityLayer.Blocks => [Keys.Blocks],
        CityLayer.Parcels => [Keys.Parcels],
        _ => [Keys.Buildings, Keys.Yards, Keys.Fixtures, Keys.Grading],
    };

    /// <summary>Whether the key is drawn here (otherwise it can only be painted on the ground, or not shown at all).</summary>
    public bool Draws(IKey key) => _views.ContainsKey(key);

    /// <summary>Center-line color of a road of the given kind.</summary>
    public static Color RoadColor(RoadKind kind) => kind switch
    {
        RoadKind.Main => new Color("c0392b"),
        RoadKind.Outer => new Color("2e86c1"),
        RoadKind.Lane => new Color("d0a868"),
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

    private Func<Vector2, float>? _ground;

    /// <summary>
    /// The ground everything here is draped on and stands on; null for the graph's terrain. Changing it redraws every
    /// value.
    /// </summary>
    public Func<Vector2, float>? Ground
    {
        get => _ground;
        set
        {
            _ground = value;
            _built.Clear();
            Show(_shown);
        }
    }

    private static readonly Color SteepColor = new("c0392b");

    /// <summary>Each bridge: a plank deck with low parapets, stone abutments at its ends and stone piers down to the riverbed.</summary>
    private void BuildBridges(Node3D root, BridgeSet set)
    {
        var timber = new MeshKit();
        var stone = new MeshKit();
        foreach (var bridge in set.Bridges)
        {
            float half = bridge.Width / 2 + 0.3f;
            for (int i = 1; i < bridge.Deck.Length; i++)
            {
                var a = bridge.Deck[i - 1];
                var b = bridge.Deck[i];
                timber.Prism(Band(a, b, half), bridge.Height - 0.5f, bridge.Height + 0.05f, CrossingColor, CrossingColor.Lightened(0.2f));
                var side = (b - a).Normalized().Orthogonal() * half;
                foreach (float sign in new[] { 1f, -1f })
                    timber.Prism(Band(a + side * sign, b + side * sign, 0.15f), bridge.Height, bridge.Height + 1, CrossingColor, CrossingColor);
            }

            var along = (bridge.Deck[^1] - bridge.Deck[0]).Normalized();
            foreach (var end in new[] { bridge.Deck[0], bridge.Deck[^1] })
                stone.Prism(Box(end, along, 2.5f, bridge.Width + 1.6f), Height(end) - 1.5f, bridge.Height - 0.5f, Stone, StoneTop);
            foreach (var pier in bridge.Piers)
                stone.Prism(Box(pier, along, 1.6f, bridge.Width + 0.4f), Height(pier) - 1, bridge.Height - 0.5f, Stone, StoneTop);
        }

        Add(root, timber, MeshKit.Material());
        Add(root, stone, MeshKit.Material());
    }

    /// <summary>
    /// Retaining walls as low stone walls, and the stretches of road left steeper than their class allows as ribbons
    /// along their center lines.
    /// </summary>
    private static readonly Color[] TerraceColors =
    {
        new("e0c68f"), new("b9cf8c"), new("8fc4b0"), new("a7b3d8"), new("cfa4c4"), new("e3a98c"), new("c9c27d"), new("9cc49a"),
    };
    private static readonly Color TerraceReach = new("5b4a32");

    /// <summary>
    /// Each terrace's level part drawn flat at its own height, tinted by height (a new tint every half meter, so steps
    /// show), its reach outlined; steps over the limit marked with a post.
    /// </summary>
    private void BuildTerraces(Node3D root, TerraceSet set)
    {
        var fill = new MeshKit();
        var lines = new MeshKit(Mesh.PrimitiveType.Lines);
        var posts = new MeshKit();
        foreach (var t in set.Terraces)
        {
            int band = (int)MathF.Floor(t.Height / 0.5f);
            var color = TerraceColors[((band % TerraceColors.Length) + TerraceColors.Length) % TerraceColors.Length];
            fill.Drape(t.Level.Triangulate(), _ => t.Height, 0.12f, color);
            foreach (var ring in t.Level.Rings)
            {
                for (int i = 0; i < ring.Length; i++)
                    lines.Line(new Vector3(ring[i].X, t.Height + 0.15f, ring[i].Y),
                        new Vector3(ring[(i + 1) % ring.Length].X, t.Height + 0.15f, ring[(i + 1) % ring.Length].Y), color.Darkened(0.5f));
            }

            foreach (var ring in t.Reach.Rings)
            {
                for (int i = 0; i < ring.Length; i++)
                    DrapedLine(lines, ring[i], ring[(i + 1) % ring.Length], 0.3f, TerraceReach);
            }
        }

        foreach (var step in set.Steps.Where(s => s.Steep))
        {
            float h = Height(step.At);
            posts.Prism(Band(step.At - new Vector2(0.4f, 0), step.At + new Vector2(0.4f, 0), 0.4f), h, h + 3, SteepColor, SteepColor);
        }

        Add(root, fill, MeshKit.Material(), shadows: false);
        Add(root, lines, MeshKit.Material(unshaded: true), shadows: false);
        Add(root, posts, MeshKit.Material());
    }

    private void BuildGrading(Node3D root, GradePlan plan, bool detail)
    {
        var walls = new MeshKit();
        foreach (var wall in plan.Walls)
        {
            for (int i = 1; i < wall.Points.Length; i++)
                walls.Prism(Band(wall.Points[i - 1], wall.Points[i], 0.25f), wall.Low - 0.5f, wall.High + 0.15f, Stone, StoneTop);
        }

        Add(root, walls, MeshKit.Material());
        var steep = plan.Corridors.Where(c => c.Steep).Select(c => ((IReadOnlyList<Vector2>)c.Points, 1.6f)).ToList();
        if (steep.Count == 0 || !detail)
            return;
        var kit = new MeshKit();
        kit.Drape(Region.Lines(steep).Triangulate(), Height, 0.5f, SteepColor);
        Add(root, kit, MeshKit.Material(unshaded: true), shadows: false);
    }

    private float Height(Vector2 p) => _ground?.Invoke(p) ?? _graph!.Terrain.HeightAt(p);

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
            float deck = Math.Max(Height(crossing.BankA), Height(crossing.BankB)) + 0.4f;
            solids.Prism(Band(crossing.BankA, crossing.BankB, (inUse ? deckWidth : 1.5f) / 2), deck - 0.8f, deck, CrossingColor,
                CrossingColor.Lightened(0.2f));
        }

        Add(root, solids, MeshKit.Material());
    }

    /// <summary>Wide route ribbons on the ground, drawn to read from the whole map, and a post at each map-edge entry.</summary>
    private void BuildRoutes(Node3D root, RouteNetwork network)
    {
        const float width = 7;
        var ribbons = new MeshKit();
        ribbons.Drape(network.Band(width / 2).Triangulate(), Height, 0.6f, RouteColor, maxEdge: 4);
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
        var corners = Box(at, Vector2.Right, size, size);
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
            switch (landmark.Kind)
            {
                case LandmarkKind.Castle:
                    BuildCastle(yard, church, landmark);
                    continue;
                case LandmarkKind.TownHall:
                    BuildTownHall(church, landmark);
                    continue;
                case LandmarkKind.Friary:
                    BuildFriary(yard, church, landmark);
                    continue;
                case LandmarkKind.LeperHouse:
                    BuildLeperHouse(yard, church, landmark);
                    continue;
                case LandmarkKind.Gallows:
                    BuildGallows(church, landmark);
                    continue;
            }

            yard.Drape(landmark.Plot.Triangulate(), Height, 0.3f, ChurchyardColor);
            var size = landmark.Plot.Bounds.Size;
            bool alongX = MathF.Abs(landmark.Axis.X) >= MathF.Abs(landmark.Axis.Y);
            float length = (alongX ? size.X : size.Y) * 0.62f, width = MathF.Min((alongX ? size.Y : size.X) * 0.45f, 12);
            var u = landmark.Axis.Normalized();
            Church(church, landmark.Center + u * length * 0.08f, u, length, width, width * 0.85f);
        }

        Add(root, yard, MeshKit.Material(), shadows: false);
        Add(root, church, MeshKit.Material());
    }

    /// <summary>
    /// A gabled nave along <paramref name="u"/> with the altar end that way; a bell tower of the given size at the other
    /// end, or with none (0) a small turret on the ridge.
    /// </summary>
    private void Church(MeshKit church, Vector2 nave, Vector2 u, float length, float width, float tower)
    {
        var corners = Box(nave, u, length, width);
        float ground = corners.Min(Height), eaves = corners.Max(Height) + 10, ridge = eaves + width * 0.6f;
        church.Prism(corners, ground - 1, eaves, ChurchWall, ChurchWall);
        GableRoof(church, corners, eaves, ridge, ChurchWall, ChurchRoof);
        if (tower <= 0)
        {
            church.Prism(Box(nave + u * length * 0.1f, u, 2, 2), ridge - 1, ridge + 6, ChurchRoof, ChurchRoof);
            return;
        }

        var towerCorners = Box(nave - u * (length / 2 + tower / 2 - 1), u, tower, tower);
        church.Prism(towerCorners, towerCorners.Min(Height) - 1, ridge + 12, Stone, ChurchRoof);
    }

    /// <summary>The corners of a rectangle <paramref name="length"/> along <paramref name="u"/>, <paramref name="width"/> across.</summary>
    private static Vector2[] Box(Vector2 center, Vector2 u, float length, float width)
    {
        var a = u.Normalized() * length / 2;
        var b = u.Normalized().Orthogonal() * width / 2;
        return [center - a - b, center + a - b, center + a + b, center - a + b];
    }

    /// <summary>The corners of a strip <paramref name="halfWidth"/> to each side of the segment from a to b.</summary>
    private static Vector2[] Band(Vector2 a, Vector2 b, float halfWidth)
    {
        var side = (b - a).Normalized().Orthogonal() * halfWidth;
        return [a + side, b + side, b - side, a - side];
    }

    /// <summary>
    /// A gable roof over a box from <paramref name="eaves"/> to <paramref name="ridge"/>: the ridge runs from the middle
    /// of side 3–0 to the middle of side 1–2, with gable walls at both ends.
    /// </summary>
    private static void GableRoof(MeshKit kit, Vector2[] c, float eaves, float ridge, Color wall, Color roof)
    {
        Vector3 E(Vector2 p) => new(p.X, eaves, p.Y);
        Vector3 R(Vector2 p) => new(p.X, ridge, p.Y);
        Vector3 Out(Vector2 p) => new(p.X - (c[0].X + c[2].X) / 2, 0, p.Y - (c[0].Y + c[2].Y) / 2);
        Vector2 start = (c[3] + c[0]) / 2, end = (c[1] + c[2]) / 2;
        kit.Quad(E(c[0]), E(c[1]), R(end), R(start), roof, Vector3.Up);
        kit.Quad(E(c[2]), E(c[3]), R(start), R(end), roof, Vector3.Up);
        kit.Triangle(E(c[1]), E(c[2]), R(end), wall, Out(end));
        kit.Triangle(E(c[3]), E(c[0]), R(start), wall, Out(start));
    }

    /// <summary>A leper house: a small chapel and three huts inside a low hedge round the plot.</summary>
    private void BuildLeperHouse(MeshKit yard, MeshKit kit, Landmark house)
    {
        yard.Drape(house.Plot.Triangulate(), Height, 0.3f, LeperYard);
        var ring = house.Plot.Offset(-0.6f).Rings[0];
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            kit.Prism(Band(a, b, 0.4f), MathF.Min(Height(a), Height(b)) - 0.5f, MathF.Max(Height(a), Height(b)) + 1.2f, Hedge, Hedge);
        }

        var u = house.Axis.Normalized();
        var v = u.Orthogonal();
        var size = house.Plot.Bounds.Size;
        Church(kit, house.Center + u * size.X * 0.2f - v * size.Y * 0.15f, u, 12, 6, 0);
        for (int i = 0; i < 3; i++)
        {
            var hut = house.Center - u * size.X * 0.28f + v * size.Y * (0.28f - 0.22f * i) + u * (i % 2) * 3;
            var corners = Box(hut, u, 5, 4);
            kit.Prism(corners, corners.Min(Height) - 0.5f, corners.Max(Height) + 3, ChurchWall, ChurchRoof);
        }
    }

    /// <summary>The gallows: two posts and a beam on a low mound.</summary>
    private void BuildGallows(MeshKit kit, Landmark gallows)
    {
        var c = gallows.Center;
        var u = gallows.Axis.Normalized();
        float side = gallows.Plot.Bounds.Size.X * 0.7f;
        var mound = Box(c, u, side, side);
        float top = mound.Max(Height) + 1.2f;
        kit.Prism(mound, mound.Min(Height) - 0.5f, top, Mound, Mound);
        foreach (float at in new[] { -1.6f, 1.6f })
            kit.Prism(Box(c + u * at, u, 0.4f, 0.4f), top, top + 4.5f, Timber, Timber);
        kit.Prism(Box(c, u, 4.2f, 0.4f), top + 4.1f, top + 4.5f, Timber, Timber);
    }

    /// <summary>A two-storey hall filling its plot, with a steep gable roof along its long side and a belfry.</summary>
    private void BuildTownHall(MeshKit kit, Landmark hall)
    {
        var u = hall.Axis.Normalized();
        var size = hall.Plot.Bounds.Size;
        bool alongX = MathF.Abs(u.X) >= MathF.Abs(u.Y);
        float length = (alongX ? size.X : size.Y) - 1, width = (alongX ? size.Y : size.X) - 1;
        var corners = Box(hall.Center, u, length, width);
        float ground = corners.Min(Height), eaves = corners.Max(Height) + 8.5f, ridge = eaves + width * 0.7f;
        kit.Prism(corners, ground - 1, eaves, HallWall, HallWall);
        GableRoof(kit, corners, eaves, ridge, HallWall, ChurchRoof);
        kit.Prism(Box(hall.Center, u, 3, 3), ridge - 2, ridge + 6, HallWall, ChurchRoof);
    }

    /// <summary>
    /// A friary: an aisleless church with a ridge turret along one long side of the plot, and on the other a cloister
    /// of four low ranges round a square garth.
    /// </summary>
    private void BuildFriary(MeshKit yard, MeshKit kit, Landmark friary)
    {
        yard.Drape(friary.Plot.Triangulate(), Height, 0.3f, FriaryYard);
        var u = friary.Axis.Normalized();
        var v = u.Orthogonal();
        var size = friary.Plot.Bounds.Size;
        bool alongX = MathF.Abs(u.X) >= MathF.Abs(u.Y);
        float plotLength = alongX ? size.X : size.Y, plotDepth = alongX ? size.Y : size.X;
        const float nave = 10, range = 6;
        var church = friary.Center + v * (plotDepth / 2 - nave / 2 - 3);
        Church(kit, church, u, plotLength * 0.62f, nave, 0);

        float side = MathF.Min(plotLength * 0.45f, plotDepth - nave - 10);
        var garth = church - v * (nave / 2 + side / 2 + 1) - u * plotLength * 0.1f;
        for (int i = 0; i < 4; i++)
        {
            var dir = i % 2 == 0 ? u : v;
            var normal = i % 2 == 0 ? v : u;
            var corners = Box(garth + normal * (i < 2 ? 1 : -1) * (side / 2 - range / 2), dir, side, range);
            kit.Prism(corners, corners.Min(Height) - 1, corners.Max(Height) + 6, ChurchWall, ChurchRoof);
        }
    }

    private static FieldLegend LandmarkLegend(LandmarkSet set)
    {
        var categories = new List<(Color, string)>();
        if (set.Landmarks.Any(l => l.Kind == LandmarkKind.Church))
            categories.AddRange([(ChurchyardColor, "教堂墓园用地"), (ChurchRoof, "教堂（钟塔在西端，祭坛朝东）")]);
        if (set.Landmarks.Any(l => l.Kind == LandmarkKind.Castle))
            categories.AddRange([(CastleYard, "城堡院子"), (Stone, "城堡（围墙、角楼、主塔；朝外的一面就是城墙）")]);
        if (set.Landmarks.Any(l => l.Kind == LandmarkKind.TownHall))
            categories.Add((HallWall, "市政厅（在市场里）"));
        if (set.Landmarks.Any(l => l.Kind == LandmarkKind.Friary))
            categories.Add((FriaryYard, "修道院用地（教堂和回廊方院）"));
        if (set.Landmarks.Any(l => l.Kind == LandmarkKind.LeperHouse))
            categories.Add((LeperYard, "麻风院（礼拜堂和小屋，围着矮篱）"));
        if (set.Landmarks.Any(l => l.Kind == LandmarkKind.Gallows))
            categories.Add((Mound, "刑场（土丘上的绞架）"));
        return Categories("地标", categories.ToArray());
    }

    /// <summary>
    /// A castle: a curtain wall round the plot with a tower at each corner and a keep toward the outer side, over a
    /// paved yard.
    /// </summary>
    private void BuildCastle(MeshKit yard, MeshKit kit, Landmark castle)
    {
        const float inset = 1.2f, thickness = 2.6f, height = 10, towerSize = 7, towerHeight = 15;
        yard.Drape(castle.Plot.Triangulate(), Height, 0.3f, CastleYard);
        var ring = castle.Plot.Offset(-inset).Rings[0];
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            float length = a.DistanceTo(b);
            var along = (b - a) / length;
            for (float t = 0; t < length - 0.01f; t += 6)
                WallPiece(kit, a + along * t, a + along * Math.Min(length, t + 6), thickness, height);

            Tower(kit, a, along, towerSize, towerHeight);
        }

        float span = castle.Plot.Bounds.Size.X;
        var keep = castle.Center + castle.Axis * span * 0.15f;
        Tower(kit, keep, castle.Axis, span * 0.3f, towerHeight + 9);
    }

    /// <summary>
    /// The wall with a tower every fourth ring point; where gates are shown too, it leaves a gap at each. Where the
    /// ring runs round a castle, the castle's own walls stand in for it.
    /// </summary>
    private void BuildWall(Node3D root, WallRing ring, GateSet? gateSet, LandmarkSet? landmarks)
    {
        var wall = new MeshKit();
        var points = ring.Ring;
        var castle = landmarks?.Landmarks.FirstOrDefault(l => l.Kind == LandmarkKind.Castle)?.Plot.Offset(1.5f);
        bool ByCastle(Vector2 p) => castle != null && castle.Contains(p);
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
            for (float t = start; t < end - 0.01f; t += 6)
            {
                var p = a + along * t;
                var q = a + along * Math.Min(end, t + 6);
                if (!ByCastle((p + q) / 2))
                    WallPiece(wall, p, q, thickness, height);
            }

            if (i % 4 == 0 && !gates.Contains(a) && !ByCastle(a))
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
        var corners = Box(center, facing, size, size);
        kit.Prism(corners, corners.Min(Height) - 1, corners.Max(Height) + height, Stone.Darkened(0.05f), StoneTop);
    }

    /// <summary>A stretch of stone wall from p to q, a meter into the ground at its low end.</summary>
    private void WallPiece(MeshKit kit, Vector2 p, Vector2 q, float thickness, float height) =>
        kit.Prism(Band(p, q, thickness / 2), Math.Min(Height(p), Height(q)) - 1, Math.Max(Height(p), Height(q)) + height, Stone,
            StoneTop);

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
            (RoadColor(RoadKind.Main), "主街"), (RoadColor(RoadKind.Outer), "城外道路"), (RoadColor(RoadKind.Street), "街巷 / 次街"),
            (RoadColor(RoadKind.Lane), "巷"), (RoadColor(RoadKind.Track), "便道"),
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

    private static readonly Color CornerColor = new("1f6fb2"), CourtColor = new("c0392b");
    private static readonly Color PlanTint = new("c9b27c"), GuideColor = new("2e86c1"), CrossColor = new("b5651d");

    /// <summary>The small squares, paved like the market and outlined.</summary>
    private void BuildSquares(Node3D root, SquareSet set)
    {
        var fill = new MeshKit();
        var lines = new MeshKit(Mesh.PrimitiveType.Lines);
        foreach (var square in set.Squares)
        {
            fill.Drape(square.Area.Triangulate(), Height, 0.35f, PlazaColor);
            foreach (var ring in square.Area.Rings)
            {
                for (int i = 0; i < ring.Length; i++)
                    DrapedLine(lines, ring[i], ring[(i + 1) % ring.Length], 0.45f, new Color("6b5a3c"));
            }
        }

        Add(root, fill, MeshKit.Material(), shadows: false);
        Add(root, lines, MeshKit.Material(unshaded: true), shadows: false);
    }

    /// <summary>The planned areas tinted, each with its guide route and its parallel and cross streets.</summary>
    private void BuildPlan(Node3D root, StreetPlan plan)
    {
        var fill = new MeshKit();
        var lines = new MeshKit(Mesh.PrimitiveType.Lines);
        var grid = plan.Grid;
        foreach (var area in plan.Areas)
        {
            fill.Drape(grid.Squares(area.Cells).Triangulate(), Height, 0.12f, PlanTint);
            for (int i = 1; i < area.Guide.Length; i++)
                DrapedLine(lines, area.Guide[i - 1], area.Guide[i], 0.6f, GuideColor);
            foreach (var street in area.Streets)
            {
                for (int i = 1; i < street.Cells.Length; i++)
                    DrapedLine(lines, grid.Center(street.Cells[i - 1]), grid.Center(street.Cells[i]), 0.6f,
                        street.Cross ? CrossColor : RoadColor(RoadKind.Street));
            }
        }

        Add(root, fill, MeshKit.Material(unshaded: true, alpha: 0.5f), shadows: false);
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
            var edge = parcel.Kind switch
            {
                ParcelKind.Corner => CornerColor,
                ParcelKind.Court => CourtColor,
                _ => new Color("5c5a3c"),
            };
            for (int i = 0; i < parcel.Ring.Length; i++)
                DrapedLine(lines, parcel.Ring[i], parcel.Ring[(i + 1) % parcel.Ring.Length], 0.35f, edge);
            if (parcel.Side is { Length: 2 } side)
            {
                var inward = (side[1] - side[0]).Orthogonal().Normalized() * 0.4f;
                foreach (float k in new[] { -1f, 1f })
                    DrapedLine(lines, side[0] + inward * k, side[1] + inward * k, 0.4f, CornerColor);
            }
        }

        Add(root, fill, MeshKit.Material(), shadows: false);
        Add(root, lines, MeshKit.Material(unshaded: true), shadows: false);
    }

    private static readonly Color HedgeGreen = new("4f6b34"), ShedWall = new("b7a07a"), ShedRoof = new("7d5b43"),
        GardenGreen = new("8fae5a");

    /// <summary>Each well as a round stone curb, 2 m across and knee high.</summary>
    private void BuildFixtures(Node3D root, FixtureSet set)
    {
        var kit = new MeshKit();
        foreach (var well in set.Wells)
        {
            var ring = Enumerable.Range(0, 10).Select(i => well.Position + Vector2.Right.Rotated(i * MathF.Tau / 10)).ToArray();
            float ground = ring.Min(Height);
            kit.Prism(ring, ground - 0.3f, ring.Max(Height) + 0.8f, Stone, StoneTop);
        }

        Add(root, kit, MeshKit.Material());
    }

    /// <summary>Walls and hedges as lines on the ground, back buildings as low boxes, gardens as green ground.</summary>
    private void BuildYards(Node3D root, YardSet yards)
    {
        var ground = new MeshKit();
        foreach (var garden in yards.Gardens)
            ground.Drape(garden.Area.Triangulate(), Height, 0.2f, GardenGreen);
        Add(root, ground, MeshKit.Material(), shadows: false);

        var lines = new MeshKit(Mesh.PrimitiveType.Lines);
        foreach (var fence in yards.Fences)
        {
            var color = fence.Kind == FenceKind.Wall ? Stone : HedgeGreen;
            for (int i = 1; i < fence.Points.Length; i++)
                DrapedLine(lines, fence.Points[i - 1], fence.Points[i], 0.5f, color);
        }

        Add(root, lines, MeshKit.Material(unshaded: true), shadows: false);

        var sheds = new MeshKit();
        foreach (var shed in yards.Outbuildings)
            sheds.Prism(shed.Corners, shed.Corners.Min(Height) - 0.3f, shed.Corners.Max(Height) + 2.5f, ShedWall, ShedRoof);
        Add(root, sheds, MeshKit.Material());
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

            // Corners: 0 front-left, 1 front-right, 2 back-right, 3 back-left. Turned a quarter, the ridge runs front to back.
            GableRoof(walls, b.RidgeToStreet ? [c[1], c[2], c[3], c[0]] : c, b.Eaves, b.Ridge, wall, roof);
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
