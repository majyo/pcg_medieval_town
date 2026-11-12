using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// A bridge carrying a road over the river: a level deck, straight between two abutments on the banks, on piers.
/// First and Last are the deck's ends among the road's stations (<see cref="Geometry.Stations"/> of the edge's line), so the
/// road's profile can take the deck's height there. Water is the river's level under it.
/// </summary>
public sealed record Bridge(string Crossing, int Edge, int First, int Last, Vector2[] Deck, float Height, float Width,
    Vector2[] Piers, float Water, float Clearance, float MaxSpan)
{
    public float Length => Deck.Zip(Deck.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
}

public sealed record BridgeSet(IReadOnlyList<Bridge> Bridges) : ISummarized
{
    public string Summary => Bridges.Count == 0 ? "没有桥"
        : string.Join("，", Bridges.Select(b => $"桥 {b.Length:0} m、{b.Piers.Length + 1} 跨、桥面高出水面 {b.Height - b.Water:0.0} m"));
}
