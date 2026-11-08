using System;
using System.Collections.Generic;
using Godot;

namespace PcgCitySandbox.City;

public sealed class ChooseCoreOptions
{
    [Param("规划半径 m", 100, 450, 10)] public float Radius { get; set; } = 230;
    [Param("核心空地边长 m", 20, 90, 2)] public float Clearance { get; set; } = 46;
    [Param("种子", 0, 99999)] public int Seed { get; set; } = 1;
}

/// <summary>
/// Best spot for the town: mostly buildable land within the planned radius, near (not on) the river, toward
/// the middle of the map, with a buildable square for the plaza at the center.
/// </summary>
public sealed class ChooseCore : CityNode<ChooseCoreOptions>
{
    public override string Group => "城镇布局";
    public override string Title => "选择城址";
    public override IReadOnlyList<IKey> Reads => [Keys.Site];
    public override IReadOnlyList<IKey> Writes => [Keys.Core];

    protected override void Run(NodeContext c, ChooseCoreOptions o)
    {
        var site = c.Get(Keys.Site);
        var map = c.Terrain;
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
                if (!site.SquareBuildable(p, o.Clearance))
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
                    + Geometry.Hash(k, o.Seed) * 0.04f;
                if (score > best)
                {
                    best = score;
                    bestCell = k;
                }
            }

        if (bestCell < 0)
            throw new InvalidOperationException("地图上没有能容纳核心空地的可建设位置");
        c.Set(Keys.Core, new TownCore(site.Center(bestCell), o.Radius));
    }
}
