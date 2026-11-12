using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class BridgesOptions
{
    [Param("净空 m", 1, 6, 0.1,
        Hint = "桥面至少高出水位多少。调大后桥更高，两岸的引道更长")]
    public float Clearance { get; set; } = 2.5f;
    [Param("最大跨度 m", 5, 30, 1,
        Hint = "相邻两个桥墩（或桥墩与桥台）之间最远多少。调小后桥墩更多")]
    public float MaxSpan { get; set; } = 12;
    [Param("桥台高出水位 m", 0.3, 3, 0.1,
        Hint = "桥台放在岸上地面高出水位这么多的地方。调大后桥更长，桥台离水更远")]
    public float AbutmentRise { get; set; } = 1;
}

/// <summary>
/// A bridge at every crossing a road uses. The deck is level, high enough to clear the water and to meet the banks;
/// its abutments stand back from the water's edge where the bank has risen above the water; piers divide the water
/// into spans. The road's approaches are graded by the next node, which takes the deck's height as fixed.
/// </summary>
public sealed class Bridges : CityNode<BridgesOptions>
{
    /// <summary>Distance between the stations of a road, shared with the road profiles.</summary>
    public const float Station = 2;

    /// <summary>How far back from the water an abutment is looked for.</summary>
    private const float AbutmentReach = 30;

    public override string Group => NodeGroups.Grading;
    public override string Title => "桥";
    public override string Description =>
        "在道路过河的每一处架桥：桥面水平，高出水位留出净空；桥台退到岸上高出水面的地方；按最大跨度在水里立桥墩。两岸的引道由道路纵断面接上。";
    public override IReadOnlyList<IKey> Reads => [Keys.Crossings, Keys.Roads, Keys.Shore];
    public override IReadOnlyList<IKey> Writes => [Keys.Bridges];

    protected override void Run(NodeContext c, BridgesOptions o)
    {
        var crossings = c.Get(Keys.Crossings).Crossings;
        var shore = c.Get(Keys.Shore);
        var terrain = c.Terrain;
        bool Water(Vector2 p) => shore.Nearest(p) < 0;
        int reach = (int)MathF.Ceiling(AbutmentReach / Station);

        var bridges = new List<Bridge>();
        foreach (var edge in c.Get(Keys.Roads).Edges)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            var points = Geometry.Stations(edge.Points, Station);
            for (int i = 0; i < points.Length;)
            {
                if (!Water(points[i]))
                {
                    i++;
                    continue;
                }

                int wetFirst = i, wetLast = i;
                while (wetLast + 1 < points.Length && Water(points[wetLast + 1]))
                    wetLast++;
                i = wetLast + 1;
                var middle = points[(wetFirst + wetLast) / 2];
                var crossing = crossings.MinBy(x => x.Center.DistanceSquaredTo(middle));
                if (crossing == null)
                    continue;

                float water = terrain.NearestRiver(middle).WaterHeight;
                int first = Abutment(wetFirst - 1, -1), last = Abutment(wetLast + 1, 1);
                if (last - first < 2)
                    continue;
                float height = MathF.Max(water + o.Clearance, MathF.Max(terrain.HeightAt(points[first]), terrain.HeightAt(points[last])));

                // The deck runs straight from abutment to abutment, whatever small steps the road's line takes between
                // them. Piers split the water under that line into equal spans no longer than allowed.
                Vector2[] deck = [points[first], points[last]];
                var over = Geometry.Samples(deck, 0.5f).Where(Water).ToList();
                var piers = Array.Empty<Vector2>();
                if (over.Count > 1)
                {
                    int spans = Math.Max(1, (int)MathF.Ceiling(over[0].DistanceTo(over[^1]) / o.MaxSpan));
                    piers = Enumerable.Range(1, spans - 1).Select(s => over[0].Lerp(over[^1], s / (float)spans)).ToArray();
                }

                bridges.Add(new Bridge(crossing.Id, edge.Id, first, last, deck, height, edge.Width, piers,
                    water, o.Clearance, o.MaxSpan));

                // The first dry station, walking away from the water, where the bank stands high enough; the road's own
                // end, or the farthest looked at, if it never does.
                int Abutment(int from, int step)
                {
                    int k = Math.Clamp(from, 0, points.Length - 1);
                    for (int walked = 0; walked < reach && k + step >= 0 && k + step < points.Length; walked++, k += step)
                    {
                        if (!Water(points[k]) && terrain.HeightAt(points[k]) >= water + o.AbutmentRise)
                            break;
                    }

                    return k;
                }
            }
        }

        if (bridges.Count == 0 && crossings.Count > 0)
            c.Notice("没有道路过河，不架桥");
        c.Set(Keys.Bridges, new BridgeSet(bridges));
    }
}
