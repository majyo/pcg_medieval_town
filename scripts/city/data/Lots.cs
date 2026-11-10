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

public enum FenceKind { Wall, Hedge }

/// <summary>A run of plot boundary off the street: a stone wall inside the town wall, a hedge outside it.</summary>
public sealed record Fence(Vector2[] Points, FenceKind Kind)
{
    public float Length => Points.Zip(Points.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
}

public enum OutbuildingKind { Store, Workshop, Barn, Stable, Shed }

/// <summary>
/// A back building at the rear of a plot: Corners run front-left, front-right, back-right, back-left as a house's do.
/// FacesBack when a road runs behind the plot, so its door opens onto that road.
/// </summary>
public sealed record Outbuilding(int ParcelId, Vector2[] Corners, OutbuildingKind Kind, bool FacesBack)
{
    public float Footprint => Corners[0].DistanceTo(Corners[1]) * Corners[1].DistanceTo(Corners[2]);

    public string Label => Kind switch
    {
        OutbuildingKind.Store => "仓房",
        OutbuildingKind.Workshop => "作坊间",
        OutbuildingKind.Barn => "畜棚",
        OutbuildingKind.Stable => "马厩",
        _ => "料棚",
    };
}

/// <summary>A plot's kitchen garden: what is left behind and beside its buildings.</summary>
public sealed record Garden(int ParcelId, Region Area);

public sealed record YardSet(IReadOnlyList<Fence> Fences, IReadOnlyList<Outbuilding> Outbuildings, IReadOnlyList<Garden> Gardens)
    : ISummarized
{
    public string Summary => $"院墙 {Fences.Where(f => f.Kind == FenceKind.Wall).Sum(f => f.Length) / 1000:0.0} km，"
        + $"篱笆 {Fences.Where(f => f.Kind == FenceKind.Hedge).Sum(f => f.Length) / 1000:0.0} km；后屋 {Outbuildings.Count} 间"
        + (Outbuildings.Count == 0 ? "" : "（" + string.Join("、", Outbuildings.GroupBy(b => b.Kind).OrderBy(g => g.Key)
            .Select(g => $"{g.First().Label} {g.Count()}")) + "）")
        + $"；菜园 {Gardens.Count} 块 {Gardens.Sum(g => g.Area.Area) / 10000:0.00} ha";
}
