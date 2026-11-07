using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PcgCitySandbox.Pcg;

[Flags]
public enum EnvironmentRestriction { None = 0, NoGroundData = 1, UnknownLayer = 2, Water = 4, TooSteep = 8, NoBuildZone = 16, Occupied = 32 }
public enum BuildCandidateState { Candidate, Blocked, Unknown }
public readonly record struct EnvironmentCell(float? MaxSlope, bool? Water, float? Vegetation,
    EnvironmentRestriction Reasons, BuildCandidateState State, bool CompleteBaseData);

public sealed record EnvironmentSummary(int CandidateCells, int BlockedCells, int UnknownCells, int CompleteCells,
    float MinHeight, float MaxHeight, int ProbeCount, int SourceQueryCount, double ElapsedSeconds,
    ReadOnlyDictionary<EnvironmentRestriction, int> ReasonCounts);

public sealed class CityEnvironmentData
{
    private readonly float?[] _heights;
    private readonly EnvironmentCell[] _cells;
    public string SourceId { get; }
    public long SourceRevision { get; }
    public float? NativeSpacing { get; }
    public EnvironmentLayers Layers { get; }
    public EnvironmentSampleOptions Options { get; }
    public Rect2 Bounds => Options.Bounds;
    public int CellsX { get; }
    public int CellsZ { get; }
    public float StepX => Bounds.Size.X / CellsX;
    public float StepZ => Bounds.Size.Y / CellsZ;
    public double CellArea => (double)StepX * StepZ;
    public EnvironmentSummary Summary { get; }
    public ReadOnlyCollection<ConstraintPolygon> NoBuildZones { get; }
    public ReadOnlyCollection<ConstraintPolygon> Obstacles { get; }

    // Arrays are owned by the sampler and transferred once, never exposed to consumers.
    internal CityEnvironmentData(string id, long revision, float? nativeSpacing, EnvironmentLayers layers,
        EnvironmentSampleOptions options, int x, int z, float?[] heights, EnvironmentCell[] cells,
        ConstraintPolygon[] noBuild, ConstraintPolygon[] obstacles, EnvironmentSummary summary)
    {
        SourceId = id; SourceRevision = revision; NativeSpacing = nativeSpacing; Layers = layers; Options = options;
        CellsX = x; CellsZ = z; _heights = heights; _cells = cells;
        NoBuildZones = Array.AsReadOnly(noBuild); Obstacles = Array.AsReadOnly(obstacles); Summary = summary;
    }

    public Vector2 VertexPosition(int x, int z)
    {
        CheckIndex(x, z, CellsX + 1, CellsZ + 1);
        return new(x == CellsX ? Bounds.End.X : Bounds.Position.X + x * StepX,
            z == CellsZ ? Bounds.End.Y : Bounds.Position.Y + z * StepZ);
    }
    public float? VertexHeight(int x, int z)
    { CheckIndex(x, z, CellsX + 1, CellsZ + 1); return _heights[z * (CellsX + 1) + x]; }
    public EnvironmentCell Cell(int x, int z)
    { CheckIndex(x, z, CellsX, CellsZ); return _cells[z * CellsX + x]; }
    private static void CheckIndex(int x, int z, int width, int depth)
    { if (x < 0 || z < 0 || x >= width || z >= depth) throw new ArgumentOutOfRangeException(nameof(x), "采样索引越界。"); }
    public bool Contains(Vector2 p) => ConstraintPolygon.Inside(Bounds, p);

    public bool TryGetCell(Vector2 p, out EnvironmentCell cell)
    {
        cell = default;
        if (!Contains(p)) return false;
        var (x, z, _, _) = Coordinates(p); cell = Cell(x, z); return true;
    }

    public bool TryHeightAt(Vector2 p, out float height)
    {
        height = 0;
        if (!Contains(p)) return false;
        var (x, z, u, v) = Coordinates(p);
        float? a = VertexHeight(x, z), b = VertexHeight(x + 1, z), c = VertexHeight(x, z + 1), d = VertexHeight(x + 1, z + 1);
        if (u + v <= 1)
        {
            if (!a.HasValue || !b.HasValue || !c.HasValue) return false;
            height = a.Value + (b.Value - a.Value) * u + (c.Value - a.Value) * v;
        }
        else
        {
            if (!b.HasValue || !c.HasValue || !d.HasValue) return false;
            height = d.Value + (c.Value - d.Value) * (1 - u) + (b.Value - d.Value) * (1 - v);
        }
        return true;
    }

    private (int X, int Z, float U, float V) Coordinates(Vector2 p)
    {
        float gx = (p.X - Bounds.Position.X) / StepX, gz = (p.Y - Bounds.Position.Y) / StepZ;
        int x = Math.Min((int)gx, CellsX - 1), z = Math.Min((int)gz, CellsZ - 1);
        return (x, z, Mathf.Clamp(gx - x, 0, 1), Mathf.Clamp(gz - z, 0, 1));
    }
}
