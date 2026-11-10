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
    public override string Group => TownGrowth.Group;
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
        float cell = grid.CellSize;
        int nx = Math.Max(1, (int)MathF.Round(o.Length / cell)), nz = Math.Max(1, (int)MathF.Round(o.Width / cell));

        var roads = o.RoadSetback > 0
            ? Region.Lines(network.Routes.Select(r => ((IReadOnlyList<Vector2>)r.Points, 2 * o.RoadSetback)))
            : Region.Empty;
        var taken = landmarks.Cells(grid);
        var blocked = new float[grid.Count];
        Array.Fill(blocked, 1);
        foreach (int k in Growth.CellsIn(grid, wall.Interior))
        {
            var p = grid.Center(k);
            bool ok = !growth.Built(k) && buildable.Buildable(k) && slope.Values[k] <= o.MaxSlope && !taken.Contains(k)
                && Geometry.DistanceToRing(p, wall.Ring) >= o.WallSetback && !roads.Contains(p);
            blocked[k] = ok ? 0 : 1;
        }

        var sums = new SummedArea(grid, blocked);
        var gatePoints = gates.Select(g => g.Position).ToList();
        (int X0, int Z0)? best = null;
        float bestScore = float.PositiveInfinity;
        var box = wall.Interior.Bounds;
        int x0Min = Math.Max(0, (int)MathF.Floor((box.Position.X - grid.Origin.X) / cell));
        int z0Min = Math.Max(0, (int)MathF.Floor((box.Position.Y - grid.Origin.Y) / cell));
        int x0Max = Math.Min(grid.Nx - nx, (int)MathF.Ceiling((box.End.X - grid.Origin.X) / cell));
        int z0Max = Math.Min(grid.Nz - nz, (int)MathF.Ceiling((box.End.Y - grid.Origin.Y) / cell));
        for (int z0 = z0Min; z0 <= z0Max; z0++)
        {
            for (int x0 = x0Min; x0 <= x0Max; x0++)
            {
                if (sums.Total(x0, z0, x0 + nx, z0 + nz) > 0)
                    continue;
                var center = grid.Origin + new Vector2(x0 + nx / 2f, z0 + nz / 2f) * cell;
                float toGate = gatePoints.Count == 0 ? 0 : gatePoints.Min(g => g.DistanceTo(center));
                float score = toGate + 0.5f * Geometry.DistanceToRing(center, wall.Ring);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = (x0, z0);
                }
            }
        }

        if (best is not { } found)
        {
            c.Notice("城墙里侧没有放得下修道院的空地");
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var corner = grid.Origin + new Vector2(found.X0, found.Z0) * cell;
        var size = new Vector2(nx, nz) * cell;
        var plot = Region.Polygon(new[] { corner, corner + new Vector2(size.X, 0), corner + size, corner + new Vector2(0, size.Y) });
        var friary = new Landmark("friary", LandmarkKind.Friary, plot, corner + size / 2, Vector2.Right, found.X0, found.Z0, nx, nz,
            growth.Latest + 1);
        c.Set(Keys.Landmarks, landmarks.With(friary));
    }
}
