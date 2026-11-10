using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class LeperHouseOptions
{
    [Param("麻风院", 0, 1,
        Hint = "1 时在城外的过境道路边建一座麻风院；0 表示没有")]
    public int Count { get; set; } = 1;
    [Param("用地长 m", 16, 64, 4,
        Hint = "麻风院用地东西向的长度（礼拜堂和几间小屋）")]
    public float Length { get; set; } = 32;
    [Param("用地宽 m", 12, 48, 4,
        Hint = "麻风院用地南北向的宽度")]
    public float Width { get; set; } = 24;
    [Param("离墙 m", 100, 1500, 50,
        Hint = "离城墙最合适的距离：够远，病人与城隔开，又在去城里的路边，可以向路人乞讨")]
    public float Distance { get; set; } = 500;
    [Param("离房屋 m", 0, 400, 10,
        Hint = "用地离任何建成格至少多远")]
    public float BuiltClear { get; set; } = 150;
    [Param("离路 m", 8, 40, 1,
        Hint = "用地离过境道路至多多远：麻风院就在路边")]
    public float RoadReach { get; set; } = 12;
}

/// <summary>
/// The leper house: out in the country beside a through route, far enough from the town to keep the sick apart
/// and on the way in, where they could beg. Its plot lies within a short way of a route but off its paving, at least
/// so far from every built cell, 300–900 m from the wall; nearest the preferred distance, and better within reach of
/// water.
/// </summary>
public sealed class LeperHouse : CityNode<LeperHouseOptions>
{
    private const float MinWall = 300, MaxWall = 900, WaterReach = 150, WaterBonus = 100;

    /// <summary>Plot cells keep at least this far from a route's cells, so its paving stays off the plot.</summary>
    public const float RouteClearance = 8;

    public override string Group => NodeGroups.Town;
    public override string Title => "麻风院";
    public override string Description =>
        "城外过境道路边的一块用地：离城墙 300–900 m，离所有房屋有一段距离，紧挨道路但不压路面；离墙越接近合适的距离越好，附近有水的更好。";
    public override IReadOnlyList<IKey> Reads => [Keys.Wall, Keys.Routes, Keys.Growth, Keys.Buildable, Keys.Shore];
    public override IReadOnlyList<IKey> Edits => [Keys.Landmarks];

    protected override void Run(NodeContext c, LeperHouseOptions o)
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
        var shore = c.Get(Keys.Shore);
        var grid = growth.Grid;

        var toWall = Proximity.ToRing(grid, wall.Ring, MaxWall + grid.CellSize);
        var toBuilt = Proximity.From(grid, growth.BuiltCells, o.BuiltClear);
        var toRoute = Proximity.From(grid, network.Cells, Math.Max(o.RoadReach, RouteClearance) + grid.CellSize);
        var taken = landmarks.Cells();
        var inside = wall.Inside(grid);
        bool Allowed(int k) => toWall[k] >= MinWall && toWall[k] <= MaxWall && buildable.Buildable(k) && !taken.Contains(k)
            && float.IsPositiveInfinity(toBuilt[k]) && !(toRoute[k] < RouteClearance) && !inside[k];
        float Score(CellRect rect)
        {
            int k = grid.CellAt(rect.Center(grid));
            float s = MathF.Abs(toWall[k] - o.Distance);
            return shore.Values[k] <= WaterReach ? s - WaterBonus : s;
        }

        var found = PlotSearch.Best(grid, grid.Span(o.Length), grid.Span(o.Width), Allowed, k => toRoute[k] <= o.RoadReach, Score);
        if (found is not { } rect)
        {
            c.Notice("城外的过境道路边没有合适的麻风院用地");
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var house = new Landmark("leper_house", LandmarkKind.LeperHouse, grid, rect, Vector2.Right, growth.Latest);
        c.Set(Keys.Landmarks, landmarks.With(house));
    }
}
