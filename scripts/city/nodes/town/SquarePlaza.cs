using System.Collections.Generic;
using Godot;

namespace PcgCitySandbox.City;

public sealed class SquarePlazaOptions
{
    [Param("广场边长 m", 20, 90, 2,
        Hint = "广场的目标边长；地形不允许时会自动缩小")]
    public float Size { get; set; } = 46;
}

/// <summary>Axis-aligned square market plaza on the town core, shrunk until it stands on buildable land.</summary>
public sealed class SquarePlaza : CityNode<SquarePlazaOptions>
{
    private const float MinSize = 12;

    public override string Group => "城镇布局";
    public override string Title => "方形广场";
    public override string Description =>
        "在城址中心放一个方形广场；放不下时逐步缩小，直到全在可建设地上。";
    public override IReadOnlyList<IKey> Reads => [Keys.Site, Keys.Core];
    public override IReadOnlyList<IKey> Writes => [Keys.Plaza];

    protected override void Run(NodeContext c, SquarePlazaOptions o)
    {
        var site = c.Get(Keys.Site);
        var core = c.Get(Keys.Core).Position;
        float size = o.Size;
        while (size > MinSize && !site.SquareBuildable(core, size))
            size -= 2;
        if (size < o.Size)
            c.Notice($"城址周围空地不足，广场缩小到 {size:0} m");

        float half = size / 2;
        var area = Region.Polygon(new[]
        {
            core + new Vector2(-half, -half), core + new Vector2(half, -half),
            core + new Vector2(half, half), core + new Vector2(-half, half),
        });
        c.Set(Keys.Plaza, new Plaza(area, core));
    }
}
