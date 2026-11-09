using System;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// The sampling grid every site field shares: square cells over the whole map, centered on the origin. Fields on
/// the same grid instance have the same cell numbering, so they combine cell by cell.
/// </summary>
public sealed class SiteGrid : ISummarized
{
    public SiteGrid(Vector2 origin, float cellSize, int nx, int nz)
    {
        Origin = origin;
        CellSize = cellSize;
        Nx = nx;
        Nz = nz;
    }

    public Vector2 Origin { get; }
    public float CellSize { get; }
    public int Nx { get; }
    public int Nz { get; }
    public int Count => Nx * Nz;
    public float CellArea => CellSize * CellSize;
    public Vector2 Size => new(Nx * CellSize, Nz * CellSize);

    public string Summary => $"{Nx}×{Nz} 格，{CellSize:0.#} m";

    public bool InGrid(int x, int z) => x >= 0 && z >= 0 && x < Nx && z < Nz;
    public int Index(int x, int z) => z * Nx + x;
    public Vector2 Center(int x, int z) => Origin + new Vector2(x + 0.5f, z + 0.5f) * CellSize;
    public Vector2 Center(int k) => Center(k % Nx, k / Nx);

    public int CellAt(Vector2 p)
    {
        int x = (int)MathF.Floor((p.X - Origin.X) / CellSize);
        int z = (int)MathF.Floor((p.Y - Origin.Y) / CellSize);
        return InGrid(x, z) ? Index(x, z) : -1;
    }
}

/// <summary>
/// One measured quantity per grid cell, in physical units (m, °, m²). Cells without a meaningful value (e.g. the
/// aspect of flat ground) hold <see cref="NoData"/>. Statistics leave those cells out.
/// </summary>
public sealed class ScalarField : ISummarized
{
    public ScalarField(SiteGrid grid, float[] values, string label, string unit, float? noData = null)
    {
        if (values.Length != grid.Count)
            throw new ArgumentException($"{label}: 数值个数 {values.Length} 与格数 {grid.Count} 不符");
        Grid = grid;
        Values = values;
        Label = label;
        Unit = unit;
        NoData = noData;

        var valid = Array.FindAll(values, v => !IsNoData(v));
        Array.Sort(valid);
        Min = valid.Length > 0 ? valid[0] : 0;
        Max = valid.Length > 0 ? valid[^1] : 0;
        Median = valid.Length > 0 ? valid[valid.Length / 2] : 0;
        DataFraction = values.Length > 0 ? valid.Length / (float)values.Length : 0;
    }

    public SiteGrid Grid { get; }
    public float[] Values { get; }
    public string Label { get; }
    public string Unit { get; }
    public float? NoData { get; }
    public float Min { get; }
    public float Max { get; }
    public float Median { get; }

    /// <summary>Share of cells that hold a value (not <see cref="NoData"/>).</summary>
    public float DataFraction { get; }

    public string Summary => $"{Label} {Format(Min)}–{Format(Max)}，中位数 {Format(Median)}" +
        (NoData.HasValue ? $"，无值 {1 - DataFraction:P0}" : "");

    public bool IsNoData(float v) => NoData.HasValue && v == NoData.Value;

    public string Format(float v) => IsNoData(v) ? "—" : $"{v:0.0}{(Unit is "°" or "" ? "" : " ")}{Unit}";

    /// <summary>Value of the cell containing <paramref name="p"/>, clamped to the grid.</summary>
    public float Nearest(Vector2 p)
    {
        int x = Math.Clamp((int)MathF.Floor((p.X - Grid.Origin.X) / Grid.CellSize), 0, Grid.Nx - 1);
        int z = Math.Clamp((int)MathF.Floor((p.Y - Grid.Origin.Y) / Grid.CellSize), 0, Grid.Nz - 1);
        return Values[Grid.Index(x, z)];
    }

    /// <summary>
    /// Bilinear interpolation between cell centers, clamped to the grid. Fields with no-data cells fall back to
    /// <see cref="Nearest"/>, since blending with a placeholder value would be meaningless.
    /// </summary>
    public float Sample(Vector2 p)
    {
        if (NoData.HasValue)
            return Nearest(p);
        float gx = Math.Clamp((p.X - Grid.Origin.X) / Grid.CellSize - 0.5f, 0, Grid.Nx - 1.001f);
        float gz = Math.Clamp((p.Y - Grid.Origin.Y) / Grid.CellSize - 0.5f, 0, Grid.Nz - 1.001f);
        int ix = (int)gx, iz = (int)gz;
        int jx = Math.Min(ix + 1, Grid.Nx - 1), jz = Math.Min(iz + 1, Grid.Nz - 1);
        float u = gx - ix, v = gz - iz;
        float top = Mathf.Lerp(Values[Grid.Index(ix, iz)], Values[Grid.Index(jx, iz)], u);
        float bottom = Mathf.Lerp(Values[Grid.Index(ix, jz)], Values[Grid.Index(jx, jz)], u);
        return Mathf.Lerp(top, bottom, v);
    }
}
