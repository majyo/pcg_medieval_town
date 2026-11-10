using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace PcgCitySandbox.City;

public sealed class HabitabilityOptions
{
    [Param("舒适坡度 °", 0, 10, 0.5,
        Hint = "坡度在它以内不扣分，超出后（到它 + 8°）逐渐降到 0.4 倍。调大对坡地更宽容")]
    public float GentleSlope { get; set; } = 2;
    [Param("取水距离 m", 20, 400, 10,
        Hint = "离水在这个距离以内不扣分，到它的 3 倍逐渐降到 0.4 倍。调小后高分区更贴近河岸")]
    public float WaterReach { get; set; } = 150;
    [Param("安全高度 m", 1, 12, 0.5,
        Hint = "高出水面达到它才不扣分，低 2 m 以内逐渐降到 0.6 倍。调大后河边低台地得分更低")]
    public float SafeHeight { get; set; } = 4.5f;
    [Param("朝向权重", 0, 0.5, 0.05,
        Hint = "朝北坡最多扣多少（朝南不扣，平地不扣）。0 表示不考虑朝向")]
    public float AspectWeight { get; set; } = 0.2f;
}

/// <summary>
/// How good a place each buildable cell is to live, 0–1 (unbuildable cells 0): the product of a few factors, each
/// 1 at its best. Gentle ground; water within fetching distance; safely above flood level; a slight rise rather
/// than a hollow; slopes facing the sun rather than away from it; little forest to clear.
/// </summary>
public sealed class Habitability : CityNode<HabitabilityOptions>
{
    public override string Group => NodeGroups.Evaluation;
    public override string Title => "宜居度";
    public override string Description =>
        "给每块可建设的格打 0–1 分：坡缓、离水近、地势高不怕淹、略高于周围、不朝北、林少，几项相乘。";
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
