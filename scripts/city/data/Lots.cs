using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>Land left for building inside the walls, and its connected parts.</summary>
public sealed record BlockSet(Region Developable, IReadOnlyList<Region> Blocks) : ISummarized
{
    public string Summary => $"可建区 {Developable.Area / 10000:0.0} ha，{Blocks.Count} 街区";
}

/// <summary>
/// A street-fronting plot. Front runs along the street edge; Inward points away from the street. Use is the land use
/// the plot was cut for (<see cref="LandUses.PlotUse"/> at <see cref="Probe"/>).
/// </summary>
public sealed record Parcel(int Id, Vector2[] Ring, Vector2 FrontA, Vector2 FrontB, Vector2 Inward, RoadKind Street, LandUse Use,
    float Area)
{
    public float Frontage => FrontA.DistanceTo(FrontB);

    /// <summary>The point just behind the middle of the front that decides the plot's use.</summary>
    public Vector2 Probe => (FrontA + FrontB) / 2 + Inward * 1.5f;
}

public sealed record ParcelSet(IReadOnlyList<Parcel> Parcels) : ISummarized
{
    public string Summary => $"{Parcels.Count} 地块，{Parcels.Sum(p => (double)p.Area) / 10000:0.00} ha（"
        + string.Join("、", Parcels.GroupBy(p => p.Use).OrderBy(g => g.Key).Select(g => $"{g.Key.Label()} {g.Count()}")) + "）";
}

/// <summary>
/// Street-front house: Corners are front-left, front-right, back-right, back-left. Walls rise from Base to
/// Eaves; a gable roof peaks at Ridge, with the ridge running front-to-back when RidgeToStreet is true.
/// </summary>
public sealed record Building(int ParcelId, Vector2[] Corners, float Base, float Eaves, float Ridge, bool RidgeToStreet, int Palette)
{
    public float Footprint => Corners[0].DistanceTo(Corners[1]) * Corners[1].DistanceTo(Corners[2]);
}

public sealed record BuildingSet(IReadOnlyList<Building> Buildings) : ISummarized
{
    public string Summary => $"{Buildings.Count} 栋，占地 {Buildings.Sum(b => b.Footprint) / 10000:0.00} ha";
}
