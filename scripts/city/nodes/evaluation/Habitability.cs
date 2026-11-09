using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace PcgCitySandbox.City;

public sealed class HabitabilityOptions
{
    [Param("舒适坡度 °", 0, 10, 0.5)] public float GentleSlope { get; set; } = 2;
    [Param("取水距离 m", 20, 400, 10)] public float WaterReach { get; set; } = 150;
    [Param("安全高度 m", 1, 12, 0.5)] public float SafeHeight { get; set; } = 4.5f;
    [Param("朝向权重", 0, 0.5, 0.05)] public float AspectWeight { get; set; } = 0.2f;
}

/// <summary>
/// How good a place each buildable cell is to live, 0–1 (unbuildable cells 0): the product of a few factors, each
/// 1 at its best. Gentle ground; water within fetching distance; safely above flood level; a slight rise rather
/// than a hollow; slopes facing the sun rather than away from it; little forest to clear.
/// </summary>
public sealed class Habitability : CityNode<HabitabilityOptions>
{
    public override string Group => Evaluation.Group;
    public override string Title => "宜居度";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Buildable, Keys.Slope, Keys.Aspect, Keys.Shore, Keys.AboveWater, Keys.Relief, Keys.Forest];
    public override IReadOnlyList<IKey> Writes => [Keys.Habitability];

    protected override void Run(NodeContext c, HabitabilityOptions o)
    {
        var buildable = c.Get(Keys.Buildable);
        var slope = c.Get(Keys.Slope).Values;
        var aspect = c.Get(Keys.Aspect);
        var shore = c.Get(Keys.Shore).Values;
        var above = c.Get(Keys.AboveWater).Values;
        var relief = c.Get(Keys.Relief).Values;
        var forest = c.Get(Keys.Forest).Values;
        var grid = buildable.Grid;
        var score = new float[grid.Count];
        var options = new ParallelOptions { CancellationToken = c.Cancellation };
        Parallel.For(0, grid.Count, options, k =>
        {
            if (!buildable.Buildable(k))
                return;
            float gentle = Mathf.Lerp(1, 0.4f, Step(o.GentleSlope, o.GentleSlope + 8, slope[k]));
            float water = Mathf.Lerp(1, 0.4f, Step(o.WaterReach, o.WaterReach * 3, shore[k]));
            float dry = Mathf.Lerp(0.6f, 1, Step(o.SafeHeight - 2, o.SafeHeight, above[k]));
            float rise = Mathf.Lerp(0.75f, 1, Step(-2, 2, relief[k]));
            // Facing north (aspect 0°) costs up to the weight on clearly sloping ground; facing south costs nothing.
            float facing = aspect.IsNoData(aspect.Values[k]) ? 0
                : (MathF.Cos(aspect.Values[k] * MathF.PI / 180) + 1) / 2 * Step(1, 6, slope[k]);
            float sun = 1 - o.AspectWeight * facing;
            float clear = 1 - 0.3f * forest[k];
            score[k] = gentle * water * dry * rise * sun * clear;
        });
        c.Set(Keys.Habitability, new ScalarField(grid, score, "宜居度", ""));
    }

    private static float Step(float from, float to, float v) => Mathf.SmoothStep(0, 1, Mathf.Clamp((v - from) / (to - from), 0, 1));
}
