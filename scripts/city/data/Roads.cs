using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// Road classes, most important first: where two classes share a grid link, the smaller one wins. Tracks are the
/// dirt roads out to the workshops and the landmarks in the country.
/// </summary>
public enum RoadKind { Main, Outer, Street, Track }

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
            return $"{Nodes.Count} 节点 / {Edges.Count} 路段；主街 {Length(RoadKind.Main):0} m，街巷 {Length(RoadKind.Street):0} m，"
                + $"城外 {Length(RoadKind.Outer):0} m，便道 {Length(RoadKind.Track):0} m";
        }
    }
}
