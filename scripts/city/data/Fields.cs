using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// Square cells over the map. The sampled fields and everything built from them share one instance (centered on
/// the origin), so they combine cell by cell.
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

    /// <summary>The cell containing <paramref name="p"/>, or the nearest one when it lies off the grid.</summary>
    public int ClampedCellAt(Vector2 p)
    {
        int x = Math.Clamp((int)MathF.Floor((p.X - Origin.X) / CellSize), 0, Nx - 1);
        int z = Math.Clamp((int)MathF.Floor((p.Y - Origin.Y) / CellSize), 0, Nz - 1);
        return Index(x, z);
    }

    public float CellArea => CellSize * CellSize;

    /// <summary>How many cells span <paramref name="meters"/>, rounded, and at least <paramref name="min"/>.</summary>
    public int Span(float meters, int min = 1) => Math.Max(min, (int)MathF.Round(meters / CellSize));

    /// <summary>How many cells make up <paramref name="hectares"/>, rounded.</summary>
    public int CellCount(float hectares) => (int)MathF.Round(hectares * 10000 / CellArea);

    public float Hectares(int cells) => cells * CellArea / 10000;

    /// <summary>Steps between two cells when diagonal steps count as one.</summary>
    public int Chebyshev(int a, int b) => Math.Max(Math.Abs(a % Nx - b % Nx), Math.Abs(a / Nx - b / Nx));

    /// <summary>Calls <paramref name="visit"/> on each side neighbor (up, down, left, right) of cell k.</summary>
    public void ForSides(int k, Action<int> visit)
    {
        int x = k % Nx, z = k / Nx;
        if (x > 0)
            visit(k - 1);
        if (x < Nx - 1)
            visit(k + 1);
        if (z > 0)
            visit(k - Nx);
        if (z < Nz - 1)
            visit(k + Nx);
    }

    /// <summary>Calls <paramref name="visit"/> on each of the up to eight neighbors of cell k (sides and corners).</summary>
    public void ForAround(int k, Action<int> visit)
    {
        int x = k % Nx, z = k / Nx;
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if ((dx != 0 || dz != 0) && InGrid(x + dx, z + dz))
                    visit(Index(x + dx, z + dz));
            }
        }
    }

    /// <summary>Whether a cell at most <paramref name="radius"/> steps from k along each axis (k included) passes the test.</summary>
    public bool AnyNear(int k, int radius, Func<int, bool> test)
    {
        int x = k % Nx, z = k / Nx;
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (InGrid(x + dx, z + dz) && test(Index(x + dx, z + dz)))
                    return true;
            }
        }

        return false;
    }

    /// <summary>The cells whose centers lie in <paramref name="area"/>.</summary>
    public List<int> CellsIn(Region area)
    {
        var cells = new List<int>();
        var bounds = area.Bounds;
        int x0 = Math.Max(0, (int)MathF.Floor((bounds.Position.X - Origin.X) / CellSize));
        int z0 = Math.Max(0, (int)MathF.Floor((bounds.Position.Y - Origin.Y) / CellSize));
        int x1 = Math.Min(Nx - 1, (int)MathF.Floor((bounds.End.X - Origin.X) / CellSize));
        int z1 = Math.Min(Nz - 1, (int)MathF.Floor((bounds.End.Y - Origin.Y) / CellSize));
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (area.Contains(Center(x, z)))
                    cells.Add(Index(x, z));
            }
        }

        return cells;
    }

    /// <summary>One flag per cell, set for <paramref name="cells"/>.</summary>
    public bool[] Mask(IEnumerable<int> cells)
    {
        var mask = new bool[Count];
        foreach (int k in cells)
            mask[k] = true;
        return mask;
    }

    /// <summary>The square of cell k, counter-clockwise from its lowest corner.</summary>
    public Vector2[] Square(int k)
    {
        var p = Center(k);
        float h = CellSize / 2;
        return [p + new Vector2(-h, -h), p + new Vector2(h, -h), p + new Vector2(h, h), p + new Vector2(-h, h)];
    }

    /// <summary>The squares of <paramref name="cells"/> merged into one region.</summary>
    public Region Squares(IEnumerable<int> cells) => Region.Polygons(cells.Select(Square));
}

/// <summary>A rectangle of whole grid cells: columns [X0, X0 + Nx) and rows [Z0, Z0 + Nz).</summary>
public readonly record struct CellRect(int X0, int Z0, int Nx, int Nz)
{
    public bool InGrid(SiteGrid grid) => X0 >= 0 && Z0 >= 0 && X0 + Nx <= grid.Nx && Z0 + Nz <= grid.Nz;

    public Vector2 Center(SiteGrid grid) => grid.Origin + new Vector2(X0 + Nx / 2f, Z0 + Nz / 2f) * grid.CellSize;

    /// <summary>The rectangle as a region, counter-clockwise from its lowest corner.</summary>
    public Region Area(SiteGrid grid)
    {
        var corner = grid.Origin + new Vector2(X0, Z0) * grid.CellSize;
        var size = new Vector2(Nx, Nz) * grid.CellSize;
        return Region.Polygon([corner, corner + new Vector2(size.X, 0), corner + size, corner + new Vector2(0, size.Y)]);
    }

    public IEnumerable<int> Cells(SiteGrid grid)
    {
        for (int z = Z0; z < Z0 + Nz; z++)
        {
            for (int x = X0; x < X0 + Nx; x++)
                yield return grid.Index(x, z);
        }
    }
}

/// <summary>
/// One measured quantity per grid cell, in physical units (m, °, m²). Cells without a meaningful value (e.g. the
/// aspect of flat ground) hold <see cref="NoData"/>. Statistics leave those cells out.
/// </summary>
public sealed class ScalarField : ISummarized
{
    /// <param name="circular">Values are angles that wrap at 360 (e.g. aspect): no interpolation, no median.</param>
    public ScalarField(SiteGrid grid, float[] values, string label, string unit, float? noData = null, bool circular = false)
    {
        if (values.Length != grid.Count)
            throw new ArgumentException($"{label}: 数值个数 {values.Length} 与格数 {grid.Count} 不符");
        Grid = grid;
        Values = values;
        Label = label;
        Unit = unit;
        NoData = noData;
        Circular = circular;

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
    public bool Circular { get; }
    public float Min { get; }
    public float Max { get; }
    public float Median { get; }

    /// <summary>Share of cells that hold a value (not <see cref="NoData"/>).</summary>
    public float DataFraction { get; }

    public string Summary => Circular
        ? $"{Label} 有值 {DataFraction:P0}"
        : $"{Label} {Format(Min)}–{Format(Max)}，中位数 {Format(Median)}" + (NoData.HasValue ? $"，无值 {1 - DataFraction:P0}" : "");

    public bool IsNoData(float v) => NoData.HasValue && v == NoData.Value;

    public string Format(float v) => IsNoData(v) ? "—" : $"{v:0.0}{(Unit is "°" or "" ? "" : " ")}{Unit}";

    /// <summary>Value of the cell containing <paramref name="p"/>, clamped to the grid.</summary>
    public float Nearest(Vector2 p) => Values[Grid.ClampedCellAt(p)];

    /// <summary>
    /// Bilinear interpolation between cell centers, clamped to the grid. Fields with no-data cells and circular
    /// fields fall back to <see cref="Nearest"/>: blending with a placeholder, or across the 360° wrap, is meaningless.
    /// </summary>
    public float Sample(Vector2 p)
    {
        if (NoData.HasValue || Circular)
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

/// <summary>
/// One category per grid cell, stored as an index into <see cref="Names"/> (e.g. why a cell cannot be built on).
/// Shares the sampled fields' grid, so categories and measurements combine cell by cell.
/// </summary>
public sealed class CategoryField : ISummarized
{
    public CategoryField(SiteGrid grid, byte[] values, string label, IReadOnlyList<string> names)
    {
        if (values.Length != grid.Count)
            throw new ArgumentException($"{label}: 数值个数 {values.Length} 与格数 {grid.Count} 不符");
        Grid = grid;
        Values = values;
        Label = label;
        Names = names;
        var counts = new int[names.Count];
        foreach (byte v in values)
        {
            if (v >= counts.Length)
                throw new ArgumentException($"{label}: 类别 {v} 超出 {names.Count} 个类别");
            counts[v]++;
        }

        Counts = counts;
    }

    public SiteGrid Grid { get; }
    public byte[] Values { get; }
    public string Label { get; }
    public IReadOnlyList<string> Names { get; }

    /// <summary>Number of cells in each category.</summary>
    public IReadOnlyList<int> Counts { get; }

    public float Fraction(int category) => Values.Length > 0 ? Counts[category] / (float)Values.Length : 0;

    /// <summary>Every category that occurs, largest first, with its share (one decimal below 10%).</summary>
    public string Summary => string.Join("，", Enumerable.Range(0, Names.Count).Where(c => Counts[c] > 0)
        .OrderByDescending(c => Counts[c]).Select(c => $"{Names[c]} {(Fraction(c) >= 0.1f ? $"{Fraction(c):P0}" : $"{Fraction(c):P1}")}"));

    /// <summary>Category of the cell containing <paramref name="p"/>, clamped to the grid.</summary>
    public byte Nearest(Vector2 p) => Values[Grid.ClampedCellAt(p)];
}
