using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

public sealed class PlaceGatesOptions
{
    [Param("城门数", 1, 6)] public int Count { get; set; } = 3;
    [Param("种子", 0, 99999)] public int Seed { get; set; } = 1;
}

/// <summary>
/// Gates go on wall points that face open, passable country, one per evenly spread sector. Each gate point is
/// snapped to a site cell center (so the wall is edited) and gets a map-edge entry for its outer road.
/// </summary>
public sealed class PlaceGates : CityNode<PlaceGatesOptions>
{
    public override string Group => "城镇布局";
    public override string Title => "布置城门";
    public override IReadOnlyList<IKey> Reads => [Keys.Site, Keys.Core];
    public override IReadOnlyList<IKey> Edits => [Keys.Wall];
    public override IReadOnlyList<IKey> Writes => [Keys.Gates];

    protected override void Run(NodeContext c, PlaceGatesOptions o)
    {
        var site = c.Get(Keys.Site);
        var town = c.Get(Keys.Core);
        var core = town.Position;
        var ring = c.Get(Keys.Wall).Ring.ToArray();
        var rng = new Random(o.Seed * 31 + 7);
        float start = rng.NextSingle() * MathF.Tau;
        float sector = MathF.Tau / o.Count;
        var used = new List<int>();
        var gates = new List<Gate>();
        for (int g = 0; g < o.Count; g++)
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

                float score = open + ring[i].DistanceTo(core) / town.Radius * 5 - difference;
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
            var entry = EdgePoint(site, c.Terrain, ring[best], direction);
            gates.Add(new Gate($"gate_{gates.Count + 1}", ring[best], direction, entry));
        }

        if (gates.Count < o.Count)
            c.Notice($"只找到 {gates.Count} 处可用城门位置");
        c.Set(Keys.Wall, new WallRing(ring));
        c.Set(Keys.Gates, new GateSet(gates));
    }

    /// <summary>Where the outward ray from a gate leaves the map, pulled 10 m inside and snapped to a cell.</summary>
    private static Vector2 EdgePoint(Site site, ITerrain map, Vector2 from, Vector2 direction)
    {
        float limit = map.Half - 10;
        float tx = MathF.Abs(direction.X) < 1e-4f ? float.MaxValue : (MathF.Sign(direction.X) * limit - from.X) / direction.X;
        float tz = MathF.Abs(direction.Y) < 1e-4f ? float.MaxValue : (MathF.Sign(direction.Y) * limit - from.Y) / direction.Y;
        return site.Snap(from + direction * Math.Min(tx, tz));
    }
}
