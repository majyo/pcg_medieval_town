using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>One phase of the town's growth: its name and how many cells it added.</summary>
public sealed record GrowthPhase(string Name, int Cells, float Area);

/// <summary>
/// When each cell of the town was built: <see cref="Phase"/> holds the phase number (1 for the first, 0 for never).
/// Phases nest, so the town as it stood after phase n is every cell with a phase from 1 to n. Each phase node copies
/// the history and adds its cells with <see cref="Add"/>.
/// </summary>
public sealed class GrowthHistory : ISummarized
{
    private static readonly string[] Numerals = { "一", "二", "三", "四", "五", "六", "七", "八", "九" };

    public GrowthHistory(SiteGrid grid, byte[] phase, IReadOnlyList<GrowthPhase> phases)
    {
        if (phase.Length != grid.Count)
            throw new ArgumentException($"生长记录: 格数 {phase.Length} 与网格 {grid.Count} 不符");
        Grid = grid;
        Phase = phase;
        Phases = phases;
    }

    public SiteGrid Grid { get; }
    public byte[] Phase { get; }
    public IReadOnlyList<GrowthPhase> Phases { get; }

    /// <summary>Number of the latest phase (0 before any).</summary>
    public int Latest => Phases.Count;

    public bool Built(int k) => Phase[k] != 0;

    /// <summary>The built cells as one region (cell squares merged), computed once.</summary>
    public Region Footprint => _footprint ??= Squares(Enumerable.Range(0, Phase.Length).Where(Built));

    private Region? _footprint;

    private Region Squares(IEnumerable<int> cells)
    {
        float h = Grid.CellSize / 2;
        return Region.Polygons(cells.Select(k => Grid.Center(k)).Select(p => new[]
        {
            p + new Vector2(-h, -h), p + new Vector2(h, -h), p + new Vector2(h, h), p + new Vector2(-h, h),
        }));
    }

    /// <summary>Built area after phase <paramref name="n"/>, in m².</summary>
    public float AreaAfter(int n) => Phases.Take(n).Sum(p => p.Area);

    /// <summary>The cells added in phase <paramref name="n"/>.</summary>
    public IEnumerable<int> CellsOf(int n)
    {
        for (int k = 0; k < Phase.Length; k++)
        {
            if (Phase[k] == n)
                yield return k;
        }
    }

    /// <summary>A copy with one more phase made of <paramref name="cells"/>, which must all be unbuilt.</summary>
    public GrowthHistory Add(string name, IReadOnlyCollection<int> cells)
    {
        var phase = (byte[])Phase.Clone();
        byte n = (byte)(Latest + 1);
        foreach (int k in cells)
        {
            if (phase[k] != 0)
                throw new InvalidOperationException($"格 {k} 已在第 {phase[k]} 期建成");
            phase[k] = n;
        }

        float area = cells.Count * Grid.CellSize * Grid.CellSize;
        return new GrowthHistory(Grid, phase, Phases.Append(new GrowthPhase(name, cells.Count, area)).ToArray());
    }

    public static GrowthHistory Empty(SiteGrid grid) => new(grid, new byte[grid.Count], Array.Empty<GrowthPhase>());

    /// <summary>"一期", "二期", ...</summary>
    public static string Ordinal(int n) => (n >= 1 && n <= Numerals.Length ? Numerals[n - 1] : n.ToString()) + "期";

    /// <summary>The phase's name with its number, e.g. "二期 市镇".</summary>
    public string Label(int n) => $"{Ordinal(n)} {Phases[n - 1].Name}";

    public string Summary => Phases.Count == 0 ? "尚未建成"
        : string.Join("，", Enumerable.Range(1, Latest).Select(n => $"{Label(n)} 累计 {AreaAfter(n) / 10000:0.0} ha"));
}

public enum LandmarkKind { Church }

/// <summary>
/// A plot set aside for a public building: an axis-aligned rectangle of whole grid cells (cell indices
/// [X0, X0 + Nx) × [Z0, Z0 + Nz)), its building's facing (a church's altar end), and the phase it belongs to.
/// </summary>
public sealed record Landmark(string Id, LandmarkKind Kind, Region Plot, Vector2 Center, Vector2 Axis, int X0, int Z0, int Nx, int Nz,
    int Phase)
{
    public string KindLabel => Kind switch
    {
        _ => "教堂",
    };

    /// <summary>The grid cells of the plot.</summary>
    public IEnumerable<int> Cells(SiteGrid grid)
    {
        for (int z = Z0; z < Z0 + Nz; z++)
        {
            for (int x = X0; x < X0 + Nx; x++)
                yield return grid.Index(x, z);
        }
    }
}

public sealed class LandmarkSet : ISummarized
{
    public LandmarkSet(IReadOnlyList<Landmark> landmarks) => Landmarks = landmarks;

    public IReadOnlyList<Landmark> Landmarks { get; }

    /// <summary>Every plot together.</summary>
    public Region Plots => Landmarks.Aggregate(Region.Empty, (all, l) => all.Union(l.Plot));

    public string Summary => Landmarks.Count == 0 ? "没有地标"
        : string.Join("，", Landmarks.Select(l =>
            $"{l.KindLabel} {l.Plot.Bounds.Size.X:0}×{l.Plot.Bounds.Size.Y:0} m（{GrowthHistory.Ordinal(l.Phase)}）"));
}
