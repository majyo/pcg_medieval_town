using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class ChooseOriginOptions
{
    [Param("评价半径 m", 40, 400, 10)] public float Radius { get; set; } = 150;
    [Param("路口加分", 0, 0.5, 0.01)] public float JunctionBonus { get; set; } = 0.15f;
    [Param("路口范围 m", 10, 200, 5)] public float JunctionReach { get; set; } = 60;
    [Param("桥头加分", 0, 0.5, 0.01)] public float BridgeBonus { get; set; } = 0.2f;
    [Param("桥头范围 m", 50, 500, 10)] public float BridgeReach { get; set; } = 220;
    [Param("离图心惩罚", 0, 0.5, 0.01)] public float CenterWeight { get; set; } = 0.15f;
    [Param("离图边最小距离 m", 100, 600, 10)] public float EdgeMargin { get; set; } = 320;
    [Param("过渡：规划半径 m", 100, 450, 10)] public float TownRadius { get; set; } = 230;
    [Param("种子", 0, 99999)] public int Seed { get; set; } = 1;
}

/// <summary>
/// Where the town starts: a buildable cell on a through route with good land around it (mean habitability over a
/// square window), better still by a river crossing or at a junction, and not too far out. Until the town grows from
/// its origin, the origin also becomes the planned town core (with a fixed radius) for the layout nodes.
/// </summary>
public sealed class ChooseOrigin : CityNode<ChooseOriginOptions>
{
    public override string Group => RegionalFrame.Group;
    public override string Title => "起源点";
    public override IReadOnlyList<IKey> Reads => [Keys.Routes, Keys.Crossings, Keys.Habitability, Keys.Buildable];
    public override IReadOnlyList<IKey> Writes => [Keys.Origin, Keys.Core];

    protected override void Run(NodeContext c, ChooseOriginOptions o)
    {
        var network = c.Get(Keys.Routes);
        var habitability = c.Get(Keys.Habitability);
        var buildable = c.Get(Keys.Buildable);
        var grid = habitability.Grid;
        var used = network.UsedCrossings.ToHashSet();
        var bridges = c.Get(Keys.Crossings).Crossings.Where(x => used.Contains(x.Id)).Select(x => x.Center).ToList();
        var junctions = network.Junctions.Select(k => grid.Center(k)).ToList();
        var sums = new SummedArea(grid, habitability.Values);
        int r = Math.Max(1, (int)MathF.Round(o.Radius / grid.CellSize));
        float half = c.Terrain.Half;

        int best = -1;
        float bestScore = float.NegativeInfinity;
        OriginKind bestKind = OriginKind.Roadside;
        foreach (int k in network.Cells.OrderBy(k => k))
        {
            var p = grid.Center(k);
            if (!buildable.Buildable(k) || MathF.Abs(p.X) > half - o.EdgeMargin || MathF.Abs(p.Y) > half - o.EdgeMargin)
                continue;
            bool bridge = bridges.Any(b => b.DistanceTo(p) <= o.BridgeReach);
            bool junction = junctions.Any(j => j.DistanceTo(p) <= o.JunctionReach);
            float score = (float)sums.Mean(k % grid.Nx, k / grid.Nx, r)
                + (bridge ? o.BridgeBonus : 0) + (junction ? o.JunctionBonus : 0)
                - o.CenterWeight * p.Length() / half
                + Geometry.Hash(k, o.Seed) * 0.02f;
            if (score > bestScore)
            {
                bestScore = score;
                best = k;
                bestKind = bridge ? OriginKind.Bridgehead : junction ? OriginKind.Junction : OriginKind.Roadside;
            }
        }

        if (best < 0)
            throw new InvalidOperationException("过境道路上没有离图边足够远的可建设位置");
        var position = grid.Center(best);
        var routes = network.Routes.Where(route => route.Cells.Contains(best)).Select(route => route.Id).ToList();
        c.Set(Keys.Origin, new TownOrigin(position, best, bestKind, routes));
        c.Set(Keys.Core, new TownCore(position, o.TownRadius));
    }
}
