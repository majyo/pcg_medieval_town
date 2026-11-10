using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class TownHallOptions
{
    [Param("市政厅", 0, 1,
        Hint = "1 时在市场里建一座市政厅（筑墙后取得特许状）；0 表示没有")]
    public int Count { get; set; } = 1;
    [Param("用地长 m", 8, 32, 4,
        Hint = "市政厅用地的长边，顺着市场较长的方向")]
    public float Length { get; set; } = 16;
    [Param("用地宽 m", 8, 24, 4,
        Hint = "市政厅用地的短边")]
    public float Width { get; set; } = 12;
    [Param("离路 m", 0, 12, 0.5,
        Hint = "用地离过境道路中线至少多远，不挡穿过市场的车马")]
    public float RoadSetback { get; set; } = 5;
}

/// <summary>
/// The town hall, built once the walled town has its charter: a rectangle of whole cells in the market, its long
/// side along the market's longer extent, clear of the through routes and the church. A spindle of a market is mostly
/// road, so the hall may stand at its edge on the houses beside it (at least a quarter in the market): as much in the
/// market as it can, then as far from the routes, then as near the market's middle.
/// </summary>
public sealed class TownHall : CityNode<TownHallOptions>
{
    /// <summary>The least share of the plot standing in the market; the rest takes the houses beside it.</summary>
    public const float MinInMarket = 0.25f;

    public override string Group => NodeGroups.Town;
    public override string Title => "市政厅";
    public override string Description =>
        "筑墙之后，在市场里放一座市政厅：长边顺着市场较长的方向，不压过境道路和教堂。纺锤形的市场大半是路，所以市政厅可以站在市场边上、占掉旁边的房子（至少四分之一在市场里）。" +
        "尽量多在市场里，其次离道路远，再次靠近市场中间。";
    public override IReadOnlyList<IKey> Reads => [Keys.Plaza, Keys.Routes, Keys.Growth, Keys.Buildable];
    public override IReadOnlyList<IKey> Edits => [Keys.Landmarks];

    protected override void Run(NodeContext c, TownHallOptions o)
    {
        var landmarks = c.Get(Keys.Landmarks);
        if (o.Count == 0)
        {
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var plaza = c.Get(Keys.Plaza);
        var network = c.Get(Keys.Routes);
        var growth = c.Get(Keys.Growth);
        var buildable = c.Get(Keys.Buildable);
        var grid = growth.Grid;
        var taken = landmarks.Cells();
        float cell = grid.CellSize;
        var bounds = plaza.Area.Bounds;
        bool alongX = bounds.Size.X >= bounds.Size.Y;
        int length = grid.Span(o.Length), width = grid.Span(o.Width);
        int nx = alongX ? length : width, nz = alongX ? width : length;
        float Clearance(Vector2 p) => network.Routes.Min(r => Geometry.DistanceToLine(p, r.Points));

        int x0Min = (int)MathF.Floor((bounds.Position.X - grid.Origin.X) / cell) - nx;
        int z0Min = (int)MathF.Floor((bounds.Position.Y - grid.Origin.Y) / cell) - nz;
        int x0Max = (int)MathF.Ceiling((bounds.End.X - grid.Origin.X) / cell);
        int z0Max = (int)MathF.Ceiling((bounds.End.Y - grid.Origin.Y) / cell);
        CellRect? best = null;
        float bestShare = 0, bestClear = float.NegativeInfinity, bestCenter = float.PositiveInfinity;
        for (int z0 = Math.Max(0, z0Min); z0 <= Math.Min(grid.Nz - nz, z0Max); z0++)
        {
            for (int x0 = Math.Max(0, x0Min); x0 <= Math.Min(grid.Nx - nx, x0Max); x0++)
            {
                var rect = new CellRect(x0, z0, nx, nz);
                var plot = rect.Area(grid);
                // Its share in the market, in tenths, so a little more market does not outweigh the rest.
                float share = MathF.Floor((float)(plot.Intersect(plaza.Area).Area / plot.Area) * 10) / 10;
                if (share < MinInMarket || share < bestShare)
                    continue;
                if (!rect.Cells(grid).All(k => buildable.Buildable(k) && !taken.Contains(k)))
                    continue;
                // The plot's nearest approach to a route's center line, from its corners and edge midpoints.
                var ring = plot.Rings[0];
                var probes = ring.Concat(ring.Select((p, i) => (p + ring[(i + 1) % 4]) / 2)).ToList();
                if (probes.Any(p => network.Cells.Contains(grid.CellAt(p))))
                    continue;
                float clear = probes.Min(Clearance);
                if (clear < o.RoadSetback)
                    continue;
                float center = rect.Center(grid).DistanceTo(plaza.Center);
                bool better = share > bestShare
                    || clear > bestClear + 0.01f || MathF.Abs(clear - bestClear) <= 0.01f && center < bestCenter;
                if (better)
                {
                    bestShare = share;
                    bestClear = clear;
                    bestCenter = center;
                    best = rect;
                }
            }
        }

        if (best is not { } found)
        {
            c.Notice("市场里放不下市政厅，没有建");
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var hall = new Landmark("town_hall", LandmarkKind.TownHall, grid, found, alongX ? Vector2.Right : Vector2.Down,
            growth.Latest + 1);
        c.Set(Keys.Landmarks, landmarks.With(hall));
    }
}
