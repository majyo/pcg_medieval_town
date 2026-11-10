using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class CountrysideOptions
{
    [Param("菜园范围 m", 0, 400, 10,
        Hint = "离城外街区或城墙空地这么近的可建设地是菜园果园")]
    public float GardenReach { get; set; } = 120;
    [Param("耕作半径 m", 200, 3000, 50,
        Hint = "离城路程（按通行代价）这么远以内的平地是耕地，更远的是牧场")]
    public float FarmRadius { get; set; } = 1200;
    [Param("葡萄园半径 m", 0, 3000, 50,
        Hint = "离城路程这么远以内、朝南有坡的地是葡萄园")]
    public float VineRadius { get; set; } = 1200;
}

/// <summary>
/// Gives every cell still unassigned an agricultural use, in this order: woodland where the forest stands, meadow on
/// the low ground by the water, gardens and orchards next to the town, vineyards on south slopes, arable fields on
/// level ground within reach, pasture on the rest of the passable land, waste where nothing goes.
/// </summary>
public sealed class Countryside : CityNode<CountrysideOptions>
{
    private const float MeadowShore = 30, MeadowHeight = 2;
    private const float VineAspectFrom = 135, VineAspectTo = 225, VineSlopeFrom = 4, VineSlopeTo = 15;
    private const float ArableSlope = 6;

    public override string Group => NodeGroups.Zoning;
    public override string Title => "农业带";
    public override string Description =>
        "把剩下的格全部分完：有森林的是林地，河边低地是草场，紧挨城的是菜园果园，南坡是葡萄园，近处的平地是耕地，其余能走到的是牧场，剩下的是荒地。";
    public override IReadOnlyList<IKey> Reads =>
    [
        Keys.LandUse, Keys.Reach, Keys.Slope, Keys.Aspect, Keys.Shore, Keys.AboveWater, Keys.Buildable, Keys.TravelCost,
    ];
    public override IReadOnlyList<IKey> Edits => [Keys.LandUse];

    protected override void Run(NodeContext c, CountrysideOptions o)
    {
        var land = c.Get(Keys.LandUse);
        var reach = c.Get(Keys.Reach);
        var slope = c.Get(Keys.Slope);
        var aspect = c.Get(Keys.Aspect);
        var shore = c.Get(Keys.Shore);
        var above = c.Get(Keys.AboveWater);
        var buildable = c.Get(Keys.Buildable);
        var travel = c.Get(Keys.TravelCost);
        var grid = land.Grid;
        var use = land.Edit();
        var town = land.CellsOf(LandUse.Suburb).Concat(land.CellsOf(LandUse.WallStrip));
        var nearTown = Proximity.From(grid, town, o.GardenReach);

        for (int k = 0; k < grid.Count; k++)
        {
            if (use[k] == (byte)LandUse.Unassigned)
                use[k] = (byte)Use(k);
        }

        c.Set(Keys.LandUse, LandUses.Field(grid, use));

        LandUse Use(int k)
        {
            float r = reach.Values[k];
            bool reached = !reach.IsNoData(r);
            if (buildable.Block(k) == SiteBlock.Forest)
                return LandUse.Woodland;
            if (IsMeadow(buildable, shore, above, k))
                return LandUse.Meadow;
            if (buildable.Buildable(k) && float.IsFinite(nearTown[k]))
                return LandUse.Garden;
            if (reached && r <= o.VineRadius && IsVineSlope(slope, aspect, k))
                return LandUse.Vineyard;
            if (reached && r <= o.FarmRadius && slope.Values[k] <= ArableSlope)
                return LandUse.Arable;
            return travel.IsNoData(travel.Values[k]) ? LandUse.Waste : LandUse.Pasture;
        }
    }

    /// <summary>Flood-prone land, or low ground close to the water.</summary>
    public static bool IsMeadow(CategoryField buildable, ScalarField shore, ScalarField above, int k) =>
        buildable.Block(k) == SiteBlock.Flood
        || shore.Values[k] >= 0 && shore.Values[k] <= MeadowShore && above.Values[k] < MeadowHeight;

    /// <summary>A south-facing slope, steep enough to drain and catch the sun, not too steep to work.</summary>
    public static bool IsVineSlope(ScalarField slope, ScalarField aspect, int k)
    {
        float a = aspect.Values[k];
        return !aspect.IsNoData(a) && a >= VineAspectFrom && a <= VineAspectTo
            && slope.Values[k] >= VineSlopeFrom && slope.Values[k] <= VineSlopeTo;
    }
}
