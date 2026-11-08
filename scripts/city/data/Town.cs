using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>Where the town stands and how far it is planned to reach.</summary>
public sealed record TownCore(Vector2 Position, float Radius) : ISummarized
{
    public string Summary => $"城址 ({Position.X:0}, {Position.Y:0})，规划半径 {Radius:0} m";
}

public sealed record Plaza(Region Area, Vector2 Center) : ISummarized
{
    public string Summary => $"广场 {Area.Area:0} m²";
}

/// <summary>Closed wall ring (site cell centers at gates) and the area it encloses.</summary>
public sealed class WallRing : ISummarized
{
    public WallRing(Vector2[] ring)
    {
        Ring = ring;
        Interior = Region.Polygon(ring);
    }

    public Vector2[] Ring { get; }
    public Region Interior { get; }
    public float Length => Ring.Select((p, i) => p.DistanceTo(Ring[(i + 1) % Ring.Length])).Sum();

    public string Summary => $"城内 {Interior.Area / 10000:0.0} ha，城墙 {Length:0} m";
}

/// <summary>A wall opening. Position is a wall ring point; Entry is where its outer road should reach the map edge.</summary>
public sealed record Gate(string Id, Vector2 Position, Vector2 Outward, Vector2 Entry);

public sealed record GateSet(IReadOnlyList<Gate> Gates) : ISummarized
{
    public string Summary => $"城门 {Gates.Count} 座";
}
