using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class TownHallOptions
{
    /// <summary>The least share of the plot standing in the market; the rest takes the houses beside it.</summary>
    public const float MinInMarket = 0.25f;

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
    public override string Group => TownGrowth.Group;
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
        var taken = landmarks.Cells(grid);
        float cell = grid.CellSize;
        var bounds = plaza.Area.Bounds;
        bool alongX = bounds.Size.X >= bounds.Size.Y;
        int length = Math.Max(1, (int)MathF.Round(o.Length / cell)), width = Math.Max(1, (int)MathF.Round(o.Width / cell));
        int nx = alongX ? length : width, nz = alongX ? width : length;
        var routeLines = network.Routes.Select(r => r.Points).ToList();
        float Clearance(Vector2 p) => routeLines.Min(points => Distance(p, points));

        int x0Min = (int)MathF.Floor((bounds.Position.X - grid.Origin.X) / cell) - nx;
        int z0Min = (int)MathF.Floor((bounds.Position.Y - grid.Origin.Y) / cell) - nz;
        int x0Max = (int)MathF.Ceiling((bounds.End.X - grid.Origin.X) / cell);
        int z0Max = (int)MathF.Ceiling((bounds.End.Y - grid.Origin.Y) / cell);
        (int X0, int Z0, Region Plot)? best = null;
        float bestShare = 0, bestClear = float.NegativeInfinity, bestCenter = float.PositiveInfinity;
        for (int z0 = Math.Max(0, z0Min); z0 <= Math.Min(grid.Nz - nz, z0Max); z0++)
        {
            for (int x0 = Math.Max(0, x0Min); x0 <= Math.Min(grid.Nx - nx, x0Max); x0++)
            {
                var corner = grid.Origin + new Vector2(x0, z0) * cell;
                var size = new Vector2(nx, nz) * cell;
                var ring = new[] { corner, corner + new Vector2(size.X, 0), corner + size, corner + new Vector2(0, size.Y) };
                var plot = Region.Polygon(ring);
                // Its share in the market, in tenths, so a little more market does not outweigh the rest.
                float share = MathF.Floor((float)(plot.Intersect(plaza.Area).Area / plot.Area) * 10) / 10;
                if (share < TownHallOptions.MinInMarket || share < bestShare)
                    continue;
                bool free = true;
                for (int z = z0; z < z0 + nz && free; z++)
                    for (int x = x0; x < x0 + nx && free; x++)
                        free = buildable.Buildable(grid.Index(x, z)) && !taken.Contains(grid.Index(x, z));
                if (!free)
                    continue;
                // The plot's nearest approach to a route's center line, from its corners and edge midpoints.
                var probes = ring.Concat(ring.Select((p, i) => (p + ring[(i + 1) % 4]) / 2)).ToList();
                if (probes.Any(p => network.Cells.Contains(grid.CellAt(p))))
                    continue;
                float clear = probes.Min(Clearance);
                if (clear < o.RoadSetback)
                    continue;
                float center = (corner + size / 2).DistanceTo(plaza.Center);
                bool better = share > bestShare
                    || clear > bestClear + 0.01f || MathF.Abs(clear - bestClear) <= 0.01f && center < bestCenter;
                if (better)
                {
                    bestShare = share;
                    bestClear = clear;
                    bestCenter = center;
                    best = (x0, z0, plot);
                }
            }
        }

        if (best is not { } found)
        {
            c.Notice("市场里放不下市政厅，没有建");
            c.Set(Keys.Landmarks, landmarks);
            return;
        }

        var middle = grid.Origin + new Vector2(found.X0 + nx / 2f, found.Z0 + nz / 2f) * cell;
        var hall = new Landmark("town_hall", LandmarkKind.TownHall, found.Plot, middle, alongX ? Vector2.Right : Vector2.Down,
            found.X0, found.Z0, nx, nz, growth.Latest + 1);
        c.Set(Keys.Landmarks, landmarks.With(hall));
    }

    private static float Distance(Vector2 p, Vector2[] points)
    {
        float best = float.PositiveInfinity;
        for (int i = 1; i < points.Length; i++)
            best = MathF.Min(best, Geometry.DistanceToSegment(p, points[i - 1], points[i]));
        return best;
    }
}
