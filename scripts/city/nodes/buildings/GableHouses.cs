using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class GableHousesOptions
{
    [Param("市场核心 最少层数", 1, 5,
        Hint = "市场核心房屋层数的下限（主街上再加一层）。市场边的房子最高最深，连排不留缝")]
    public int CoreFloorsMin { get; set; } = 3;
    [Param("市场核心 最多层数", 1, 6,
        Hint = "市场核心房屋层数的上限（主街上再加一层）")]
    public int CoreFloorsMax { get; set; } = 4;
    [Param("市场核心 最小进深 m", 4, 20, 0.5,
        Hint = "市场核心房屋进深的下限（放不下时会自动缩短）")]
    public float CoreDepthMin { get; set; } = 10;
    [Param("市场核心 最大进深 m", 5, 25, 0.5,
        Hint = "市场核心房屋进深的上限；每栋在上下限之间随机")]
    public float CoreDepthMax { get; set; } = 15;
    [Param("市场核心 小巷概率", 0, 1, 0.05,
        Hint = "市场核心房屋一侧留出约 1.5 m 小巷的概率")]
    public float CoreAlleyChance { get; set; } = 0;
    [Param("居住 最少层数", 1, 5,
        Hint = "居住房屋层数的下限（主街上再加一层）。城内居住区")]
    public int HousingFloorsMin { get; set; } = 2;
    [Param("居住 最多层数", 1, 6,
        Hint = "居住房屋层数的上限（主街上再加一层）")]
    public int HousingFloorsMax { get; set; } = 3;
    [Param("居住 最小进深 m", 4, 20, 0.5,
        Hint = "居住房屋进深的下限（放不下时会自动缩短）")]
    public float HousingDepthMin { get; set; } = 8;
    [Param("居住 最大进深 m", 5, 25, 0.5,
        Hint = "居住房屋进深的上限；每栋在上下限之间随机")]
    public float HousingDepthMax { get; set; } = 13;
    [Param("居住 小巷概率", 0, 1, 0.05,
        Hint = "居住房屋一侧留出约 1.5 m 小巷的概率")]
    public float HousingAlleyChance { get; set; } = 0.15f;
    [Param("城外街区 最少层数", 1, 5,
        Hint = "城外街区房屋层数的下限（主街上再加一层）。城外的房子矮而浅，常有空隙")]
    public int SuburbFloorsMin { get; set; } = 1;
    [Param("城外街区 最多层数", 1, 6,
        Hint = "城外街区房屋层数的上限（主街上再加一层）")]
    public int SuburbFloorsMax { get; set; } = 2;
    [Param("城外街区 最小进深 m", 4, 20, 0.5,
        Hint = "城外街区房屋进深的下限（放不下时会自动缩短）")]
    public float SuburbDepthMin { get; set; } = 7;
    [Param("城外街区 最大进深 m", 5, 25, 0.5,
        Hint = "城外街区房屋进深的上限；每栋在上下限之间随机")]
    public float SuburbDepthMax { get; set; } = 10;
    [Param("城外街区 小巷概率", 0, 1, 0.05,
        Hint = "城外街区房屋一侧留出约 1.5 m 小巷的概率")]
    public float SuburbAlleyChance { get; set; } = 0.5f;
    [Param("作坊 最少层数", 1, 5,
        Hint = "作坊房屋层数的下限（主街上再加一层）。作坊是一两层的棚屋、窑房，屋顶比住房平")]
    public int TradeFloorsMin { get; set; } = 1;
    [Param("作坊 最多层数", 1, 6,
        Hint = "作坊房屋层数的上限（主街上再加一层）")]
    public int TradeFloorsMax { get; set; } = 2;
    [Param("作坊 最小进深 m", 4, 20, 0.5,
        Hint = "作坊房屋进深的下限（放不下时会自动缩短）")]
    public float TradeDepthMin { get; set; } = 8;
    [Param("作坊 最大进深 m", 5, 25, 0.5,
        Hint = "作坊房屋进深的上限；每栋在上下限之间随机")]
    public float TradeDepthMax { get; set; } = 12;
    [Param("作坊 最长 m", 6, 40, 0.5,
        Hint = "作坊房屋沿街的长度上限；地块更宽时房屋靠一侧，另一侧留作院子")]
    public float TradeLengthMax { get; set; } = 16;
    [Param("种子", 0, 99999,
        Hint = "只影响房屋的进深、层数、颜色和小巷的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;

    private const float TradePitch = 0.3f;

    /// <summary>The house rules for plots cut for <paramref name="use"/>.</summary>
    public HouseSpec Spec(LandUse use) => use switch
    {
        LandUse.Core => new(CoreFloorsMin, CoreFloorsMax, CoreDepthMin, CoreDepthMax, CoreAlleyChance),
        LandUse.Suburb => new(SuburbFloorsMin, SuburbFloorsMax, SuburbDepthMin, SuburbDepthMax, SuburbAlleyChance),
        _ when LandUses.Trades.Contains(use) =>
            new(TradeFloorsMin, TradeFloorsMax, TradeDepthMin, TradeDepthMax, 0, TradeLengthMax, TradePitch),
        _ => new(HousingFloorsMin, HousingFloorsMax, HousingDepthMin, HousingDepthMax, HousingAlleyChance),
    };
}

/// <param name="LengthMax">The longest a house may run along the street; a wider plot keeps a yard to one side.</param>
/// <param name="Pitch">Roof rise over the span: 0.5 is about 45°.</param>
public readonly record struct HouseSpec(int FloorsMin, int FloorsMax, float DepthMin, float DepthMax, float AlleyChance,
    float LengthMax = float.PositiveInfinity, float Pitch = 0.5f);

/// <summary>
/// One rectangular house at the street front of every plot, with random depth and floors (one more on main
/// streets) and a gable roof; narrow, deep houses turn the gable to the street. Sometimes leaves an alley. Floors,
/// depth and the alley chance follow the plot's use: tall, deep and unbroken in the market core, low and loose in
/// the suburbs; the trades get sheds with flatter roofs, at most so long, leaving a yard beside them.
/// </summary>
public sealed class GableHouses : CityNode<GableHousesOptions>
{
    private const float FloorHeight = 3.2f;
    private const float MinDepth = 4;
    private const float MaxSetBack = 12;

    private static readonly (float Left, float Right)[] Trims =
        [(0, 0), (1.5f, 0), (0, 1.5f), (1.5f, 1.5f), (3, 0), (0, 3), (3, 3), (5, 0), (0, 5), (5, 5)];

    public override string Group => NodeGroups.Buildings;
    public override string Title => "临街双坡房屋";
    public override string Description =>
        "在每个地块临街处放一栋矩形双坡顶房屋，进深和层数随机，主街上多一层；窄长的房屋山墙朝街。层数、进深和留小巷的概率按地块的用途：市场核心高而深、连排，城外街区矮而疏；作坊是屋顶较平的棚屋，旁边留出院子。";
    public override IReadOnlyList<IKey> Reads => [Keys.Parcels, Keys.Terraces, Keys.Grading];
    public override IReadOnlyList<IKey> Writes => [Keys.Buildings];

    protected override void Run(NodeContext c, GableHousesOptions o)
    {
        foreach (var use in LandUses.Plots)
        {
            var spec = o.Spec(use);
            if (spec.DepthMax < spec.DepthMin || spec.FloorsMax < spec.FloorsMin)
                throw new ArgumentException($"{use.Label()}的最大值不能小于最小值");
        }

        var ground = c.Get(Keys.Grading);
        // The floor of a house is the terrace its plot fronts on, where there are terraces.
        var terraces = c.Get(Keys.Terraces);
        var rng = new Random(o.Seed);
        var buildings = new List<Building>();
        int skipped = 0;
        foreach (var parcel in c.Get(Keys.Parcels).Parcels)
        {
            var region = Region.Polygon(parcel.Ring);
            var spec = o.Spec(parcel.Use);
            var along = (parcel.FrontB - parcel.FrontA).Normalized();
            float left = 0.05f, right = 0.05f;
            if (rng.NextSingle() < spec.AlleyChance)
            {
                if (rng.Next(2) == 0)
                    left = 1.5f;
                else
                    right = 1.5f;
            }

            float extra = Math.Max(0, parcel.Frontage - left - right - spec.LengthMax);
            bool atRight = extra > 0 && rng.Next(2) == 0;

            float depth = spec.DepthMin + rng.NextSingle() * (spec.DepthMax - spec.DepthMin);
            int floors = rng.Next(spec.FloorsMin, spec.FloorsMax + 1) + (parcel.Street == RoadKind.Main ? 1 : 0);
            int palette = rng.Next(4);
            // A plot cut short at a corner by an earlier plot gets a narrower house before a shallower one; a plot
            // shallow everywhere gets the shallowest house. A plot wider than the longest house keeps a yard: the
            // house stands at one end, or slides along the front until it fits (a bend in a mill's bank).
            // A yard's shed may also stand back from the front, where a curved plot's straight front misses it.
            float maxBack = extra > 0 ? MaxSetBack : 0;
            var corners = Slide(depth, 4) ?? Slide(MinDepth, 1);
            Vector2[]? Slide(float start, int attempts)
            {
                for (float back = 0; back <= maxBack; back += 2)
                    for (float s = 0; s <= extra; s = s < extra && s + 2 > extra ? extra : s + 2)
                    {
                        float shift = atRight ? extra - s : s;
                        if (Fit(start, attempts, shift, extra - shift, back) is { } found)
                            return found;
                    }

                return null;
            }

            Vector2[]? Fit(float start, int attempts, float shiftLeft, float shiftRight, float back)
            {
                foreach (var (trimLeft, trimRight) in Trims)
                {
                    var a = parcel.FrontA + along * (left + shiftLeft + trimLeft) + parcel.Inward * (0.3f + back);
                    var b = parcel.FrontB - along * (right + shiftRight + trimRight) + parcel.Inward * (0.3f + back);
                    if (a.DistanceTo(b) < 3 || (b - a).Dot(along) <= 0)
                        continue;

                    float tryDepth = start;
                    for (int attempt = 0; attempt < attempts && tryDepth >= MinDepth; attempt++, tryDepth *= 0.75f)
                    {
                        var candidate = new[] { a, b, b + parcel.Inward * tryDepth, a + parcel.Inward * tryDepth };
                        // Sliding tries many places; most leave a corner outside, which is cheap to see.
                        if (extra > 0 && !candidate.All(region.Contains))
                            continue;
                        if (Region.Polygon(candidate).Subtract(region).Area < 0.2)
                            return candidate;
                    }
                }

                return null;
            }

            if (corners == null)
            {
                skipped++;
                continue;
            }

            float width = corners[0].DistanceTo(corners[1]);
            depth = corners[1].DistanceTo(corners[2]);
            var heights = corners.Select(ground.HeightAt).ToArray();
            float floor = terraces.At(parcel.Probe)?.Height ?? heights.Max();
            float eaves = floor + floors * FloorHeight;
            bool ridgeToStreet = width <= depth * 1.2f;
            float span = ridgeToStreet ? width : depth;
            buildings.Add(new Building(parcel.Id, corners, MathF.Min(heights.Min(), floor) - 0.5f, eaves, eaves + span * spec.Pitch, ridgeToStreet,
                palette));
        }

        if (skipped > 0)
            c.Notice($"{skipped} 个地块放不下建筑");
        c.Set(Keys.Buildings, new BuildingSet(buildings));
    }
}
