using System;
using System.Collections.Generic;
using System.Linq;

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

    public static LandUse Use(this CategoryField land, int k) => (LandUse)land.Values[k];

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
