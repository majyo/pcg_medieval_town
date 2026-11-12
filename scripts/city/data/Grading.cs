using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

/// <summary>
/// A graded road: the edge's center line resampled, the height the road surface takes at each point, and its width.
/// The surface is level across the width. <see cref="Steep"/> marks a stretch that keeps a grade over its class's
/// limit (<see cref="GradeLimit"/>, rise over run), because bringing it under would cut or fill deeper than allowed.
/// </summary>
public sealed record GradeCorridor(int Edge, RoadKind Kind, Vector2[] Points, float[] Heights, float Width, bool Steep,
    float GradeLimit)
{
    public float Length => Points.Zip(Points.Skip(1), (a, b) => a.DistanceTo(b)).Sum();

    /// <summary>The steepest grade between neighboring points, as rise over run.</summary>
    public float MaxGrade => Enumerable.Range(1, Points.Length - 1)
        .Max(i => MathF.Abs(Heights[i] - Heights[i - 1]) / MathF.Max(0.01f, Points[i].DistanceTo(Points[i - 1])));
}

/// <summary>A flat surface, level or tilted: its height at <see cref="Origin"/> and how it rises per meter along X and Z.</summary>
public readonly record struct GradePlane(Vector2 Origin, float Height, Vector2 Slope)
{
    public static GradePlane Level(float height) => new(Vector2.Zero, height, Vector2.Zero);

    public float HeightAt(Vector2 p) => Height + Slope.Dot(p - Origin);

    /// <summary>The plane's grade, as rise over run.</summary>
    public float Grade => Slope.Length();
}

/// <summary>What a platform carries; later kinds override earlier ones where they overlap.</summary>
public enum PlatformKind { Plot, Landmark, Square, Market }

/// <summary>A piece of ground made flat: its outline, its plane, and what it is for.</summary>
public sealed record GradePlatform(PlatformKind Kind, string Of, Region Area, GradePlane Plane);

/// <summary>A retaining wall along a platform's edge, holding the ground at <see cref="High"/> above <see cref="Low"/>.</summary>
public sealed record RetainingWall(Vector2[] Points, float Low, float High)
{
    public float Length => Points.Zip(Points.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
}

/// <summary>
/// The graded ground as an offset from the terrain: one value per point of a one-meter lattice (points at whole
/// meters, like the terrain's height grid at its default spacing) over a rectangle, zero everywhere else.
/// </summary>
public sealed class HeightPatch
{
    public static readonly HeightPatch Empty = new(0, 0, 0, 0, []);

    public HeightPatch(int x0, int z0, int nx, int nz, float[] offsets)
    {
        if (offsets.Length != nx * nz)
            throw new ArgumentException($"整平补丁: 数值个数 {offsets.Length} 与 {nx}×{nz} 不符");
        X0 = x0;
        Z0 = z0;
        Nx = nx;
        Nz = nz;
        Offsets = offsets;
    }

    /// <summary>World X and Z of the first lattice point, in whole meters.</summary>
    public int X0 { get; }
    public int Z0 { get; }
    public int Nx { get; }
    public int Nz { get; }
    public float[] Offsets { get; }

    public bool IsEmpty => Offsets.Length == 0;

    /// <summary>The rectangle the patch covers, in meters.</summary>
    public Rect2 Bounds => new(X0, Z0, Math.Max(0, Nx - 1), Math.Max(0, Nz - 1));

    /// <summary>The offset at lattice point (x, z) in whole meters of world position; zero off the patch.</summary>
    public float At(int x, int z) =>
        x < X0 || z < Z0 || x >= X0 + Nx || z >= Z0 + Nz ? 0 : Offsets[(z - Z0) * Nx + x - X0];

    /// <summary>Bilinear interpolation between lattice points; zero off the patch.</summary>
    public float Sample(Vector2 p)
    {
        if (IsEmpty)
            return 0;
        int x = (int)MathF.Floor(p.X), z = (int)MathF.Floor(p.Y);
        if (x < X0 - 1 || z < Z0 - 1 || x >= X0 + Nx || z >= Z0 + Nz)
            return 0;
        float u = p.X - x, v = p.Y - z;
        float top = Mathf.Lerp(At(x, z), At(x + 1, z), u);
        float bottom = Mathf.Lerp(At(x, z + 1), At(x + 1, z + 1), u);
        return Mathf.Lerp(top, bottom, v);
    }
}

/// <summary>
/// How the ground is reshaped for the town: graded roads, platforms and retaining walls (the shapes, for drawing and
/// checking), and the height patch baked from them (for reading heights). Planning reads the terrain as it was;
/// everything drawn or built reads <see cref="HeightAt"/>.
/// </summary>
public sealed class GradePlan : ISummarized
{
    public GradePlan(ITerrain terrain, IReadOnlyList<GradeCorridor> corridors, IReadOnlyList<GradePlatform> platforms,
        IReadOnlyList<RetainingWall> walls, HeightPatch patch, float earthworkLimit = 0)
    {
        EarthworkLimit = earthworkLimit;
        Terrain = terrain;
        Corridors = corridors;
        Platforms = platforms;
        Walls = walls;
        Patch = patch;
        foreach (float offset in patch.Offsets)
        {
            if (offset < 0)
            {
                MaxCut = MathF.Max(MaxCut, -offset);
                CutVolume -= offset;
            }
            else
            {
                MaxFill = MathF.Max(MaxFill, offset);
                FillVolume += offset;
            }
        }
    }

    public ITerrain Terrain { get; }
    public IReadOnlyList<GradeCorridor> Corridors { get; }
    public IReadOnlyList<GradePlatform> Platforms { get; }
    public IReadOnlyList<RetainingWall> Walls { get; }
    public HeightPatch Patch { get; }

    /// <summary>How far a road's center line may be cut below or filled above the terrain, in meters.</summary>
    public float EarthworkLimit { get; }

    /// <summary>The deepest cut and the highest fill, in meters.</summary>
    public float MaxCut { get; }
    public float MaxFill { get; }

    /// <summary>Earth cut away and filled in, in cubic meters (one lattice point stands for a square meter).</summary>
    public double CutVolume { get; }
    public double FillVolume { get; }

    /// <summary>The ungraded plan: no shapes, the ground as the terrain has it.</summary>
    public static GradePlan None(ITerrain terrain) => new(terrain, [], [], [], HeightPatch.Empty);

    /// <summary>Ground height after grading.</summary>
    public float HeightAt(Vector2 p) => Terrain.HeightAt(p) + Patch.Sample(p);

    /// <summary>Length of the corridors marked steep, in meters.</summary>
    public float SteepLength => Corridors.Where(c => c.Steep).Sum(c => c.Length);

    public string Summary
    {
        get
        {
            if (Corridors.Count == 0 && Platforms.Count == 0)
                return "地面未修整";
            string roads = Corridors.Count == 0 ? "" : $"道路 {Corridors.Sum(c => c.Length) / 1000:0.0} km，"
                + $"最大坡度 {Corridors.Where(c => !c.Steep).Select(c => c.MaxGrade).DefaultIfEmpty(0).Max() * 100:0.0}%，"
                + $"陡坡段 {SteepLength:0} m；";
            string platforms = Platforms.Count == 0 ? "" : $"平台 {Platforms.Count} 块，挡土墙 {Walls.Sum(w => w.Length):0} m；";
            return roads + platforms + $"最深挖 {MaxCut:0.0} m，最高填 {MaxFill:0.0} m，挖 {CutVolume:0} m³，填 {FillVolume:0} m³";
        }
    }
}
