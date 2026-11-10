using System;
using System.Collections.Generic;

namespace PcgCitySandbox.City;

public sealed class StreetMeshOptions
{
    [Param("街巷间距 m", 25, 140, 5,
        Hint = "撒点的间距，决定街巷的疏密。调小后街巷更密、街区更小")]
    public float Spacing { get; set; } = 60;
    [Param("成环概率", 0, 1, 0.05,
        Hint = "可以成环的连线被采用的概率。0 是纯树状（尽头路多），调大后环路更多")]
    public float LoopChance { get; set; } = 0.35f;
    [Param("种子", 0, 99999,
        Hint = "只影响撒点位置和成环的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;
}

/// <summary>
/// Organic street mesh inside the walls (<see cref="StreetWeave"/>): points anywhere inside, starting from the roads
/// already inside.
/// </summary>
public sealed class StreetMesh : CityNode<StreetMeshOptions>
{
    public override string Group => "路网";
    public override string Title => "街巷网";
    public override string Description =>
        "在城内按间距撒点，最近的点两两相连成树，再加少量不形成锐角的环，得到街巷。";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones];
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, StreetMeshOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var links = c.Get(Keys.RoadLinks).Copy();
        int failed = StreetWeave.Weave(zones, links, RoadZone.Inside, zones.Inside,
            k => zones.Allowed(k, RoadZone.Inside) && !zones.IsGate(k), null, o.Spacing, o.LoopChance, new Random(o.Seed),
            c.Cancellation);
        if (failed > 0)
            c.Notice($"{failed} 条候选街巷无合法通路，已跳过");
        c.Set(Keys.RoadLinks, links);
    }
}
