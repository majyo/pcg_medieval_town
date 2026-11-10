using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class ExtractRoadsOptions
{
    [Param("主街宽 m", 4, 14, 0.5,
        Hint = "主街的路面宽度，影响铺装范围和街区大小")]
    public float MainWidth { get; set; } = 7;
    [Param("街巷宽 m", 2.5, 8, 0.5,
        Hint = "街巷的路面宽度，影响铺装范围和街区大小")]
    public float StreetWidth { get; set; } = 4;
    [Param("城外道路宽 m", 3, 10, 0.5,
        Hint = "城外道路的路面宽度")]
    public float OuterWidth { get; set; } = 5;
    [Param("便道宽 m", 2, 6, 0.5,
        Hint = "通往作坊和城外地标的土路的路面宽度")]
    public float TrackWidth { get; set; } = 3;

    public float Width(RoadKind kind) => kind switch
    {
        RoadKind.Main => MainWidth,
        RoadKind.Outer => OuterWidth,
        RoadKind.Track => TrackWidth,
        _ => StreetWidth,
    };
}

/// <summary>
/// Turns grid links into a road graph: chains of degree-2 cells become simplified polylines; anchors, junctions,
/// dead ends and changes of road class become graph nodes. Since every road was marked on the same grid,
/// junctions are exact cells and no segment intersection is needed. Also paves the roads and the plaza.
/// </summary>
public sealed class ExtractRoads : CityNode<ExtractRoadsOptions>
{
    public override string Group => NodeGroups.Roads;
    public override string Title => "提取路网";
    public override string Description =>
        "把格子之间的连线整理成路网：连续的格子变成折线，路口、端点和等级变化处成为节点；再按路宽铺出路面。";
    public override IReadOnlyList<IKey> Reads => [Keys.Buildable, Keys.RoadLinks, Keys.Plaza, Keys.Wall];
    public override IReadOnlyList<IKey> Writes => [Keys.Roads];

    protected override void Run(NodeContext c, ExtractRoadsOptions o)
    {
        var buildable = c.Get(Keys.Buildable);
        var grid = buildable.Grid;
        var links = c.Get(Keys.RoadLinks);
        var plaza = c.Get(Keys.Plaza);
        var interior = c.Get(Keys.Wall).Interior;
        var anchors = links.Anchors;
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (a, b) in links.Links.Keys)
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

        RoadKind Kind(int a, int b) => links.Links[RoadLinks.Key(a, b)];
        bool IsJoint(int k) => anchors.ContainsKey(k) || adjacency[k].Count != 2
            || Kind(k, adjacency[k][0]) != Kind(k, adjacency[k][1]);

        var ids = new Dictionary<int, int>();
        var nodes = new List<RoadNode>();
        int Id(int k)
        {
            if (!ids.TryGetValue(k, out int id))
            {
                id = ids[k] = nodes.Count;
                string role = anchors.TryGetValue(k, out var anchor) ? anchor
                    : adjacency.TryGetValue(k, out var next) && next.Count == 1 ? "end" : "junction";
                nodes.Add(new RoadNode(id, grid.Center(k), role));
            }

            return id;
        }

        var used = new HashSet<(int, int)>();
        var edges = new List<RoadEdge>();
        var starts = adjacency.Keys.Where(IsJoint).OrderBy(k => k).ToList();
        if (starts.Count == 0 && adjacency.Count > 0)
            starts.Add(adjacency.Keys.Min());
        foreach (int start in starts)
        {
            foreach (int first in adjacency[start])
            {
                if (used.Contains(RoadLinks.Key(start, first)))
                    continue;
                var kind = Kind(start, first);
                var cells = new List<int> { start };
                int previous = start, current = first;
                used.Add(RoadLinks.Key(start, first));
                while (!IsJoint(current) && current != start)
                {
                    cells.Add(current);
                    int next = adjacency[current][0] == previous ? adjacency[current][1] : adjacency[current][0];
                    used.Add(RoadLinks.Key(current, next));
                    previous = current;
                    current = next;
                }

                cells.Add(current);
                // Main and outer roads follow the straightened through routes, whose cells step around lines at any
                // angle; simplify them as loosely as the routes were, or they zigzag and their paving nicks the plots.
                // Where the loose line touches water (a crossing, or a cut river bend) or cuts across the wall away from
                // the ends (a route hugging the wall), keep the tight one.
                var centers = cells.Select(k => grid.Center(k)).ToList();
                var points = Geometry.Simplify(centers, grid.CellSize * 0.4f);
                if (kind is RoadKind.Main or RoadKind.Outer)
                {
                    var loose = Geometry.Simplify(centers, grid.CellSize * 0.75f);
                    bool Dry(Vector2 p) => grid.CellAt(p) is int k && k >= 0 && buildable.Block(k) != SiteBlock.Water;
                    bool Side(Vector2 p) => interior.Contains(p) == (kind == RoadKind.Main)
                        || p.DistanceTo(centers[0]) < grid.CellSize * 1.5f || p.DistanceTo(centers[^1]) < grid.CellSize * 1.5f;
                    if (Geometry.Samples(loose, 1).All(p => Dry(p) && Side(p)))
                        points = loose;
                }
                edges.Add(new RoadEdge(edges.Count, Id(start), Id(current), points, kind, o.Width(kind)));
            }
        }

        int core = Id(links.CoreCell);
        var footprint = Region.Lines(edges.Select(e => ((IReadOnlyList<Vector2>)e.Points, e.Width))).Union(plaza.Area);
        c.Set(Keys.Roads, new RoadGraph { Nodes = nodes, Edges = edges, CoreNode = core, Footprint = footprint });
    }
}
