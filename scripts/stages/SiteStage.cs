using System;
using System.Collections.Generic;
using Godot;
using PcgCitySandbox.Core;

namespace PcgCitySandbox.Stages;

public interface IStageResult
{
    string Summary { get; }
    IReadOnlyList<string> Notices { get; }
}

public enum SiteBlock : byte { None, Water, Bank, Slope, Forest }

public sealed class SiteOptions
{
    [Param("网格间距 m", 4, 20, 1)] public float CellSize { get; set; } = 8;
    [Param("建设坡度上限 °", 2, 30, 0.5)] public float MaxSlope { get; set; } = 9;
    [Param("河岸退让 m", 0, 80, 1)] public float RiverSetback { get; set; } = 14;
    [Param("森林覆盖上限", 0, 1, 0.05)] public float ForestLimit { get; set; } = 0.55f;
}

/// <summary>Regular analysis grid over the whole map: height, slope and why a cell cannot be built on.</summary>
public sealed class Site : IStageResult
{
    public Site(Vector2 origin, float cellSize, int nx, int nz)
    {
        Origin = origin;
        CellSize = cellSize;
        Nx = nx;
        Nz = nz;
        Height = new float[nx * nz];
        Slope = new float[nx * nz];
        Suitability = new float[nx * nz];
        Block = new SiteBlock[nx * nz];
    }

    public Vector2 Origin { get; }
    public float CellSize { get; }
    public int Nx { get; }
    public int Nz { get; }
    public float[] Height { get; }
    public float[] Slope { get; }
    public float[] Suitability { get; }
    public SiteBlock[] Block { get; }
    public int Count => Nx * Nz;
    public float BuildableFraction { get; internal set; }

    public string Summary => $"{Nx}×{Nz} 格，可建设 {BuildableFraction:P0}";
    public IReadOnlyList<string> Notices { get; } = Array.Empty<string>();

    public bool Buildable(int k) => Block[k] == SiteBlock.None;
    public bool InGrid(int x, int z) => x >= 0 && z >= 0 && x < Nx && z < Nz;
    public Vector2 Center(int k) => Origin + new Vector2(k % Nx + 0.5f, k / Nx + 0.5f) * CellSize;

    public int CellAt(Vector2 p)
    {
        int x = (int)MathF.Floor((p.X - Origin.X) / CellSize);
        int z = (int)MathF.Floor((p.Y - Origin.Y) / CellSize);
        return InGrid(x, z) ? z * Nx + x : -1;
    }

    public Vector2 Snap(Vector2 p)
    {
        int k = CellAt(p);
        return k < 0 ? p : Center(k);
    }
}

public sealed class SiteStage : Stage<SiteOptions, Site>
{
    public override string Title => "01 场地分析";

    protected override Site Run(StageInputs inputs, SiteOptions o)
    {
        var map = inputs.Map;
        float size = map.Settings.SizeMeters;
        int n = (int)MathF.Floor(size / o.CellSize);
        var site = new Site(new Vector2(-map.Half, -map.Half), o.CellSize, n, n);
        int buildable = 0;
        for (int k = 0; k < site.Count; k++)
        {
            if (k % 4096 == 0)
                inputs.Cancellation.ThrowIfCancellationRequested();

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
        return site;
    }
}
