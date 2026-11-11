using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// Road classes, most important first: where two classes share a grid link, the smaller one wins. Tracks are the
/// dirt roads out to the workshops and the landmarks in the country.
/// </summary>
public enum RoadKind { Main, Outer, Street, Lane, Track }

public enum RoadZone { Inside, Outside }

/// <summary>
/// Where roads may run on the site grid: passable terrain, inside or outside the walls, never along the wall
/// except through a gate cell.
/// </summary>
public sealed class RoadZones : ISummarized
{
    private readonly bool[] _inside;
    private readonly bool[] _nearWall;
    private readonly bool[] _passable;
    private readonly HashSet<int> _gates;

    public RoadZones(CategoryField buildable, ScalarField slope, bool[] inside, bool[] nearWall, bool[] passable,
        IEnumerable<int> gates)
    {
        Buildable = buildable;
        Slope = slope;
        _inside = inside;
        _nearWall = nearWall;
        _passable = passable;
        _gates = gates.ToHashSet();
    }

    public SiteGrid Grid => Buildable.Grid;
    public CategoryField Buildable { get; }
    public ScalarField Slope { get; }

    public string Summary => $"可通行 {_passable.Count(p => p) / (float)_passable.Length:P0}，城内 {_inside.Count(i => i)} 格";

    public bool Passable(int k) => _passable[k];
    public bool Inside(int k) => _inside[k];
    public bool IsGate(int k) => _gates.Contains(k);

    public bool Allowed(int k, RoadZone zone) =>
        _passable[k] && (_gates.Contains(k) || !_nearWall[k] && _inside[k] == (zone == RoadZone.Inside));
}

/// <summary>
/// Roads as links between neighboring site cells, each with a class, plus named anchor cells (core, gates,
/// entries) that must become graph nodes. Road nodes add to a copy and set it; a stored value is never changed.
/// </summary>
public sealed class RoadLinks : ISummarized
{
    private readonly Dictionary<(int, int), RoadKind> _links;
    private readonly Dictionary<int, string> _anchors;

    public RoadLinks()
    {
        _links = new Dictionary<(int, int), RoadKind>();
        _anchors = new Dictionary<int, string>();
    }

    private RoadLinks(RoadLinks source)
    {
        _links = new Dictionary<(int, int), RoadKind>(source._links);
        _anchors = new Dictionary<int, string>(source._anchors);
        CoreCell = source.CoreCell;
    }

    public int CoreCell { get; set; } = -1;
    public IReadOnlyDictionary<(int, int), RoadKind> Links => _links;
    public IReadOnlyDictionary<int, string> Anchors => _anchors;
    public IEnumerable<int> Cells => _links.Keys.SelectMany(l => new[] { l.Item1, l.Item2 }).Distinct();

    public string Summary => $"{_links.Count} 段网格连线，{_anchors.Count} 个锚点";

    public RoadLinks Copy() => new(this);

    public static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    public bool TryGet(int a, int b, out RoadKind kind) => _links.TryGetValue(Key(a, b), out kind);

    public void Anchor(int cell, string role) => _anchors[cell] = role;

    /// <summary>Sets the class of one existing link, whatever it was (marking only ever raises a class).</summary>
    public void Rank((int, int) link, RoadKind kind)
    {
        if (!_links.ContainsKey(link))
            throw new KeyNotFoundException($"没有连线 {link}");
        _links[link] = kind;
    }

    public void Mark(IReadOnlyList<int> path, RoadKind kind)
    {
        for (int i = 1; i < path.Count; i++)
        {
            var key = Key(path[i - 1], path[i]);
            if (!_links.TryGetValue(key, out var old) || kind < old)
                _links[key] = kind;
        }
    }
}

public sealed record RoadNode(int Id, Vector2 Position, string Kind);

public sealed record RoadEdge(int Id, int From, int To, Vector2[] Points, RoadKind Kind, float Width)
{
    public float Length => Points.Zip(Points.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
}

/// <summary>Road network as polylines between junctions, plus the paved area (roads and plaza).</summary>
public sealed class RoadGraph : ISummarized
{
    public required IReadOnlyList<RoadNode> Nodes { get; init; }
    public required IReadOnlyList<RoadEdge> Edges { get; init; }
    public required int CoreNode { get; init; }
    public required Region Footprint { get; init; }

    public string Summary
    {
        get
        {
            float Length(RoadKind kind) => Edges.Where(e => e.Kind == kind).Sum(e => e.Length);
            return $"{Nodes.Count} 节点 / {Edges.Count} 路段；主街 {Length(RoadKind.Main):0} m，次街 {Length(RoadKind.Street):0} m，"
                + $"巷 {Length(RoadKind.Lane):0} m，城外 {Length(RoadKind.Outer):0} m，便道 {Length(RoadKind.Track):0} m";
        }
    }
}

public static class RoadTrees
{
    /// <summary>
    /// The links that lead nowhere: repeatedly prunes a node left with one link, unless the node is fixed (an
    /// anchor) or that link may not be pruned. What is left is every loop and every way between fixed nodes, so the
    /// result does not depend on the order. Returns the indices of the pruned links.
    /// </summary>
    public static HashSet<int> DeadEnds(IReadOnlyList<(int A, int B, bool Prunable)> links, Func<int, bool> isFixed)
    {
        var incident = new Dictionary<int, List<int>>();
        for (int i = 0; i < links.Count; i++)
        {
            Add(links[i].A, i);
            Add(links[i].B, i);
        }

        void Add(int node, int link)
        {
            if (!incident.TryGetValue(node, out var list))
                incident[node] = list = new List<int>();
            list.Add(link);
        }

        var degree = incident.ToDictionary(p => p.Key, p => p.Value.Count);
        var pruned = new HashSet<int>();
        var queue = new Queue<int>(incident.Keys.Where(n => degree[n] == 1 && !isFixed(n)).OrderBy(n => n));
        while (queue.TryDequeue(out int node))
        {
            if (degree[node] != 1)
                continue;
            int link = incident[node].First(l => !pruned.Contains(l));
            if (!links[link].Prunable)
                continue;
            pruned.Add(link);
            degree[node] = 0;
            int other = links[link].A == node ? links[link].B : links[link].A;
            if (--degree[other] == 1 && !isFixed(other))
                queue.Enqueue(other);
        }

        return pruned;
    }
}

/// <summary>One street of a planned area: parallel to the guide at an offset, or across it at a distance along it.</summary>
public sealed record PlanStreet(int[] Cells, bool Cross, float Offset);

/// <summary>
/// A planned area: a patch of late-built land (walled infill, suburbs) laid out from a guide, the through route it
/// lies along, with streets parallel to the guide and cross streets back to it.
/// </summary>
public sealed record PlanArea(int[] Cells, Vector2[] Guide, IReadOnlyList<PlanStreet> Streets)
{
    /// <summary>
    /// Where a point lies relative to the guide: its signed distance (left of the guide's direction is positive) and
    /// how far along the guide its nearest point is.
    /// </summary>
    public (float Offset, float Along) Frame(Vector2 p) => Frame(Guide, p);

    public static (float Offset, float Along) Frame(Vector2[] guide, Vector2 p)
    {
        float best = float.PositiveInfinity, offset = 0, along = 0, run = 0;
        for (int i = 1; i < guide.Length; i++)
        {
            var a = guide[i - 1];
            var b = guide[i];
            float length = a.DistanceTo(b);
            var on = Geometry.ClosestOnSegment(p, a, b);
            float d = p.DistanceTo(on);
            if (d < best)
            {
                best = d;
                float side = (b - a).Cross(p - a);
                offset = side >= 0 ? d : -d;
                along = run + a.DistanceTo(on);
            }

            run += length;
        }

        return (offset, along);
    }
}

/// <summary>The planned areas; the organic street nodes keep out of their cells.</summary>
public sealed class StreetPlan : ISummarized
{
    private readonly HashSet<int> _cells;

    public StreetPlan(SiteGrid grid, IReadOnlyList<PlanArea> areas)
    {
        Grid = grid;
        Areas = areas;
        _cells = areas.SelectMany(a => a.Cells).ToHashSet();
    }

    public SiteGrid Grid { get; }
    public IReadOnlyList<PlanArea> Areas { get; }

    public bool Contains(int k) => _cells.Contains(k);

    public float Length(PlanStreet street) =>
        street.Cells.Zip(street.Cells.Skip(1), (a, b) => Grid.Center(a).DistanceTo(Grid.Center(b))).Sum();

    public string Summary
    {
        get
        {
            var parallel = Areas.SelectMany(a => a.Streets).Where(s => !s.Cross).ToList();
            var cross = Areas.SelectMany(a => a.Streets).Where(s => s.Cross).ToList();
            return $"{Areas.Count} 片规划区，{Grid.Hectares(_cells.Count):0.0} ha；平行街 {parallel.Count} 条 {parallel.Sum(Length):0} m，"
                + $"横街 {cross.Count} 条 {cross.Sum(Length):0} m";
        }
    }
}

public enum SquareKind { Gate, Parvis, Forecourt }

/// <summary>A small paved square: inside a gate, before a church's west door, before a castle.</summary>
public sealed record Square(SquareKind Kind, string Of, Region Area)
{
    public string Label => Kind switch
    {
        SquareKind.Gate => "门内广场",
        SquareKind.Parvis => "教堂前庭",
        _ => "城堡门前",
    };
}

public sealed class SquareSet(IReadOnlyList<Square> squares) : ISummarized
{
    public IReadOnlyList<Square> Squares { get; } = squares;
    public Region Area { get; } = squares.Aggregate(Region.Empty, (all, s) => all.Union(s.Area));

    public string Summary => Squares.Count == 0 ? "没有小广场"
        : string.Join("，", Squares.GroupBy(s => s.Kind).OrderBy(g => g.Key)
            .Select(g => $"{g.First().Label} {g.Count()} 处 {g.Sum(s => s.Area.Area):0} m²"));
}

public enum FixtureKind { Well, MarketCross }

/// <summary>A small public thing standing in the paving: a well, the market cross.</summary>
public sealed record Fixture(FixtureKind Kind, Vector2 Position);

public sealed class FixtureSet(IReadOnlyList<Fixture> fixtures) : ISummarized
{
    public IReadOnlyList<Fixture> Fixtures { get; } = fixtures;
    public IEnumerable<Fixture> Wells => Fixtures.Where(f => f.Kind == FixtureKind.Well);

    public string Summary => $"水井 {Wells.Count()} 口"
        + (Fixtures.Any(f => f.Kind == FixtureKind.MarketCross) ? "，市场十字" : "");
}
