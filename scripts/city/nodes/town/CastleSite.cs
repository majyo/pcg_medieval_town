using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class CastleSiteOptions
{
    [Param("城堡", 0, 1,
        Hint = "1 时在墙线上相对最高的地方建一座城堡，墙线绕过它；0 表示没有城堡")]
    public int Count { get; set; } = 1;
    [Param("用地边长 m", 24, 80, 4,
        Hint = "城堡方形用地的边长")]
    public float Size { get; set; } = 40;
    [Param("离路 m", 0, 80, 2,
        Hint = "用地离过境道路中线至少多远。城门开在道路穿墙的地方，所以不会落在城堡上")]
    public float RoadSetback { get; set; } = 30;
    [Param("离市场 m", 0, 300, 10,
        Hint = "用地离市场边缘至少多远：城堡在城的一角，不挤着市场")]
    public float PlazaGap { get; set; } = 60;
    [Param("离河 m", 0, 60, 1,
        Hint = "用地离水至少多远，与城墙的离河距离一致")]
    public float RiverSetback { get; set; } = 20;
}

/// <summary>
/// The castle: a square plot of whole cells across the wall line, at least half inside, on the highest and best
/// guarded ground the line passes (height over the market, landform position, and the slope just around the plot),
/// clear of the routes, the market, the river and the church. The plot joins the town, so the wall runs round the
/// castle's outer side, which stands in for that stretch of wall.
/// </summary>
public sealed class CastleSite : CityNode<CastleSiteOptions>
{
    public override string Group => NodeGroups.Town;
    public override string Title => "城堡";
    public override string Description =>
        "在墙线经过的地方找相对最高、四周较陡的一块方形用地，跨在墙线上、至少一半在城内，离过境道路、市场、河和教堂都有一段距离；" +
        "用地并进城内，墙线绕过城堡的外侧，城堡朝外的墙就是那一段城墙。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Plaza, Keys.Routes, Keys.Growth, Keys.Height, Keys.Relief, Keys.Slope, Keys.Shore, Keys.Buildable];
    public override IReadOnlyList<IKey> Edits => [Keys.Wall, Keys.Landmarks];

    protected override void Run(NodeContext c, CastleSiteOptions o)
    {
        var wall = c.Get(Keys.Wall);
        var landmarks = c.Get(Keys.Landmarks);
        if (o.Count == 0)
        {
            c.Set(Keys.Wall, wall);
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var plaza = c.Get(Keys.Plaza);
        var network = c.Get(Keys.Routes);
        var height = c.Get(Keys.Height);
        var relief = c.Get(Keys.Relief);
        var slope = c.Get(Keys.Slope);
        var shore = c.Get(Keys.Shore);
        var buildable = c.Get(Keys.Buildable);
        var grid = buildable.Grid;
        int n = grid.Span(o.Size, 2);

        // Cells the plot may not cover (only near the wall is any allowed), and the cells inside the wall.
        var window = wall.Interior.Bounds.Grow(o.Size + grid.CellSize);
        var roads = network.Band(o.RoadSetback);
        var market = plaza.Area.Offset(o.PlazaGap);
        var taken = landmarks.Cells();
        bool Allowed(int k)
        {
            var p = grid.Center(k);
            return window.HasPoint(p) && buildable.Buildable(k) && shore.Values[k] >= o.RiverSetback && !taken.Contains(k)
                && !roads.Contains(p) && !market.Contains(p);
        }

        var insideSums = new SummedArea(grid, Array.ConvertAll(wall.Inside(grid), i => i ? 1f : 0f));
        var slopeSums = new SummedArea(grid, slope.Values);
        float plazaHeight = height.Sample(plaza.Center);
        int area = n * n, around = (n + 2) * (n + 2) - area;

        // Across the line (at least half inside, not all), with a ring of cells round it for the edge slope.
        float Score(CellRect r)
        {
            if (r.X0 < 1 || r.Z0 < 1 || r.X0 + n + 1 > grid.Nx || r.Z0 + n + 1 > grid.Nz)
                return float.NaN;
            double within = insideSums.Total(r.X0, r.Z0, r.X0 + n, r.Z0 + n);
            if (within * 2 < area || within >= area)
                return float.NaN;
            var center = r.Center(grid);
            double edge = (slopeSums.Total(r.X0 - 1, r.Z0 - 1, r.X0 + n + 1, r.Z0 + n + 1)
                - slopeSums.Total(r.X0, r.Z0, r.X0 + n, r.Z0 + n)) / around;
            return -(height.Sample(center) - plazaHeight + relief.Sample(center) + 0.5f * (float)edge);
        }

        if (PlotSearch.Best(grid, n, n, Allowed, _ => true, Score) is not { } rect)
        {
            c.Notice("墙线上没有放得下城堡的地方");
            c.Set(Keys.Wall, wall);
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var outward = (rect.Center(grid) - plaza.Center).Normalized();
        var castle = new Landmark("castle", LandmarkKind.Castle, grid, rect, outward, c.Get(Keys.Growth).Latest + 1);

        // The plot joins the town: the wall now runs round the castle's outer side.
        var ring = wall.Interior.Union(castle.Plot).Rings.MaxBy(r => Region.Polygon(r).Area)!;
        c.Set(Keys.Wall, wall.With(ring));
        c.Set(Keys.Landmarks, landmarks.With(castle));
    }
}
