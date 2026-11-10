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
    [Param("种子", 0, 99999,
        Hint = "只影响房屋的进深、层数、颜色和小巷的随机选择：换一个值，按同样的规则得到另一种结果。与地图种子无关")]
    public int Seed { get; set; } = 1;

    /// <summary>The house rules for plots cut for <paramref name="use"/>.</summary>
    public HouseSpec Spec(LandUse use) => use switch
    {
        LandUse.Core => new(CoreFloorsMin, CoreFloorsMax, CoreDepthMin, CoreDepthMax, CoreAlleyChance),
        LandUse.Suburb => new(SuburbFloorsMin, SuburbFloorsMax, SuburbDepthMin, SuburbDepthMax, SuburbAlleyChance),
        _ => new(HousingFloorsMin, HousingFloorsMax, HousingDepthMin, HousingDepthMax, HousingAlleyChance),
    };
}

public readonly record struct HouseSpec(int FloorsMin, int FloorsMax, float DepthMin, float DepthMax, float AlleyChance);

/// <summary>
/// One rectangular house at the street front of every plot, with random depth and floors (one more on main
/// streets) and a gable roof; narrow, deep houses turn the gable to the street. Sometimes leaves an alley. Floors,
/// depth and the alley chance follow the plot's use: tall, deep and unbroken in the market core, low and loose in
/// the suburbs.
/// </summary>
public sealed class GableHouses : CityNode<GableHousesOptions>
{
    private const float FloorHeight = 3.2f;
    private const float MinDepth = 4;

    private static readonly (float Left, float Right)[] Trims =
        [(0, 0), (1.5f, 0), (0, 1.5f), (1.5f, 1.5f), (3, 0), (0, 3), (3, 3), (5, 0), (0, 5), (5, 5)];

    public override string Group => "建筑";
    public override string Title => "临街双坡房屋";
    public override string Description =>
        "在每个地块临街处放一栋矩形双坡顶房屋，进深和层数随机，主街上多一层；窄长的房屋山墙朝街。层数、进深和留小巷的概率按地块的用途：市场核心高而深、连排，城外街区矮而疏。";
    public override IReadOnlyList<IKey> Reads => [Keys.Parcels];
    public override IReadOnlyList<IKey> Writes => [Keys.Buildings];

    protected override void Run(NodeContext c, GableHousesOptions o)
    {
        foreach (var use in LandUses.Plots)
        {
            var spec = o.Spec(use);
            if (spec.DepthMax < spec.DepthMin || spec.FloorsMax < spec.FloorsMin)
                throw new ArgumentException($"{use.Label()}的最大值不能小于最小值");
        }

        var map = c.Terrain;
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

            float depth = spec.DepthMin + rng.NextSingle() * (spec.DepthMax - spec.DepthMin);
            int floors = rng.Next(spec.FloorsMin, spec.FloorsMax + 1) + (parcel.Street == RoadKind.Main ? 1 : 0);
            int palette = rng.Next(4);
            // A plot cut short at a corner by an earlier plot gets a narrower house before a shallower one; a plot
            // shallow everywhere gets the shallowest house.
            var corners = Fit(depth, 4) ?? Fit(MinDepth, 1);
            Vector2[]? Fit(float start, int attempts)
            {
                foreach (var (trimLeft, trimRight) in Trims)
                {
                    var a = parcel.FrontA + along * (left + trimLeft) + parcel.Inward * 0.3f;
                    var b = parcel.FrontB - along * (right + trimRight) + parcel.Inward * 0.3f;
                    if (a.DistanceTo(b) < 3 || (b - a).Dot(along) <= 0)
                        continue;

                    float tryDepth = start;
                    for (int attempt = 0; attempt < attempts && tryDepth >= MinDepth; attempt++, tryDepth *= 0.75f)
                    {
                        var candidate = new[] { a, b, b + parcel.Inward * tryDepth, a + parcel.Inward * tryDepth };
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
            var heights = corners.Select(map.HeightAt).ToArray();
            float eaves = heights.Max() + floors * FloorHeight;
            bool ridgeToStreet = width <= depth * 1.2f;
            float span = ridgeToStreet ? width : depth;
            buildings.Add(new Building(parcel.Id, corners, heights.Min() - 0.5f, eaves, eaves + span * 0.5f, ridgeToStreet, palette));
        }

        if (skipped > 0)
            c.Notice($"{skipped} 个地块放不下建筑");
        c.Set(Keys.Buildings, new BuildingSet(buildings));
    }
}
