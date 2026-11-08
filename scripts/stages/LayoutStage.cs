using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.Core;

namespace PcgCitySandbox.Stages;

public sealed class LayoutOptions
{
    [Param("城墙半径 m", 100, 450, 10)] public float Radius { get; set; } = 230;
    [Param("广场边长 m", 20, 90, 2)] public float PlazaSize { get; set; } = 46;
    [Param("城门数", 1, 6)] public int Gates { get; set; } = 3;
    [Param("种子", 0, 99999)] public int Seed { get; set; } = 1;
}

public sealed record Gate(string Id, Vector2 Position, Vector2 Outward, Vector2 Entry);

/// <summary>Town core, market plaza, wall ring and gates. Everything is snapped to site cell centers.</summary>
public sealed class TownLayout : IStageResult
{
    public required Vector2 Core { get; init; }
    public required float PlazaSize { get; init; }
    public required Region Plaza { get; init; }
    public required Vector2[] Wall { get; init; }
    public required Region Interior { get; init; }
    public required IReadOnlyList<Gate> Gates { get; init; }
    public List<string> Notices { get; } = new();

    public float WallLength => Wall.Select((p, i) => p.DistanceTo(Wall[(i + 1) % Wall.Length])).Sum();

    public string Summary =>
        $"城内 {Interior.Area / 10000:0.0} ha，城墙 {WallLength:0} m，城门 {Gates.Count} 座";

    IReadOnlyList<string> IStageResult.Notices => Notices;
}

public sealed class LayoutStage : Stage<LayoutOptions, TownLayout>
{
    private const int RingPoints = 40;

    public override string Title => "02 城镇布局";

    protected override TownLayout Run(StageInputs inputs, LayoutOptions o)
    {
        var site = inputs.Get<Site>();
        var map = inputs.Map;
        var core = ChooseCore(site, map, o);
        var ring = TraceWall(site, core, o);
        var gates = PlaceGates(site, map, core, ring, o);
        var half = o.PlazaSize / 2;
        var plaza = Region.Polygon(new[]
        {
            core + new Vector2(-half, -half), core + new Vector2(half, -half),
            core + new Vector2(half, half), core + new Vector2(-half, half),
        });

        var layout = new TownLayout
        {
            Core = core,
            PlazaSize = o.PlazaSize,
            Plaza = plaza,
            Wall = ring,
            Interior = Region.Polygon(ring),
            Gates = gates,
        };
        if (gates.Count < o.Gates)
            layout.Notices.Add($"只找到 {gates.Count} 处可用城门位置");
        return layout;
    }

    /// <summary>Best spot for the town: mostly buildable land within the wall radius, near (not on) the river.</summary>
    private static Vector2 ChooseCore(Site site, TemperateMapData map, LayoutOptions o)
    {
        int stride = Math.Max(1, (int)(o.Radius / 3 / site.CellSize));
        int sample = Math.Max(1, (int)(o.Radius / 12 / site.CellSize));
        int reach = (int)(o.Radius / site.CellSize);
        float margin = o.Radius + 80;
        float best = float.NegativeInfinity;
        int bestCell = -1;
        for (int z = 0; z < site.Nz; z += stride)
            for (int x = 0; x < site.Nx; x += stride)
            {
                int k = z * site.Nx + x;
                var p = site.Center(k);
                if (MathF.Abs(p.X) > map.Half - margin || MathF.Abs(p.Y) > map.Half - margin)
                    continue;
                if (!PlazaFits(site, p, o.PlazaSize))
                    continue;

                int total = 0, open = 0;
                float suitability = 0;
                for (int dz = -reach; dz <= reach; dz += sample)
                    for (int dx = -reach; dx <= reach; dx += sample)
                    {
                        if (dx * dx + dz * dz > reach * reach || !site.InGrid(x + dx, z + dz))
                            continue;
                        int j = (z + dz) * site.Nx + x + dx;
                        total++;
                        if (site.Buildable(j))
                        {
                            open++;
                            suitability += site.Suitability[j];
                        }
                    }

                float river = map.NearestRiver(p).Distance;
                float score = open / (float)total * 0.7f + suitability / Math.Max(1, total) * 0.3f
                    + (river > o.Radius * 0.6f && river < o.Radius * 1.6f ? 0.08f : 0)
                    - 0.25f * p.Length() / map.Half
                    + Hash(k, o.Seed) * 0.04f;
                if (score > best)
                {
                    best = score;
                    bestCell = k;
                }
            }

        if (bestCell < 0)
            throw new InvalidOperationException("地图上没有能容纳广场的可建设位置");
        return site.Center(bestCell);
    }

    private static bool PlazaFits(Site site, Vector2 center, float size)
    {
        for (float dz = -size / 2; dz <= size / 2; dz += site.CellSize / 2)
            for (float dx = -size / 2; dx <= size / 2; dx += site.CellSize / 2)
            {
                int k = site.CellAt(center + new Vector2(dx, dz));
                if (k < 0 || !site.Buildable(k))
                    return false;
            }

        return true;
    }

    /// <summary>Star-shaped ring around the core that stops before unbuildable land (river banks, steep ground).</summary>
    private static Vector2[] TraceWall(Site site, Vector2 core, LayoutOptions o)
    {
        var rng = new Random(o.Seed);
        float p1 = rng.NextSingle() * MathF.Tau, p2 = rng.NextSingle() * MathF.Tau, p3 = rng.NextSingle() * MathF.Tau;
        float minimum = o.PlazaSize * 0.75f + site.CellSize * 2;
        var radii = new float[RingPoints];
        for (int i = 0; i < RingPoints; i++)
        {
            float angle = i * MathF.Tau / RingPoints;
            float target = o.Radius * (1 + 0.10f * MathF.Sin(2 * angle + p1) + 0.07f * MathF.Sin(3 * angle + p2)
                + 0.04f * MathF.Sin(5 * angle + p3));
            var direction = Vector2.FromAngle(angle);
            float r = minimum;
            for (float t = minimum; t <= target; t += site.CellSize / 2)
            {
                int k = site.CellAt(core + direction * t);
                if (k < 0 || !site.Buildable(k))
                {
                    r = Math.Max(minimum, t - site.CellSize);
                    break;
                }

                r = t;
            }

            radii[i] = r;
        }

        return Enumerable.Range(0, RingPoints).Select(i =>
        {
            float average = (radii[(i + RingPoints - 1) % RingPoints] + radii[i] + radii[(i + 1) % RingPoints]) / 3;
            return core + Vector2.FromAngle(i * MathF.Tau / RingPoints) * Math.Min(radii[i], average);
        }).ToArray();
    }

    /// <summary>Gates go on ring points that face open, passable country; each gets a map-edge entry point.</summary>
    private static List<Gate> PlaceGates(Site site, TemperateMapData map, Vector2 core, Vector2[] ring, LayoutOptions o)
    {
        var rng = new Random(o.Seed * 31 + 7);
        float start = rng.NextSingle() * MathF.Tau;
        float sector = MathF.Tau / o.Gates;
        var used = new List<int>();
        var gates = new List<Gate>();
        for (int g = 0; g < o.Gates; g++)
        {
            float target = start + g * sector + (rng.NextSingle() - 0.5f) * sector * 0.3f;
            int best = -1;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < ring.Length; i++)
            {
                float angle = i * MathF.Tau / ring.Length;
                float difference = MathF.Abs(Mathf.AngleDifference(angle, target));
                if (difference > sector * 0.35f || used.Any(u => Math.Min(Math.Abs(u - i), ring.Length - Math.Abs(u - i)) < 3))
                    continue;

                var outward = (ring[i] - core).Normalized();
                int open = 0;
                for (int step = 1; step <= 25; step++)
                {
                    int k = site.CellAt(ring[i] + outward * step * site.CellSize);
                    if (k < 0 || site.Block[k] is SiteBlock.Water || site.Slope[k] > 15)
                        break;
                    open++;
                }

                float score = open + ring[i].DistanceTo(core) / o.Radius * 5 - difference;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best < 0)
                continue;

            used.Add(best);
            ring[best] = site.Snap(ring[best]);
            var direction = (ring[best] - core).Normalized();
            gates.Add(new Gate($"gate_{gates.Count + 1}", ring[best], direction, EdgePoint(site, map, ring[best], direction)));
        }

        return gates;
    }

    /// <summary>Where the outward ray from a gate leaves the map, pulled 10 m inside and snapped to a cell.</summary>
    private static Vector2 EdgePoint(Site site, TemperateMapData map, Vector2 from, Vector2 direction)
    {
        float limit = map.Half - 10;
        float tx = MathF.Abs(direction.X) < 1e-4f ? float.MaxValue : (MathF.Sign(direction.X) * limit - from.X) / direction.X;
        float tz = MathF.Abs(direction.Y) < 1e-4f ? float.MaxValue : (MathF.Sign(direction.Y) * limit - from.Y) / direction.Y;
        return site.Snap(from + direction * Math.Min(tx, tz));
    }

    private static float Hash(int k, int seed)
    {
        unchecked
        {
            uint h = (uint)(k * 73856093 ^ seed * 19349663);
            h ^= h >> 16;
            h *= 0x7feb352d;
            h ^= h >> 15;
            return (h & 0xffff) / 65535f;
        }
    }
}
