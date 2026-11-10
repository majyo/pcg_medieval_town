using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class TownQuartersOptions
{
    [Param("核心半径 m", 0, 200, 5,
        Hint = "离市场边缘或教堂用地这么近的城内建成格算市场核心，其余是居住")]
    public float CoreRadius { get; set; } = 60;
    [Param("墙内空地 m", 0, 30, 1,
        Hint = "城墙内侧这么宽、没建房的地算城墙空地（墙根路），与街区的城墙内侧退让一致")]
    public float InsideStrip { get; set; } = 8;
    [Param("墙外空地 m", 0, 60, 1,
        Hint = "城墙外侧这么宽、没建房的地算城墙空地（防守用的空地），与街区的城墙外侧退让一致")]
    public float OutsideStrip { get; set; } = 15;
}

/// <summary>
/// Starts the land use with water and the town: inside the wall the public plots, the market core and housing on
/// built cells and gardens on the rest; outside the suburbs on built cells; the strip along the wall on both sides;
/// the through routes outside. Every other cell is left unassigned for the workshops and the countryside.
/// </summary>
public sealed class TownQuarters : CityNode<TownQuartersOptions>
{
    public override string Group => LandZoning.Group;
    public override string Title => "城镇分区";
    public override string Description =>
        "写出水域和城镇的用途：城内的公共用地、市场核心、居住、园地，墙外的城外街区，城墙两侧的空地和城外道路；其余的格留给作坊和农业带。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Shore, Keys.Growth, Keys.Wall, Keys.Plaza, Keys.Landmarks, Keys.Routes];
    public override IReadOnlyList<IKey> Writes => [Keys.LandUse];

    protected override void Run(NodeContext c, TownQuartersOptions o)
    {
        var shore = c.Get(Keys.Shore);
        var growth = c.Get(Keys.Growth);
        var wall = c.Get(Keys.Wall);
        var plaza = c.Get(Keys.Plaza);
        var landmarks = c.Get(Keys.Landmarks);
        var grid = growth.Grid;

        var inside = new bool[grid.Count];
        foreach (int k in Growth.CellsIn(grid, wall.Interior))
            inside[k] = true;
        var publicCells = Growth.CellsIn(grid, plaza.Area).Concat(landmarks.Landmarks.SelectMany(l => l.Cells(grid)))
            .ToHashSet();
        // The core gathers round the market and its church (and town hall), not round a castle or a friary.
        var civic = landmarks.Landmarks.Where(l => l.Kind is LandmarkKind.Church or LandmarkKind.TownHall)
            .Aggregate(plaza.Area, (all, l) => all.Union(l.Plot));
        var core = new HashSet<int>(Growth.CellsIn(grid, civic.Offset(o.CoreRadius)));
        var toWall = Proximity.ToRing(grid, wall.Ring, Math.Max(o.InsideStrip, o.OutsideStrip) + grid.CellSize);
        var routes = c.Get(Keys.Routes).Cells;

        var use = new byte[grid.Count];
        for (int k = 0; k < grid.Count; k++)
        {
            use[k] = (byte)Use(k);
        }

        c.Set(Keys.LandUse, LandUses.Field(grid, use));

        LandUse Use(int k)
        {
            if (shore.Values[k] < 0)
                return LandUse.Water;
            if (growth.Built(k))
            {
                if (!inside[k])
                    return LandUse.Suburb;
                return publicCells.Contains(k) ? LandUse.Public : core.Contains(k) ? LandUse.Core : LandUse.Housing;
            }

            if (toWall[k] < (inside[k] ? o.InsideStrip : o.OutsideStrip))
                return LandUse.WallStrip;
            if (inside[k])
                return LandUse.TownGarden;
            return routes.Contains(k) ? LandUse.Road : LandUse.Unassigned;
        }
    }
}
