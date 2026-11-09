using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>Whether a cell can be built on and, if not, the first reason that rules it out (in this order).</summary>
public enum SiteBlock : byte { None, Water, Flood, Bank, Slope, Forest }

public static class SiteBlocks
{
    public static string Label(this SiteBlock block) => block switch
    {
        SiteBlock.None => "可建设",
        SiteBlock.Water => "水域",
        SiteBlock.Flood => "易淹",
        SiteBlock.Bank => "河岸退让",
        SiteBlock.Slope => "坡度过大",
        _ => "森林",
    };

    /// <summary>Category names of a buildability <see cref="CategoryField"/>, indexed by <see cref="SiteBlock"/>.</summary>
    public static readonly IReadOnlyList<string> Names = Enum.GetValues<SiteBlock>().Select(b => b.Label()).ToArray();

    public static SiteBlock Block(this CategoryField buildable, int k) => (SiteBlock)buildable.Values[k];
    public static bool Buildable(this CategoryField buildable, int k) => buildable.Values[k] == (byte)SiteBlock.None;
}

/// <summary>
/// Buildability over the whole map as the road, block and layout nodes still read it: height, slope, a 0–1
/// suitability and why a cell cannot be built on, all on the sampling grid. Built from the evaluation keys by
/// <see cref="LegacySite"/> until those nodes read the evaluation keys directly.
/// </summary>
public sealed class Site : ISummarized
{
    public Site(SiteGrid grid)
    {
        Grid = grid;
        Height = new float[grid.Count];
        Slope = new float[grid.Count];
        Suitability = new float[grid.Count];
        Block = new SiteBlock[grid.Count];
    }

    public SiteGrid Grid { get; }
    public float[] Height { get; }
    public float[] Slope { get; }
    public float[] Suitability { get; }
    public SiteBlock[] Block { get; }
    public float BuildableFraction { get; internal set; }

    public Vector2 Origin => Grid.Origin;
    public float CellSize => Grid.CellSize;
    public int Nx => Grid.Nx;
    public int Nz => Grid.Nz;
    public int Count => Grid.Count;

    public string Summary => $"{Nx}×{Nz} 格，可建设 {BuildableFraction:P0}";

    public bool Buildable(int k) => Block[k] == SiteBlock.None;
    public bool InGrid(int x, int z) => Grid.InGrid(x, z);
    public Vector2 Center(int k) => Grid.Center(k);
    public int CellAt(Vector2 p) => Grid.CellAt(p);

    public Vector2 Snap(Vector2 p)
    {
        int k = CellAt(p);
        return k < 0 ? p : Center(k);
    }

    /// <summary>True when every half-cell sample of the axis-aligned square is on buildable land.</summary>
    public bool SquareBuildable(Vector2 center, float size)
    {
        for (float dz = -size / 2; dz <= size / 2; dz += CellSize / 2)
            for (float dx = -size / 2; dx <= size / 2; dx += CellSize / 2)
            {
                int k = CellAt(center + new Vector2(dx, dz));
                if (k < 0 || !Buildable(k))
                    return false;
            }

        return true;
    }
}
