using System;
using System.Collections.Generic;
using Godot;

namespace PcgCitySandbox.City;

public sealed class SiteAnalysisOptions
{
    [Param("网格间距 m", 4, 20, 1)] public float CellSize { get; set; } = 8;
    [Param("建设坡度上限 °", 2, 30, 0.5)] public float MaxSlope { get; set; } = 9;
    [Param("河岸退让 m", 0, 80, 1)] public float RiverSetback { get; set; } = 14;
    [Param("森林覆盖上限", 0, 1, 0.05)] public float ForestLimit { get; set; } = 0.55f;
}

/// <summary>Samples the terrain on a regular grid and records why each cell cannot be built on.</summary>
public sealed class SiteAnalysis : CityNode<SiteAnalysisOptions>
{
    public override string Group => "场地";
    public override string Title => "场地分析";
    public override IReadOnlyList<IKey> Writes => [Keys.Site];

    protected override void Run(NodeContext c, SiteAnalysisOptions o)
    {
        var map = c.Terrain;
        int n = (int)MathF.Floor(map.Half * 2 / o.CellSize);
        var site = new Site(new SiteGrid(new Vector2(-map.Half, -map.Half), o.CellSize, n, n));
        int buildable = 0;
        for (int k = 0; k < site.Count; k++)
        {
            if (k % 4096 == 0)
                c.Cancellation.ThrowIfCancellationRequested();

            var p = site.Center(k);
            var sample = map.Sample(p);
            var river = map.NearestRiver(p);
            site.Height[k] = sample.Height;
            site.Slope[k] = sample.SlopeDegrees;
            site.Suitability[k] = sample.Suitability;
            site.Block[k] = sample.IsWater || river.Distance < river.HalfWidth ? SiteBlock.Water
                : river.Distance < river.HalfWidth + o.RiverSetback ? SiteBlock.Bank
                : sample.SlopeDegrees > o.MaxSlope ? SiteBlock.Slope
                : sample.Forest > o.ForestLimit ? SiteBlock.Forest
                : SiteBlock.None;
            if (site.Block[k] == SiteBlock.None)
                buildable++;
        }

        site.BuildableFraction = buildable / (float)site.Count;
        c.Set(Keys.Site, site);
    }
}
