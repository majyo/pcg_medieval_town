using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public enum TerraceKind { Block, Landmark }

/// <summary>
/// A stretch of a road's side that a terrace fronts: the road edge, which side of it (as <see cref="RoadEdge"/>'s
/// points run, +1 or -1, the sign the plots use), and where along the edge's center line it starts and ends, in meters.
/// </summary>
public sealed record TerraceFront(int Edge, int Side, float From, float To)
{
    public float Length => To - From;
}

/// <summary>
/// A piece of ground made level for building. A block terrace is a stretch of a block along a street: <see cref="Level"/>
/// is its front, as deep as a house and its doorstep, level at <see cref="Height"/>; <see cref="Reach"/> adds the back
/// land behind it, left as it is, where plots starting on this front may grow. A landmark terrace is the landmark's
/// plot off the paving (the town hall stands partly in the market). <see cref="StreetLow"/> and
/// <see cref="StreetHigh"/> bound the street's height along the fronts (NaN for a landmark); <see cref="Ground"/> is
/// the terrain the height is held near; <see cref="Floor"/> the least height it may take (above a river's water), or
/// negative infinity. <see cref="Steep"/> marks a terrace that could not keep a limit: on a street too steep for every
/// doorstep and every step to stay within bounds, or joined as a sliver.
/// </summary>
public sealed record Terrace(int Id, TerraceKind Kind, string Of, Region Level, Region Reach, float Height,
    IReadOnlyList<TerraceFront> Fronts, float StreetLow, float StreetHigh, float Ground, float Floor, bool Steep)
{
    public float FrontLength => Fronts.Sum(f => f.Length);
}

/// <summary>
/// Where two terraces meet along a street: the point on the front, the rise from the one before to the one after in
/// the direction the edge runs, the street's rise between their middles, and whether the step is over the limit.
/// </summary>
public readonly record struct TerraceStep(Vector2 At, float Rise, float StreetRise, bool Steep);

/// <summary>
/// The town's level ground: the terraces and the steps between them along the streets, with the limits they were cut
/// to (so their contract can check them without the node's options).
/// </summary>
public sealed class TerraceSet : ISummarized
{
    private const float Bucket = 24;
    private readonly Dictionary<(int, int), List<int>> _index = new();

    public TerraceSet(IReadOnlyList<Terrace> terraces, IReadOnlyList<TerraceStep> steps, float stepMax, float doorstep,
        float maxOffset, int merges)
    {
        Terraces = terraces;
        Steps = steps;
        StepMax = stepMax;
        Doorstep = doorstep;
        MaxOffset = maxOffset;
        Merges = merges;
        for (int i = 0; i < terraces.Count; i++)
        {
            var box = terraces[i].Level.Bounds;
            for (int z = (int)MathF.Floor(box.Position.Y / Bucket); z <= (int)MathF.Floor(box.End.Y / Bucket); z++)
            {
                for (int x = (int)MathF.Floor(box.Position.X / Bucket); x <= (int)MathF.Floor(box.End.X / Bucket); x++)
                {
                    if (!_index.TryGetValue((x, z), out var list))
                        _index[(x, z)] = list = new List<int>();
                    list.Add(i);
                }
            }
        }
    }

    public static TerraceSet Empty { get; } = new([], [], 0, 0, 0, 0);

    public IReadOnlyList<Terrace> Terraces { get; }
    public IReadOnlyList<TerraceStep> Steps { get; }

    /// <summary>The largest step between neighbors along a street, in meters, steep steps aside.</summary>
    public float StepMax { get; }

    /// <summary>How far a terrace may stand above or below the street in front of it, in meters.</summary>
    public float Doorstep { get; }

    /// <summary>How far a block terrace may stand from its <see cref="Terrace.Ground"/>, in meters.</summary>
    public float MaxOffset { get; }

    /// <summary>How many times two neighboring terraces were joined into one.</summary>
    public int Merges { get; }

    /// <summary>The terrace whose level part holds <paramref name="p"/>, or null.</summary>
    public Terrace? At(Vector2 p)
    {
        if (!_index.TryGetValue(((int)MathF.Floor(p.X / Bucket), (int)MathF.Floor(p.Y / Bucket)), out var list))
            return null;
        foreach (int i in list)
        {
            var t = Terraces[i];
            if (t.Level.Bounds.HasPoint(p) && t.Level.Contains(p))
                return t;
        }

        return null;
    }

    public string Summary
    {
        get
        {
            if (Terraces.Count == 0)
                return "没有台地";
            var blocks = Terraces.Where(t => t.Kind == TerraceKind.Block).ToList();
            string steps = Steps.Count == 0 ? "沿街没有台阶" :
                $"沿街台阶 {Steps.Count} 处，平均 {Steps.Average(s => MathF.Abs(s.Rise)):0.00} m、最大 {Steps.Max(s => MathF.Abs(s.Rise)):0.00} m，"
                + $"超过上限 {Steps.Count(s => s.Steep)} 处";
            return $"台地 {blocks.Count} 块（另有地标 {Terraces.Count - blocks.Count} 块），平均临街 "
                + $"{(blocks.Count == 0 ? 0 : blocks.Average(t => t.FrontLength)):0} m；{steps}；合并 {Merges} 次";
        }
    }
}
