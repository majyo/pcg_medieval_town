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
    public override string Group => TownGrowth.Group;
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
        float cell = grid.CellSize;
        int nx = Math.Max(1, (int)MathF.Round(o.Length / cell)), nz = Math.Max(1, (int)MathF.Round(o.Width / cell));

        // Cells the plot may not cover: unbuildable, near a route's center line, or near the market.
        var window = new Rect2(plaza.Center - Vector2.One * (o.Radius + o.Length), Vector2.One * 2 * (o.Radius + o.Length));
        var roads = o.RoadSetback > 0
            ? Region.Lines(network.Routes.Select(r => ((IReadOnlyList<Vector2>)r.Points, 2 * o.RoadSetback)))
            : Region.Empty;
        var market = plaza.Area.Offset(o.PlazaGap);
        var blocked = new float[grid.Count];
        var built = new float[grid.Count];
        for (int k = 0; k < grid.Count; k++)
        {
            var p = grid.Center(k);
            built[k] = growth.Built(k) ? 1 : 0;
            blocked[k] = !buildable.Buildable(k) || window.HasPoint(p) && (roads.Contains(p) || market.Contains(p)) ? 1 : 0;
        }

        var blockedSums = new SummedArea(grid, blocked);
        var builtSums = new SummedArea(grid, built);
        float plazaHeight = height.Sample(plaza.Center);
        Landmark? best = null;
        float bestScore = float.NegativeInfinity;
        int cx = grid.CellAt(plaza.Center) % grid.Nx, cz = grid.CellAt(plaza.Center) / grid.Nx;
        int reach = (int)MathF.Ceiling(o.Radius / cell);
        for (int z0 = Math.Max(0, cz - reach - nz); z0 <= Math.Min(grid.Nz - nz, cz + reach); z0++)
        {
            for (int x0 = Math.Max(0, cx - reach - nx); x0 <= Math.Min(grid.Nx - nx, cx + reach); x0++)
            {
                var center = grid.Origin + new Vector2(x0 + nx / 2f, z0 + nz / 2f) * cell;
                float distance = center.DistanceTo(plaza.Center);
                if (distance > o.Radius || blockedSums.Total(x0, z0, x0 + nx, z0 + nz) > 0)
                    continue;
                // It must touch the village: a built cell in the plot or right around it.
                int ax = Math.Max(0, x0 - 1), az = Math.Max(0, z0 - 1);
                int bx = Math.Min(grid.Nx, x0 + nx + 1), bz = Math.Min(grid.Nz, z0 + nz + 1);
                if (builtSums.Total(ax, az, bx, bz) <= 0)
                    continue;
                float score = height.Sample(center) - plazaHeight + relief.Sample(center) - 2 * distance / o.Radius;
                if (score <= bestScore)
                    continue;
                bestScore = score;
                var corner = grid.Origin + new Vector2(x0, z0) * cell;
                var size = new Vector2(nx, nz) * cell;
                var plot = Region.Polygon(new[]
                {
                    corner, corner + new Vector2(size.X, 0), corner + size, corner + new Vector2(0, size.Y),
                });
                best = new Landmark("church", LandmarkKind.Church, plot, center, Vector2.Right, x0, z0, nx, nz, growth.Latest + 1);
            }
        }

        if (best == null)
            c.Notice("市场附近没有放得下教堂用地的地方");
        c.Set(Keys.Landmarks, new LandmarkSet(best == null ? Array.Empty<Landmark>() : new[] { best }));
    }
}
