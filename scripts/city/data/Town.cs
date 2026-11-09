using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>Where the town stands and how far it is planned to reach.</summary>
public sealed record TownCore(Vector2 Position, float Radius) : ISummarized
{
    public string Summary => $"城址 ({Position.X:0}, {Position.Y:0})，规划半径 {Radius:0} m";
}

/// <summary>The market place and a point inside it (on its road, or the middle of a triangle at a junction).</summary>
public sealed record Plaza(Region Area, Vector2 Center) : ISummarized
{
    public string Summary => $"市场 {Area.Area:0} m²";
}

/// <summary>
/// Closed wall ring (site cell centers at gates) and the area it encloses. <see cref="BuiltShare"/>, when known, is
/// the share of the town built before the wall that it encloses.
/// </summary>
public sealed class WallRing : ISummarized
{
    public WallRing(Vector2[] ring, float? builtShare = null)
    {
        Ring = ring;
        Interior = Region.Polygon(ring);
        BuiltShare = builtShare;
    }

    public Vector2[] Ring { get; }
    public Region Interior { get; }
    public float? BuiltShare { get; }
    public float Length => Ring.Select((p, i) => p.DistanceTo(Ring[(i + 1) % Ring.Length])).Sum();

    /// <summary>The same wall along a new ring (e.g. with gate points inserted).</summary>
    public WallRing With(Vector2[] ring) => new(ring, BuiltShare);

    public string Summary => $"城内 {Interior.Area / 10000:0.0} ha" +
        (BuiltShare is { } share ? $"（围住建成区的 {share:P0}）" : "") + $"，城墙 {Length:0} m";
}

/// <summary>A wall opening. Position is a wall ring point; Entry is where its outer road should reach the map edge.</summary>
public sealed record Gate(string Id, Vector2 Position, Vector2 Outward, Vector2 Entry);

public sealed record GateSet(IReadOnlyList<Gate> Gates) : ISummarized
{
    /// <summary>Distance between the two closest gates (infinite with fewer than two).</summary>
    public float MinSpacing => Gates.SelectMany((g, i) => Gates.Skip(i + 1).Select(h => g.Position.DistanceTo(h.Position)))
        .DefaultIfEmpty(float.PositiveInfinity).Min();

    public string Summary => $"城门 {Gates.Count} 座" + (Gates.Count > 1 ? $"，最近两座相距 {MinSpacing:0} m" : "");
}
