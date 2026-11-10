using System;
using System.Collections.Generic;
using Godot;

namespace PcgCitySandbox.City;

public sealed class SampleGridOptions
{
    [Param("格距 m", 2, 16, 1,
        Hint = "采样网格的格边长。调小更精细（耗时约按格数增长，格距减半约慢 4 倍）；调大更快但会漏掉窄的地形")]
    public float CellSize { get; set; } = 4;
}

/// <summary>Chooses the grid every site field is sampled on: square cells covering the map, centered on the origin.</summary>
public sealed class SampleGrid : CityNode<SampleGridOptions>
{
    public override string Group => NodeGroups.Sampling;
    public override string Title => "采样网格";
    public override string Description =>
        "确定所有采样场共用的方格网：格距越小越精细，但所有下游节点都会变慢。";
    public override IReadOnlyList<IKey> Writes => [Keys.Grid];

    protected override void Run(NodeContext c, SampleGridOptions o)
    {
        int n = (int)MathF.Ceiling(c.Terrain.Half * 2 / o.CellSize - 1e-4f);
        var origin = -new Vector2(n, n) * o.CellSize / 2;
        c.Set(Keys.Grid, new SiteGrid(origin, o.CellSize, n, n));
    }
}
