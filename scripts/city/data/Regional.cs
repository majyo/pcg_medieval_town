using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// A place to cross the river: the line between two bridgeheads (the first passable land cells on either bank),
/// through <see cref="Center"/> on the channel's center line. <see cref="Cells"/> are the grid cells along that line,
/// from <see cref="BankA"/> to <see cref="BankB"/>, each a side neighbor of the one before; routes may use the water
/// cells among them and no others.
/// </summary>
public sealed record Crossing(string Id, Vector2 Center, Vector2 BankA, Vector2 BankB, float Width, float Score, int[] Cells)
{
    /// <summary>Length of the crossing from bridgehead to bridgehead.</summary>
    public float Span => BankA.DistanceTo(BankB);
}

public sealed class CrossingSet : ISummarized
{
    public CrossingSet(IReadOnlyList<Crossing> crossings) => Crossings = crossings;

    public IReadOnlyList<Crossing> Crossings { get; }

    public string Summary => Crossings.Count == 0 ? "没有渡口"
        : $"{Crossings.Count} 处，最窄 {Crossings.Min(c => c.Width):0} m，最宽 {Crossings.Max(c => c.Width):0} m";
}

/// <summary>Where a road enters the map from the country beyond: an edge cell, which edge, and which bank it is on.</summary>
public sealed record MapEntry(string Id, Vector2 Position, int Cell, string Edge, int Bank);

/// <summary>
/// A through route as a chain of 8-neighbor grid cells, and its simplified line. It starts at entry
/// <see cref="From"/> and ends at <see cref="To"/>: another entry (the trunk) or a cell of an earlier route it joins.
/// </summary>
public sealed record Route(string Id, string From, string To, int[] Cells, Vector2[] Points, IReadOnlyList<string> Crossings)
{
    public float Length => Points.Zip(Points.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
}

/// <summary>
/// The roads that cross the map before there is a town: entries on the map edges and the routes between them. Later
/// routes end on earlier ones; junctions are cells where three or more route links meet.
/// </summary>
public sealed class RouteNetwork : ISummarized
{
    public RouteNetwork(SiteGrid grid, IReadOnlyList<MapEntry> entries, IReadOnlyList<Route> routes)
    {
        Grid = grid;
        Entries = entries;
        Routes = routes;
        var links = new HashSet<(int, int)>();
        foreach (var route in routes)
        {
            for (int i = 1; i < route.Cells.Length; i++)
                links.Add(RoadLinks.Key(route.Cells[i - 1], route.Cells[i]));
        }

        Links = links;
        var degree = new Dictionary<int, int>();
        foreach (var (a, b) in links)
        {
            degree[a] = degree.GetValueOrDefault(a) + 1;
            degree[b] = degree.GetValueOrDefault(b) + 1;
        }

        Cells = degree.Keys.ToHashSet();
        Junctions = degree.Where(d => d.Value >= 3).Select(d => d.Key).OrderBy(k => k).ToArray();
        Length = links.Sum(l => grid.Center(l.Item1).DistanceTo(grid.Center(l.Item2)));
    }

    public SiteGrid Grid { get; }
    public IReadOnlyList<MapEntry> Entries { get; }
    public IReadOnlyList<Route> Routes { get; }

    /// <summary>Every link between neighboring cells used by some route, smaller cell first.</summary>
    public IReadOnlySet<(int, int)> Links { get; }

    /// <summary>Every cell some route passes through.</summary>
    public IReadOnlySet<int> Cells { get; }

    public IReadOnlyList<int> Junctions { get; }

    /// <summary>Total length of road, shared stretches counted once (along grid links, so a little longer than the lines).</summary>
    public float Length { get; }

    public IEnumerable<string> UsedCrossings => Routes.SelectMany(r => r.Crossings).Distinct();

    public string Summary =>
        $"{Entries.Count} 个入口，{Routes.Count} 条道路，共 {Length / 1000:0.0} km，过河 {UsedCrossings.Count()} 处，路口 {Junctions.Count} 个";
}

public enum OriginKind { Bridgehead, Junction, Roadside }

/// <summary>Where the town starts and why there: by a crossing, at a junction, or simply on a through route.</summary>
public sealed record TownOrigin(Vector2 Position, int Cell, OriginKind Kind, IReadOnlyList<string> Routes) : ISummarized
{
    public string KindLabel => Kind switch
    {
        OriginKind.Bridgehead => "桥头",
        OriginKind.Junction => "路口",
        _ => "路边",
    };

    public string Summary => $"{KindLabel} ({Position.X:0}, {Position.Y:0})，位于 {string.Join("、", Routes)}";
}
