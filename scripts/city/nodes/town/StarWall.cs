using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class StarWallOptions
{
    [Param("轮廓起伏", 0, 3, 0.1,
        Hint = "城墙轮廓偏离正圆的程度。0 是圆，调大后凹凸更明显")]
    public float Wobble { get; set; } = 1;
    [Param("种子", 0, 99999,
        Hint = "只影响城墙的凹凸形状的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;
}

/// <summary>
/// Star-shaped ring around the core: a wobbly circle of the planned radius, each ray stopping before unbuildable
/// land (river banks, steep ground), then lightly smoothed. Never closer to the core than the plaza needs.
/// </summary>
public sealed class StarWall : CityNode<StarWallOptions>
{
    private const int RingPoints = 40;

    public override string Group => TownGrowth.Group;
    public override string Title => "星形城墙";
    public override string Description =>
        "过渡用：围绕城址画一圈带起伏的城墙，每条射线遇到不可建设地就收缩，再轻度平滑。";
    public override IReadOnlyList<IKey> Reads => [Keys.Site, Keys.Core, Keys.Plaza];
    public override IReadOnlyList<IKey> Writes => [Keys.Wall];

    protected override void Run(NodeContext c, StarWallOptions o)
    {
        var site = c.Get(Keys.Site);
        var town = c.Get(Keys.Core);
        var plaza = c.Get(Keys.Plaza).Area.Bounds.Size;
        var core = town.Position;
        var rng = new Random(o.Seed);
        float p1 = rng.NextSingle() * MathF.Tau, p2 = rng.NextSingle() * MathF.Tau, p3 = rng.NextSingle() * MathF.Tau;
        float minimum = Math.Max(plaza.X, plaza.Y) * 0.75f + site.CellSize * 2;
        var radii = new float[RingPoints];
        for (int i = 0; i < RingPoints; i++)
        {
            float angle = i * MathF.Tau / RingPoints;
            float wobble = 0.10f * MathF.Sin(2 * angle + p1) + 0.07f * MathF.Sin(3 * angle + p2) + 0.04f * MathF.Sin(5 * angle + p3);
            float target = town.Radius * (1 + o.Wobble * wobble);
            var direction = Vector2.FromAngle(angle);
            float r = minimum;
            for (float t = minimum; t <= target; t += site.CellSize / 2)
            {
                int k = site.CellAt(core + direction * t);
                if (k < 0 || !site.Buildable(k))
                {
                    r = Math.Max(minimum, t - site.CellSize);
                    break;
                }

                r = t;
            }

            radii[i] = r;
        }

        var ring = Enumerable.Range(0, RingPoints).Select(i =>
        {
            float average = (radii[(i + RingPoints - 1) % RingPoints] + radii[i] + radii[(i + 1) % RingPoints]) / 3;
            return core + Vector2.FromAngle(i * MathF.Tau / RingPoints) * Math.Min(radii[i], average);
        }).ToArray();
        c.Set(Keys.Wall, new WallRing(ring));
    }
}
