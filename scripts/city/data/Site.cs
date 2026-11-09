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

/// <summary>
/// Buildability on its own coarse grid over the whole map: height, slope and why a cell cannot be built on. The
/// grid geometry is a <see cref="SiteGrid"/> like the sampled fields use, but not the same instance.
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
