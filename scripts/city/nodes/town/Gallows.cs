using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class GallowsOptions
{
    [Param("刑场", 0, 1,
        Hint = "1 时在城外路边的高处设刑场；0 表示没有")]
    public int Count { get; set; } = 1;
    [Param("用地边长 m", 8, 24, 4,
        Hint = "刑场方形用地的边长")]
    public float Size { get; set; } = 12;
    [Param("离房屋 m", 0, 600, 10,
        Hint = "用地离任何建成格至少多远")]
    public float BuiltClear { get; set; } = 200;
    [Param("离路 m", 8, 200, 4,
        Hint = "用地离过境道路至多多远：要让路人远远看见")]
    public float RoadReach { get; set; } = 60;
}

/// <summary>
/// The gallows: out on the town's land, on the highest ground near a through route, where travellers saw it from
/// afar. Its plot lies 300–800 m from the wall (in sight of the town), clear of every built cell and of the leper
/// house, within reach of a route but off its paving; highest by height over the market plus landform position.
/// </summary>
public sealed class Gallows : CityNode<GallowsOptions>
{
    private const float MinWall = 300, MaxWall = 800, LeperClear = 200;

    public override string Group => NodeGroups.Town;
    public override string Title => "刑场";
    public override string Description =>
        "城外过境道路附近的高处，让路人远远看见：离城墙 300–800 m（从城里看得见），离所有房屋和麻风院有一段距离，不压路面；取最高的地方（比市场高多少加地形位置）。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Wall, Keys.Plaza, Keys.Routes, Keys.Growth, Keys.Buildable, Keys.Height, Keys.Relief];
    public override IReadOnlyList<IKey> Edits => [Keys.Landmarks];

    protected override void Run(NodeContext c, GallowsOptions o)
    {
        var landmarks = c.Get(Keys.Landmarks);
        if (o.Count == 0)
        {
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var wall = c.Get(Keys.Wall);
        var network = c.Get(Keys.Routes);
        var growth = c.Get(Keys.Growth);
        var buildable = c.Get(Keys.Buildable);
        var height = c.Get(Keys.Height);
        var relief = c.Get(Keys.Relief);
        var grid = growth.Grid;
        int n = grid.Span(o.Size);
        float plazaHeight = height.Sample(c.Get(Keys.Plaza).Center);

        var toWall = Proximity.ToRing(grid, wall.Ring, MaxWall + grid.CellSize);
        var toBuilt = Proximity.From(grid, growth.BuiltCells, o.BuiltClear);
        var toRoute = Proximity.From(grid, network.Cells, Math.Max(o.RoadReach, LeperHouse.RouteClearance) + grid.CellSize);
        var taken = landmarks.Cells();
        var inside = wall.Inside(grid);
        var lepers = landmarks.Landmarks.Where(l => l.Kind == LandmarkKind.LeperHouse).Select(l => l.Center).ToList();
        bool Allowed(int k) => toWall[k] >= MinWall && toWall[k] <= MaxWall && buildable.Buildable(k) && !taken.Contains(k)
            && float.IsPositiveInfinity(toBuilt[k]) && !(toRoute[k] < LeperHouse.RouteClearance) && !inside[k]
            && lepers.All(l => l.DistanceTo(grid.Center(k)) >= LeperClear);
        float Score(CellRect rect)
        {
            var p = rect.Center(grid);
            return -(height.Sample(p) - plazaHeight + relief.Sample(p));
        }

        if (PlotSearch.Best(grid, n, n, Allowed, k => toRoute[k] <= o.RoadReach, Score) is not { } rect)
        {
            c.Notice("城外路边没有合适的刑场用地");
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var gallows = new Landmark("gallows", LandmarkKind.Gallows, grid, rect, Vector2.Right, growth.Latest);
        c.Set(Keys.Landmarks, landmarks.With(gallows));
    }
}
