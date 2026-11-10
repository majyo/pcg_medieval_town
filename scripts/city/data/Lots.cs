using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>Land left for building (the built cells inside and outside the wall, and the trades), and its connected parts.</summary>
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

    /// <summary>What sort of plot it is (<see cref="ParcelKinds"/>); a plot is general until classified.</summary>
    public ParcelKind Kind { get; init; } = ParcelKind.General;

    /// <summary>A corner plot's side along the side street: its two ends. Null for other plots.</summary>
    public Vector2[]? Side { get; init; }

    /// <summary>How far back the plot reaches from its front.</summary>
    public float Depth => Ring.Max(v => (v - FrontA).Dot(Inward));

    /// <summary>The point just behind the middle of the front that decides the plot's use.</summary>
    public Vector2 Probe => (FrontA + FrontB) / 2 + Inward * 1.5f;
}

/// <summary>
/// Plot kinds: long strips behind a street front, corner plots on two streets, a court (an inn's yard by a gate or the
/// market, merged from neighbors), the trades' plots, and the rest.
/// </summary>
public enum ParcelKind { General, Strip, Corner, Court, Trade }

public static class ParcelKindLabels
{
    public static string Label(this ParcelKind kind) => kind switch
    {
        ParcelKind.Strip => "长条",
        ParcelKind.Corner => "街角",
        ParcelKind.Court => "大院",
        ParcelKind.Trade => "作坊",
        _ => "一般",
    };
}

public sealed record ParcelSet(IReadOnlyList<Parcel> Parcels) : ISummarized
{
    public string Summary => $"{Parcels.Count} 地块，{Parcels.Sum(p => (double)p.Area) / 10000:0.00} ha（"
        + string.Join("、", Parcels.GroupBy(p => p.Use).OrderBy(g => g.Key).Select(g => $"{g.Key.Label()} {g.Count()}")) + "）"
        + (Parcels.Any(p => p.Kind != ParcelKind.General)
            ? "；" + string.Join("、", Parcels.GroupBy(p => p.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key.Label()} {g.Count()}"))
            : "");
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
