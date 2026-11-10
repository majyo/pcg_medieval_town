using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class ChurchSiteOptions
{
    [Param("用地长 m", 20, 80, 4,
        Hint = "教堂和墓园用地东西向的长度（教堂沿这个方向，祭坛朝东）")]
    public float Length { get; set; } = 44;
    [Param("用地宽 m", 12, 60, 4,
        Hint = "教堂和墓园用地南北向的宽度")]
    public float Width { get; set; } = 28;
    [Param("搜索半径 m", 40, 400, 10,
        Hint = "在离市场中心多远以内找用地")]
    public float Radius { get; set; } = 160;
    [Param("离路 m", 0, 40, 1,
        Hint = "用地离过境道路中线至少多远：教堂离主路一小段，不压路")]
    public float RoadSetback { get; set; } = 10;
    [Param("离市场 m", 0, 40, 1,
        Hint = "用地离市场边缘至少多远")]
    public float PlazaGap { get; set; } = 6;
}

/// <summary>
/// The parish church: an east–west plot of whole cells near the market, on the highest buildable ground that
/// touches the village, set back from the through routes and the market. Its building faces east (altar end).
/// </summary>
public sealed class ChurchSite : CityNode<ChurchSiteOptions>
{
    public override string Group => NodeGroups.Town;
    public override string Title => "教堂";
    public override string Description =>
        "在市场附近、紧挨村落的地方找地势最高的一块东西向矩形用地（祭坛朝东），离主路一小段，不压道路和市场。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Plaza, Keys.Growth, Keys.Routes, Keys.Height, Keys.Relief, Keys.Buildable];
    public override IReadOnlyList<IKey> Writes => [Keys.Landmarks];

    protected override void Run(NodeContext c, ChurchSiteOptions o)
    {
        var plaza = c.Get(Keys.Plaza);
        var growth = c.Get(Keys.Growth);
        var network = c.Get(Keys.Routes);
        var height = c.Get(Keys.Height);
        var relief = c.Get(Keys.Relief);
        var buildable = c.Get(Keys.Buildable);
        var grid = buildable.Grid;

        // Cells the plot may not cover: unbuildable, near a route's center line, or near the market.
        var window = new Rect2(plaza.Center - Vector2.One * (o.Radius + o.Length), Vector2.One * 2 * (o.Radius + o.Length));
        var roads = network.Band(o.RoadSetback);
        var market = plaza.Area.Offset(o.PlazaGap);
        bool Allowed(int k) => buildable.Buildable(k)
            && !(window.HasPoint(grid.Center(k)) && (roads.Contains(grid.Center(k)) || market.Contains(grid.Center(k))));

        // Within the radius, touching the village (a built cell in the plot or right around it); highest first.
        float plazaHeight = height.Sample(plaza.Center);
        float Score(CellRect rect)
        {
            var center = rect.Center(grid);
            float distance = center.DistanceTo(plaza.Center);
            return distance > o.Radius ? float.NaN
                : -(height.Sample(center) - plazaHeight + relief.Sample(center) - 2 * distance / o.Radius);
        }

        var found = PlotSearch.Best(grid, grid.Span(o.Length), grid.Span(o.Width), Allowed, k => grid.AnyNear(k, 1, growth.Built),
            Score);
        if (found == null)
            c.Notice("市场附近没有放得下教堂用地的地方");
        c.Set(Keys.Landmarks, new LandmarkSet(found is { } rect
            ? new[] { new Landmark("church", LandmarkKind.Church, grid, rect, Vector2.Right, growth.Latest + 1) }
            : Array.Empty<Landmark>()));
    }
}
