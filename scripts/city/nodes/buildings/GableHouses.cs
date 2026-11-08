using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class GableHousesOptions
{
    [Param("最小进深 m", 5, 20, 0.5)] public float DepthMin { get; set; } = 8;
    [Param("最大进深 m", 6, 25, 0.5)] public float DepthMax { get; set; } = 13;
    [Param("最少层数", 1, 5)] public int FloorsMin { get; set; } = 2;
    [Param("最多层数", 1, 6)] public int FloorsMax { get; set; } = 3;
    [Param("小巷概率", 0, 1, 0.05)] public float AlleyChance { get; set; } = 0.15f;
    [Param("种子", 0, 99999)] public int Seed { get; set; } = 1;
}

/// <summary>
/// One rectangular house at the street front of every plot, with random depth and floors (one more on main
/// streets) and a gable roof; narrow, deep houses turn the gable to the street. Sometimes leaves an alley.
/// </summary>
public sealed class GableHouses : CityNode<GableHousesOptions>
{
    private const float FloorHeight = 3.2f;

    public override string Group => "建筑";
    public override string Title => "临街双坡房屋";
    public override IReadOnlyList<IKey> Reads => [Keys.Parcels];
    public override IReadOnlyList<IKey> Writes => [Keys.Buildings];

    protected override void Run(NodeContext c, GableHousesOptions o)
    {
        if (o.DepthMax < o.DepthMin || o.FloorsMax < o.FloorsMin)
            throw new ArgumentException("最大值不能小于最小值");

        var map = c.Terrain;
        var rng = new Random(o.Seed);
        var buildings = new List<Building>();
        int skipped = 0;
        foreach (var parcel in c.Get(Keys.Parcels).Parcels)
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

        if (skipped > 0)
            c.Notice($"{skipped} 个地块放不下建筑");
        c.Set(Keys.Buildings, new BuildingSet(buildings));
    }
}
