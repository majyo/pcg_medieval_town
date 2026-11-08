using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

public sealed class BuildingOptions
{
    [Param("最小进深 m", 5, 20, 0.5)] public float DepthMin { get; set; } = 8;
    [Param("最大进深 m", 6, 25, 0.5)] public float DepthMax { get; set; } = 13;
    [Param("最少层数", 1, 5)] public int FloorsMin { get; set; } = 2;
    [Param("最多层数", 1, 6)] public int FloorsMax { get; set; } = 3;
    [Param("小巷概率", 0, 1, 0.05)] public float AlleyChance { get; set; } = 0.15f;
    [Param("种子", 0, 99999)] public int Seed { get; set; } = 1;
}

/// <summary>
/// Street-front house: Corners are front-left, front-right, back-right, back-left. Walls rise from Base to
/// Eaves; a gable roof peaks at Ridge, with the ridge running front-to-back when RidgeToStreet is true.
/// </summary>
public sealed record Building(int ParcelId, Vector2[] Corners, float Base, float Eaves, float Ridge, bool RidgeToStreet, int Palette);

public sealed class BuildingSet : IStageResult
{
    public required IReadOnlyList<Building> Buildings { get; init; }
    public List<string> Notices { get; } = new();

    public string Summary => $"{Buildings.Count} 栋，占地 {Buildings.Sum(b => Footprint(b)) / 10000:0.00} ha";

    IReadOnlyList<string> IStageResult.Notices => Notices;

    public static float Footprint(Building b) =>
        b.Corners[0].DistanceTo(b.Corners[1]) * b.Corners[1].DistanceTo(b.Corners[2]);
}

public sealed class BuildingStage : Stage<BuildingOptions, BuildingSet>
{
    private const float FloorHeight = 3.2f;

    public override string Title => "05 建筑";

    protected override BuildingSet Run(StageInputs inputs, BuildingOptions o)
    {
        if (o.DepthMax < o.DepthMin || o.FloorsMax < o.FloorsMin)
            throw new ArgumentException("最大值不能小于最小值");

        var map = inputs.Terrain;
        var parcels = inputs.Get<ParcelSet>();
        var rng = new Random(o.Seed);
        var buildings = new List<Building>();
        int skipped = 0;
        foreach (var parcel in parcels.Parcels)
        {
            var region = Region.Polygon(parcel.Ring);
            var along = (parcel.FrontB - parcel.FrontA).Normalized();
            float left = 0.05f, right = 0.05f;
            if (rng.NextSingle() < o.AlleyChance)
            {
                if (rng.Next(2) == 0)
                    left = 1.5f;
                else
                    right = 1.5f;
            }

            var a = parcel.FrontA + along * left + parcel.Inward * 0.3f;
            var b = parcel.FrontB - along * right + parcel.Inward * 0.3f;
            float width = a.DistanceTo(b);
            float depth = o.DepthMin + rng.NextSingle() * (o.DepthMax - o.DepthMin);
            int floors = rng.Next(o.FloorsMin, o.FloorsMax + 1) + (parcel.Street == RoadKind.Main ? 1 : 0);
            int palette = rng.Next(4);
            Vector2[]? corners = null;
            for (int attempt = 0; attempt < 4 && width >= 3; attempt++, depth *= 0.75f)
            {
                var candidate = new[] { a, b, b + parcel.Inward * depth, a + parcel.Inward * depth };
                if (depth >= 4 && Region.Polygon(candidate).Subtract(region).Area < 0.2)
                {
                    corners = candidate;
                    break;
                }
            }

            if (corners == null)
            {
                skipped++;
                continue;
            }

            var heights = corners.Select(map.HeightAt).ToArray();
            float eaves = heights.Max() + floors * FloorHeight;
            bool ridgeToStreet = width <= depth * 1.2f;
            float span = ridgeToStreet ? width : depth;
            buildings.Add(new Building(parcel.Id, corners, heights.Min() - 0.5f, eaves, eaves + span * 0.5f, ridgeToStreet, palette));
        }

        var set = new BuildingSet { Buildings = buildings };
        if (skipped > 0)
            set.Notices.Add($"{skipped} 个地块放不下建筑");
        return set;
    }
}
