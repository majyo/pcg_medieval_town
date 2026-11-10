using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>What a cell of the map is used for (the values of <c>land.use</c>), in the order the nodes assign them.</summary>
public enum LandUse : byte
{
    Unassigned,
    Water,
    Public,
    Core,
    Housing,
    Suburb,
    WallStrip,
    TownGarden,
    Road,
    WetWorks,
    FireWorks,
    Mill,
    Woodland,
    Meadow,
    Garden,
    Vineyard,
    Arable,
    Pasture,
    Waste,
}

public static class LandUses
{
    public static readonly IReadOnlyList<string> Names =
    [
        "未分配", "水域", "公共用地", "市场核心", "居住", "城外街区", "城墙空地", "城内园地", "城外道路",
        "湿作坊", "火作坊", "水磨", "林地", "草场", "菜园果园", "葡萄园", "耕地", "牧场", "荒地",
    ];

    /// <summary>Uses only found inside the wall.</summary>
    public static readonly IReadOnlySet<LandUse> Inside = new HashSet<LandUse>
    {
        LandUse.Public, LandUse.Core, LandUse.Housing, LandUse.TownGarden,
    };

    /// <summary>Uses of the built cells: exactly these, and only on built cells.</summary>
    public static readonly IReadOnlySet<LandUse> Built = new HashSet<LandUse>
    {
        LandUse.Public, LandUse.Core, LandUse.Housing, LandUse.Suburb,
    };

    /// <summary>Uses cut into street plots.</summary>
    public static readonly IReadOnlySet<LandUse> Plots = new HashSet<LandUse>
    {
        LandUse.Core, LandUse.Housing, LandUse.Suburb,
    };

    public static LandUse Use(this CategoryField land, int k) => (LandUse)land.Values[k];

    /// <summary>
    /// The use of a plot whose front is at <paramref name="p"/>: the use of that cell if it is a plot use on the plot's
    /// side of the wall; else the most common such use within two cells (blocks are rounded off the built cells, so an
    /// edge can lie on a garden); else housing inside the wall and suburb outside.
    /// </summary>
    public static LandUse PlotUse(this CategoryField land, Vector2 p, bool inside)
    {
        bool Fits(LandUse use) => Plots.Contains(use) && Inside.Contains(use) == inside;
        var grid = land.Grid;
        int k = grid.CellAt(p);
        if (k >= 0 && Fits(land.Use(k)))
            return land.Use(k);

        var counts = new int[Names.Count];
        if (k >= 0)
        {
            int x = k % grid.Nx, z = k / grid.Nx;
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (grid.InGrid(x + dx, z + dz))
                        counts[land.Values[grid.Index(x + dx, z + dz)]]++;
                }
        }

        var best = Plots.Where(Fits).OrderByDescending(u => counts[(int)u]).ThenBy(u => u).First();
        return counts[(int)best] > 0 ? best : inside ? LandUse.Housing : LandUse.Suburb;
    }

    public static string Label(this LandUse use) => Names[(int)use];

    /// <summary>A copy of <paramref name="land"/>'s values for an editing node to change.</summary>
    public static byte[] Edit(this CategoryField land) => (byte[])land.Values.Clone();

    public static CategoryField Field(SiteGrid grid, byte[] values) => new(grid, values, "用地", Names);

    /// <summary>The cells holding <paramref name="use"/>.</summary>
    public static IEnumerable<int> CellsOf(this CategoryField land, LandUse use) =>
        Enumerable.Range(0, land.Values.Length).Where(k => land.Values[k] == (byte)use);

    static LandUses()
    {
        if (Names.Count != Enum.GetValues<LandUse>().Length)
            throw new InvalidOperationException("用地类别的名称与枚举数目不符");
    }
}
