using System;
using Godot;

namespace PcgCitySandbox.City;

public enum SiteBlock : byte { None, Water, Bank, Slope, Forest }

public static class SiteBlocks
{
    public static string Label(this SiteBlock block) => block switch
    {
        SiteBlock.None => "可建设",
        SiteBlock.Water => "水域",
        SiteBlock.Bank => "河岸退让",
        SiteBlock.Slope => "坡度过大",
        _ => "森林",
    };
}

/// <summary>Regular analysis grid over the whole map: height, slope and why a cell cannot be built on.</summary>
public sealed class Site : ISummarized
{
    public Site(Vector2 origin, float cellSize, int nx, int nz)
    {
        Origin = origin;
        CellSize = cellSize;
        Nx = nx;
        Nz = nz;
        Height = new float[nx * nz];
        Slope = new float[nx * nz];
        Suitability = new float[nx * nz];
        Block = new SiteBlock[nx * nz];
    }

    public Vector2 Origin { get; }
    public float CellSize { get; }
    public int Nx { get; }
    public int Nz { get; }
    public float[] Height { get; }
    public float[] Slope { get; }
    public float[] Suitability { get; }
    public SiteBlock[] Block { get; }
    public int Count => Nx * Nz;
    public float BuildableFraction { get; internal set; }

    public string Summary => $"{Nx}×{Nz} 格，可建设 {BuildableFraction:P0}";

    public bool Buildable(int k) => Block[k] == SiteBlock.None;
    public bool InGrid(int x, int z) => x >= 0 && z >= 0 && x < Nx && z < Nz;
    public Vector2 Center(int k) => Origin + new Vector2(k % Nx + 0.5f, k / Nx + 0.5f) * CellSize;

    public int CellAt(Vector2 p)
    {
        int x = (int)MathF.Floor((p.X - Origin.X) / CellSize);
        int z = (int)MathF.Floor((p.Y - Origin.Y) / CellSize);
        return InGrid(x, z) ? z * Nx + x : -1;
    }

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
