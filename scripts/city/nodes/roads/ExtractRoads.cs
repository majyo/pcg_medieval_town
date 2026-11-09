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

    public float Width(RoadKind kind) => kind switch
    {
        RoadKind.Main => MainWidth,
        RoadKind.Outer => OuterWidth,
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
    public override string Group => "路网";
    public override string Title => "提取路网";
    public override string Description =>
        "把格子之间的连线整理成路网：连续的格子变成折线，路口、端点和等级变化处成为节点；再按路宽铺出路面。";
    public override IReadOnlyList<IKey> Reads => [Keys.Site, Keys.RoadLinks, Keys.Plaza];
    public override IReadOnlyList<IKey> Writes => [Keys.Roads];

    protected override void Run(NodeContext c, ExtractRoadsOptions o)
    {
        var site = c.Get(Keys.Site);
        var links = c.Get(Keys.RoadLinks);
        var plaza = c.Get(Keys.Plaza);
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
                nodes.Add(new RoadNode(id, site.Center(k), role));
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
                // Where the loose line touches water (a crossing, or a cut river bend), keep the tight one.
                var centers = cells.Select(site.Center).ToList();
                var points = Geometry.Simplify(centers, site.CellSize * 0.4f);
                if (kind != RoadKind.Street)
                {
                    var loose = Geometry.Simplify(centers, site.CellSize * 0.75f);
                    if (Geometry.Samples(loose, 1).All(p => site.CellAt(p) is int k && k >= 0 && site.Block[k] != SiteBlock.Water))
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
