using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public enum RoadKind { Main, Outer, Street }

public sealed class RoadOptions
{
    [Param("主街宽 m", 4, 14, 0.5)] public float MainWidth { get; set; } = 7;
    [Param("街巷宽 m", 2.5, 8, 0.5)] public float StreetWidth { get; set; } = 4;
    [Param("城外道路宽 m", 3, 10, 0.5)] public float OuterWidth { get; set; } = 5;
    [Param("街巷间距 m", 25, 140, 5)] public float StreetSpacing { get; set; } = 60;
    [Param("成环概率", 0, 1, 0.05)] public float LoopChance { get; set; } = 0.35f;
    [Param("道路坡度上限 °", 3, 25, 0.5)] public float MaxSlope { get; set; } = 12;
    [Param("城外道路")] public bool OuterRoads { get; set; } = true;
    [Param("种子", 0, 99999)] public int Seed { get; set; } = 1;

    public float Width(RoadKind kind) => kind switch
    {
        RoadKind.Main => MainWidth,
        RoadKind.Outer => OuterWidth,
        _ => StreetWidth,
    };
}

public sealed record RoadNode(int Id, Vector2 Position, string Kind);

public sealed record RoadEdge(int Id, int From, int To, Vector2[] Points, RoadKind Kind, float Width)
{
    public float Length => Points.Zip(Points.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
}

public sealed class RoadGraph : IStageResult
{
    public required IReadOnlyList<RoadNode> Nodes { get; init; }
    public required IReadOnlyList<RoadEdge> Edges { get; init; }
    public required int CoreNode { get; init; }
    public required Region Footprint { get; init; }
    public List<string> Notices { get; } = new();

    public string Summary
    {
        get
        {
            float main = Edges.Where(e => e.Kind == RoadKind.Main).Sum(e => e.Length);
            float street = Edges.Where(e => e.Kind == RoadKind.Street).Sum(e => e.Length);
            float outer = Edges.Where(e => e.Kind == RoadKind.Outer).Sum(e => e.Length);
            return $"{Nodes.Count} 节点 / {Edges.Count} 路段；主街 {main:0} m，街巷 {street:0} m，城外 {outer:0} m";
        }
    }

    IReadOnlyList<string> IStageResult.Notices => Notices;
}

/// <summary>
/// Plans roads on the site grid. All searches mark grid links; the graph is extracted from those links
/// afterwards, so junctions are exact grid cells and no segment intersection is ever needed.
/// </summary>
public sealed class RoadStage : Stage<RoadOptions, RoadGraph>
{
    public override string Title => "03 路网";

    protected override RoadGraph Run(StageInputs inputs, RoadOptions o)
    {
        var site = inputs.Get<Site>();
        var layout = inputs.Get<TownLayout>();
        var planner = new RoadPlanner(site, layout, o, inputs.Cancellation);
        var notices = new List<string>();
        int core = site.CellAt(layout.Core);
        var anchors = new Dictionary<int, string> { [core] = "core" };

        foreach (var gate in layout.Gates)
        {
            int gateCell = site.CellAt(gate.Position);
            anchors[gateCell] = "gate";
            if (!planner.Connect(gateCell, core, RoadPlanner.Zone.Inside, RoadKind.Main))
                throw new InvalidOperationException($"{gate.Id} 无法通往广场");

            if (!o.OuterRoads)
                continue;
            int entry = site.CellAt(gate.Entry);
            if (planner.Connect(gateCell, entry, RoadPlanner.Zone.Outside, RoadKind.Outer))
                anchors[entry] = "entry";
            else
                notices.Add($"{gate.Id} 城外道路无法到达地图边缘（可能被河流阻隔）");
        }

        int failed = planner.AddStreets(core);
        if (failed > 0)
            notices.Add($"{failed} 条候选街巷无合法通路，已跳过");

        var graph = planner.Extract(anchors, core);
        graph.Notices.AddRange(notices);
        return graph;
    }
}

internal sealed class RoadPlanner
{
    public enum Zone { Inside, Outside }

    private readonly Site _site;
    private readonly TownLayout _layout;
    private readonly RoadOptions _o;
    private readonly System.Threading.CancellationToken _cancel;
    private readonly bool[] _inside;
    private readonly bool[] _nearWall;
    private readonly bool[] _terrain;
    private readonly HashSet<int> _gates;
    private readonly Dictionary<(int, int), RoadKind> _links = new();

    public RoadPlanner(Site site, TownLayout layout, RoadOptions o, System.Threading.CancellationToken cancel)
    {
        _site = site;
        _layout = layout;
        _o = o;
        _cancel = cancel;
        _inside = new bool[site.Count];
        _nearWall = new bool[site.Count];
        _terrain = new bool[site.Count];
        _gates = layout.Gates.Select(g => site.CellAt(g.Position)).ToHashSet();
        var wallBounds = layout.Interior.Bounds.Grow(site.CellSize * 2);
        for (int k = 0; k < site.Count; k++)
        {
            var p = site.Center(k);
            _terrain[k] = site.Block[k] != SiteBlock.Water && site.Slope[k] <= o.MaxSlope;
            if (!wallBounds.HasPoint(p))
                continue;
            _inside[k] = layout.Interior.Contains(p);
            _nearWall[k] = DistanceToRing(p, layout.Wall) < site.CellSize * 0.5f;
        }
    }

    private bool Allowed(int k, Zone zone) =>
        _terrain[k] && (_gates.Contains(k) || !_nearWall[k] && _inside[k] == (zone == Zone.Inside));

    public bool Connect(int from, int to, Zone zone, RoadKind kind)
    {
        var path = Search(from, to, zone, kind);
        if (path == null)
            return false;
        Mark(path, kind);
        return true;
    }

    /// <summary>Organic street mesh: spaced sites inside the walls, joined by nearest pairs plus a few loops.</summary>
    public int AddStreets(int core)
    {
        var rng = new Random(_o.Seed);
        float spacing = _o.StreetSpacing;
        var hubs = new List<int>();
        foreach (int k in _links.Keys.SelectMany(l => new[] { l.Item1, l.Item2 }).Distinct().Where(k => _inside[k]).OrderBy(k => k))
        {
            if (hubs.All(h => Distance(h, k) >= spacing * 0.8f))
                hubs.Add(k);
        }

        var roadCells = _links.Keys.SelectMany(l => new[] { l.Item1, l.Item2 }).ToHashSet();
        var sites = new List<int>(hubs);
        var candidates = Enumerable.Range(0, _site.Count).Where(k => Allowed(k, Zone.Inside) && !_gates.Contains(k))
            .OrderBy(_ => rng.Next()).ToList();
        foreach (int k in candidates)
        {
            if (sites.Count >= hubs.Count + 160)
                break;
            if (sites.All(s => Distance(s, k) >= spacing) && roadCells.All(r => Distance(r, k) >= spacing * 0.5f))
                sites.Add(k);
        }

        var parent = Enumerable.Range(0, sites.Count).Select(i => i < hubs.Count ? 0 : i).ToArray();
        int Root(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        var pairs = new List<(int A, int B, float Length)>();
        for (int i = 0; i < sites.Count; i++)
            for (int j = Math.Max(i + 1, hubs.Count); j < sites.Count; j++)
            {
                float length = Distance(sites[i], sites[j]);
                if (length < spacing * 1.9f)
                    pairs.Add((i, j, length));
            }

        var accepted = new List<(int A, int B)>();
        int failed = 0, attempts = 0;
        foreach (var (a, b, _) in pairs.OrderBy(p => p.Length))
        {
            if (Root(a) == Root(b))
            {
                if (rng.NextSingle() > _o.LoopChance || Acute(a, b, accepted, sites))
                    continue;
            }

            if (++attempts > 900)
                break;
            var path = Search(sites[a], sites[b], Zone.Inside, RoadKind.Street);
            if (path == null)
            {
                failed++;
                continue;
            }

            Mark(path, RoadKind.Street);
            parent[Root(b)] = Root(a);
            accepted.Add((a, b));
        }

        return failed;
    }

    private bool Acute(int a, int b, List<(int A, int B)> accepted, List<int> sites)
    {
        // Rejects a new link that would leave either endpoint at a sharp angle to an accepted link.
        bool SharpAt(int pivot, int target)
        {
            var direction = _site.Center(sites[target]) - _site.Center(sites[pivot]);
            foreach (var (p, q) in accepted)
            {
                if (p != pivot && q != pivot)
                    continue;
                var other = _site.Center(sites[p == pivot ? q : p]) - _site.Center(sites[pivot]);
                if (MathF.Abs(other.AngleTo(direction)) < 0.6f)
                    return true;
            }

            return false;
        }

        return SharpAt(a, b) || SharpAt(b, a);
    }

    private List<int>? Search(int start, int goal, Zone zone, RoadKind kind)
    {
        if (start < 0 || goal < 0 || !Allowed(start, zone) || !Allowed(goal, zone))
            return null;

        int nx = _site.Nx;
        var cost = new Dictionary<int, float> { [start] = 0 };
        var from = new Dictionary<int, int>();
        var closed = new HashSet<int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(start, 0);
        int expanded = 0;
        while (open.TryDequeue(out int k, out _))
        {
            if (!closed.Add(k))
                continue;
            if (k == goal)
            {
                var path = new List<int> { k };
                while (k != start)
                    path.Add(k = from[k]);
                path.Reverse();
                return path;
            }

            if (++expanded % 4096 == 0)
                _cancel.ThrowIfCancellationRequested();

            int x = k % nx, z = k / nx;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0 || !_site.InGrid(x + dx, z + dz))
                        continue;
                    int j = (z + dz) * nx + x + dx;
                    if (closed.Contains(j) || !Allowed(j, zone))
                        continue;
                    if (dx != 0 && dz != 0)
                    {
                        int side1 = z * nx + x + dx, side2 = (z + dz) * nx + x;
                        if (!_terrain[side1] || !_terrain[side2] || _links.ContainsKey(Key(side1, side2)))
                            continue;
                    }

                    float step = (dx != 0 && dz != 0 ? 1.4142f : 1) * _site.CellSize;
                    float factor = 1 + _site.Slope[j] / 12 + (_site.Block[j] is SiteBlock.Forest ? 0.5f : 0);
                    if (_links.TryGetValue(Key(k, j), out var existing))
                        factor *= kind == RoadKind.Street || existing == kind ? 0.5f : 0.8f;
                    float next = cost[k] + step * factor;
                    if (cost.TryGetValue(j, out float old) && old <= next)
                        continue;
                    cost[j] = next;
                    from[j] = k;
                    open.Enqueue(j, next + _site.Center(j).DistanceTo(_site.Center(goal)) * 0.5f);
                }
        }

        return null;
    }

    private void Mark(List<int> path, RoadKind kind)
    {
        for (int i = 1; i < path.Count; i++)
        {
            var key = Key(path[i - 1], path[i]);
            if (!_links.TryGetValue(key, out var old) || kind < old)
                _links[key] = kind;
        }
    }

    /// <summary>Chains of degree-2 cells become edges; anchors, junctions, ends and kind changes become nodes.</summary>
    public RoadGraph Extract(Dictionary<int, string> anchors, int core)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (a, b) in _links.Keys)
        {
            Add(a, b);
            Add(b, a);
        }

        void Add(int a, int b)
        {
            if (!adjacency.TryGetValue(a, out var list))
                adjacency[a] = list = new List<int>();
            list.Add(b);
        }

        bool IsNode(int k) => anchors.ContainsKey(k) || adjacency[k].Count != 2
            || _links[Key(k, adjacency[k][0])] != _links[Key(k, adjacency[k][1])];

        var nodeIds = new Dictionary<int, int>();
        var nodes = new List<RoadNode>();
        int NodeId(int k)
        {
            if (!nodeIds.TryGetValue(k, out int id))
            {
                id = nodeIds[k] = nodes.Count;
                string role = anchors.TryGetValue(k, out var anchor) ? anchor : adjacency[k].Count == 1 ? "end" : "junction";
                nodes.Add(new RoadNode(id, _site.Center(k), role));
            }

            return id;
        }

        var used = new HashSet<(int, int)>();
        var edges = new List<RoadEdge>();
        var starts = adjacency.Keys.Where(IsNode).OrderBy(k => k).ToList();
        if (starts.Count == 0 && adjacency.Count > 0)
            starts.Add(adjacency.Keys.Min());
        foreach (int start in starts)
        {
            foreach (int first in adjacency[start])
            {
                if (used.Contains(Key(start, first)))
                    continue;
                var kind = _links[Key(start, first)];
                var cells = new List<int> { start };
                int previous = start, current = first;
                used.Add(Key(start, first));
                while (!IsNode(current) && current != start)
                {
                    cells.Add(current);
                    int next = adjacency[current][0] == previous ? adjacency[current][1] : adjacency[current][0];
                    used.Add(Key(current, next));
                    previous = current;
                    current = next;
                }

                cells.Add(current);
                var points = Simplify(cells.Select(_site.Center).ToList(), _site.CellSize * 0.4f);
                edges.Add(new RoadEdge(edges.Count, NodeId(start), NodeId(current), points, kind, _o.Width(kind)));
            }
        }

        NodeId(core);
        var footprint = Region.Lines(edges.Select(e => ((IReadOnlyList<Vector2>)e.Points, e.Width)))
            .Union(_layout.Plaza);
        return new RoadGraph { Nodes = nodes, Edges = edges, CoreNode = nodeIds[core], Footprint = footprint };
    }

    private static Vector2[] Simplify(List<Vector2> points, float tolerance)
    {
        if (points.Count < 3)
            return points.ToArray();

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float worst = 0;
            int index = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistanceToSegment(points[i], points[a], points[b]);
                if (d > worst)
                {
                    worst = d;
                    index = i;
                }
            }

            if (index >= 0 && worst > tolerance)
            {
                keep[index] = true;
                stack.Push((a, index));
                stack.Push((index, b));
            }
        }

        return points.Where((_, i) => keep[i]).ToArray();
    }

    private float Distance(int a, int b) => _site.Center(a).DistanceTo(_site.Center(b));

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    internal static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-8f ? 0 : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    private static float DistanceToRing(Vector2 p, Vector2[] ring)
    {
        float best = float.MaxValue;
        for (int i = 0; i < ring.Length; i++)
            best = Math.Min(best, DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Length]));
        return best;
    }
}
