using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class FriaryOptions
{
    [Param("修道院", 0, 1,
        Hint = "1 时在城墙里侧的空地上建一座托钵修会修道院；0 表示没有")]
    public int Count { get; set; } = 1;
    [Param("用地长 m", 24, 96, 4,
        Hint = "修道院用地东西向的长度（教堂沿这个方向）")]
    public float Length { get; set; } = 56;
    [Param("用地宽 m", 20, 72, 4,
        Hint = "修道院用地南北向的宽度（教堂一侧是回廊方院）")]
    public float Width { get; set; } = 40;
    [Param("离墙 m", 0, 30, 1,
        Hint = "用地离城墙至少多远，与三期的离墙距离一致")]
    public float WallSetback { get; set; } = 8;
    [Param("离路 m", 0, 40, 1,
        Hint = "用地离过境道路中线至少多远")]
    public float RoadSetback { get; set; } = 10;
    [Param("最大坡度 °", 1, 15, 0.5,
        Hint = "用地里每一格的坡度都不超过它")]
    public float MaxSlope { get; set; } = 6;
}

/// <summary>
/// The friary: the mendicant orders came late and found room only inside the wall, on land not yet built. An
/// east–west plot of whole cells inside the wall, clear of it, of the routes and of the other landmarks, on gentle
/// unbuilt ground; nearest a gate (the friars kept to the poor by the gates), then nearest the wall.
/// </summary>
public sealed class Friary : CityNode<FriaryOptions>
{
    public override string Group => NodeGroups.Town;
    public override string Title => "修道院";
    public override string Description =>
        "托钵修会来得晚，只能在城墙里侧还没建房的空地上落脚：一块东西向的矩形用地，离墙、离过境道路和别的地标都有一段距离，坡度平缓；" +
        "尽量靠近城门，其次靠近城墙。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Wall, Keys.Gates, Keys.Routes, Keys.Growth, Keys.Slope, Keys.Buildable];
    public override IReadOnlyList<IKey> Edits => [Keys.Landmarks];

    protected override void Run(NodeContext c, FriaryOptions o)
    {
        var landmarks = c.Get(Keys.Landmarks);
        if (o.Count == 0)
        {
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var wall = c.Get(Keys.Wall);
        var gates = c.Get(Keys.Gates).Gates;
        var network = c.Get(Keys.Routes);
        var growth = c.Get(Keys.Growth);
        var slope = c.Get(Keys.Slope);
        var buildable = c.Get(Keys.Buildable);
        var grid = growth.Grid;

        var roads = network.Band(o.RoadSetback);
        var taken = landmarks.Cells();
        var open = grid.Mask(grid.CellsIn(wall.Interior).Where(k => !growth.Built(k) && buildable.Buildable(k)
            && slope.Values[k] <= o.MaxSlope && !taken.Contains(k)
            && Geometry.DistanceToRing(grid.Center(k), wall.Ring) >= o.WallSetback && !roads.Contains(grid.Center(k))));
        var gatePoints = gates.Select(g => g.Position).ToList();
        float Score(CellRect rect)
        {
            var center = rect.Center(grid);
            float toGate = gatePoints.Count == 0 ? 0 : gatePoints.Min(g => g.DistanceTo(center));
            return toGate + 0.5f * Geometry.DistanceToRing(center, wall.Ring);
        }

        var found = PlotSearch.Best(grid, grid.Span(o.Length), grid.Span(o.Width), k => open[k], _ => true, Score);
        if (found is not { } rect)
        {
            c.Notice("城墙里侧没有放得下修道院的空地");
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        c.Set(Keys.Landmarks, landmarks.With(new Landmark("friary", LandmarkKind.Friary, grid, rect, Vector2.Right, growth.Latest + 1)));
    }
}
