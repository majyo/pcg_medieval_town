using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class ChooseOriginOptions
{
    [Param("评价半径 m", 40, 400, 10,
        Hint = "按起源点周围多大范围的平均宜居度打分（方窗半边长）")]
    public float Radius { get; set; } = 150;
    [Param("路口加分", 0, 0.5, 0.01,
        Hint = "离路口在“路口范围”以内时加的分")]
    public float JunctionBonus { get; set; } = 0.15f;
    [Param("路口范围 m", 10, 200, 5,
        Hint = "离路口多近算“在路口”")]
    public float JunctionReach { get; set; } = 60;
    [Param("桥头加分", 0, 0.5, 0.01,
        Hint = "离使用中的渡口在“桥头范围”以内时加的分")]
    public float BridgeBonus { get; set; } = 0.2f;
    [Param("桥头范围 m", 50, 500, 10,
        Hint = "离使用中的渡口多近算“在桥头”")]
    public float BridgeReach { get; set; } = 220;
    [Param("离图心惩罚", 0, 0.5, 0.01,
        Hint = "按离图心的距离线性扣分，在图边中点扣这么多。调大后城镇更靠近地图中央")]
    public float CenterWeight { get; set; } = 0.15f;
    [Param("离图边最小距离 m", 100, 600, 10,
        Hint = "起源点离图边至少多远，给城镇留出生长的空间")]
    public float EdgeMargin { get; set; } = 320;
    [Param("种子", 0, 99999,
        Hint = "只影响得分相近的候选之间的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;
}

/// <summary>
/// Where the town starts: a buildable cell on a through route with good land around it (mean habitability over a
/// square window), better still by a river crossing or at a junction, and not too far out.
/// </summary>
public sealed class ChooseOrigin : CityNode<ChooseOriginOptions>
{
    public override string Group => NodeGroups.Regional;
    public override string Title => "起源点";
    public override string Description =>
        "在过境道路上选出城镇最早出现的地点：周围宜居度高，靠近渡口桥头或路口更好，离图心不太远。";
    public override IReadOnlyList<IKey> Reads => [Keys.Routes, Keys.Crossings, Keys.Habitability, Keys.Buildable];
    public override IReadOnlyList<IKey> Writes => [Keys.Origin];

    protected override void Run(NodeContext c, ChooseOriginOptions o)
    {
        var network = c.Get(Keys.Routes);
        var habitability = c.Get(Keys.Habitability);
        var buildable = c.Get(Keys.Buildable);
        var grid = habitability.Grid;
        var bridges = c.Get(Keys.Crossings).UsedBy(network).Select(x => x.Center).ToList();
        var junctions = network.Junctions.Select(k => grid.Center(k)).ToList();
        var sums = new SummedArea(grid, habitability.Values);
        int r = grid.Span(o.Radius);
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
    }
}
