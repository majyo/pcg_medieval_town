using System;
using System.Collections.Generic;
using Godot;

namespace PcgCitySandbox.Terrain;

/// <summary>
/// Headless terrain checks for fixed seeds: the height grid is sound, it is the one source of
/// <see cref="TemperateMapData.HeightAt"/>, the fast river query matches the exhaustive one, and generation is
/// reproducible. Returns 0 when everything passes.
/// </summary>
public static class TerrainChecks
{
    private static readonly int[] Seeds = { 73129, 7, 22017 };

    /// <summary>Steepest allowed rise between neighboring height samples (rise over run).</summary>
    private const float MaxGradient = 1.0f;

    public static int Run(MapSettings settings)
    {
        var failures = new List<string>();
        foreach (int seed in Seeds)
        {
            var map = TemperateMapData.Create(settings, seed);
            GD.Print($"seed={seed} 地形: {map.HeightSide}×{map.HeightSide} 高度网格，间距 {map.HeightSpacing:0.###} m，" +
                $"高程 {map.MinHeight:0.0}–{map.MaxHeight:0.0} m，生成 {map.GenerationSeconds:0.00} s");
            foreach (var error in Invariants(map))
                failures.Add($"seed={seed} {error}");
        }

        var first = TemperateMapData.Create(settings, Seeds[0]);
        var again = TemperateMapData.Create(settings, Seeds[0]);
        if (Fingerprint(first) != Fingerprint(again))
            failures.Add("同一种子两次生成的地形不一致");

        foreach (var failure in failures)
            GD.PrintErr("TERRAIN CHECK FAILED: " + failure);
        GD.Print(failures.Count == 0 ? "TERRAIN CHECKS PASSED" : $"TERRAIN CHECKS FAILED ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }

    public static List<string> Invariants(TemperateMapData map)
    {
        var errors = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition && errors.Count < 20)
                errors.Add(message);
        }

        int n = map.HeightSide;
        Require(n % 2 == 1, "高度网格边长应为奇数（以原点为中心）");
        Require(Mathf.IsEqualApprox((n - 1) * map.HeightSpacing, map.Half * 2), "高度网格未恰好覆盖地图");
        Require(map.HeightSpacing <= map.Settings.HeightSpacingMeters * 1.01f, "高度网格间距大于设定值");

        float steepest = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                float h = map.HeightGrid[map.HeightIndex(x, z)];
                if (!float.IsFinite(h))
                {
                    Require(false, $"高度网格 ({x}, {z}) 不是有限值");
                    continue;
                }

                if (x + 1 < n)
                    steepest = MathF.Max(steepest, MathF.Abs(map.HeightGrid[map.HeightIndex(x + 1, z)] - h));
                if (z + 1 < n)
                    steepest = MathF.Max(steepest, MathF.Abs(map.HeightGrid[map.HeightIndex(x, z + 1)] - h));
            }

        Require(steepest / map.HeightSpacing <= MaxGradient, $"相邻高度点坡度过陡 {steepest / map.HeightSpacing:0.00}");

        // HeightAt must reproduce the grid at its nodes, and the analysis heights must come from HeightAt.
        var rng = new Random(map.Settings.Seed);
        for (int i = 0; i < 2000; i++)
        {
            int x = rng.Next(n), z = rng.Next(n);
            float stored = map.HeightGrid[map.HeightIndex(x, z)];
            Require(MathF.Abs(map.HeightAt(map.HeightPosition(x, z)) - stored) < 1e-3f, $"HeightAt 与高度网格不一致 ({x}, {z})");
        }

        for (int k = 0; k < map.Heights.Length; k += 97)
        {
            var p = map.GridPosition(k % map.Side, k / map.Side);
            Require(MathF.Abs(map.Heights[k] - map.HeightAt(p)) < 1e-3f, $"分析网格高度与 HeightAt 不一致 {p}");
        }

        // The fast nearest-river search must agree with testing every segment.
        for (int i = 0; i < 2000; i++)
        {
            var p = new Vector2((float)(rng.NextDouble() * 2 - 1) * map.Half * 1.1f, (float)(rng.NextDouble() * 2 - 1) * map.Half * 1.1f);
            var fast = map.NearestRiver(p);
            var exact = map.NearestRiverExhaustive(p);
            Require(MathF.Abs(fast.Distance - exact.Distance) < 1e-3f && MathF.Abs(fast.WaterHeight - exact.WaterHeight) < 1e-3f,
                $"最近河段查询结果错误 {p}: {fast.Distance} ≠ {exact.Distance}");
            var capped = map.NearestRiver(p, 200);
            Require(exact.Distance < 200 ? MathF.Abs(capped.Distance - exact.Distance) < 1e-3f : float.IsPositiveInfinity(capped.Distance),
                $"限距最近河段查询结果错误 {p}: {capped.Distance} vs {exact.Distance}");
        }

        return errors;
    }

    private static ulong Fingerprint(TemperateMapData map)
    {
        ulong hash = 14695981039346656037;
        foreach (float h in map.HeightGrid)
            hash = (hash ^ BitConverter.SingleToUInt32Bits(h)) * 1099511628211;
        return hash ^ (ulong)map.Trees.Count;
    }
}
