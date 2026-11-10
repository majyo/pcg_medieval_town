using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class SquaresOptions
{
    [Param("门内广场深 m", 0, 50, 1,
        Hint = "城门往里多远的一段主街放宽成梯形的门内广场；0 表示不设")]
    public float GateDepth { get; set; } = 25;
    [Param("门内广场宽 m", 10, 40, 1,
        Hint = "门内广场里端的宽度（门口与主街同宽）")]
    public float GateWidth { get; set; } = 20;
    [Param("教堂前庭深 m", 0, 30, 1,
        Hint = "教堂西门外的前庭有多深（宽同教堂用地）；0 表示不设")]
    public float ParvisDepth { get; set; } = 12;
    [Param("城堡门前深 m", 0, 30, 1,
        Hint = "城堡朝向市场的一边门前空地有多深（宽 20 m）；0 表示不设")]
    public float ForecourtDepth { get; set; } = 15;
}

/// <summary>
/// Small paved squares: inside each gate the main street widens into a trapezoid; before a church's west door (the
/// altar faces east) lies a forecourt as wide as its plot; before the castle, on its side toward the market, an open
/// space. Each is cut to buildable cells inside the wall, clear of the wall and of the landmark plots and of the
/// squares before it; a forecourt with no road near gets a street from it to the nearest road.
/// </summary>
public sealed class Squares : CityNode<SquaresOptions>
{
    /// <summary>The narrowest end of a gate square: the main street's width.</summary>
    private const float GateMouth = 7;
    /// <summary>How far a square keeps from the wall line (half the wall and a little).</summary>
    private const float WallMargin = 2.5f;
    private const float ForecourtWidth = 20;
    /// <summary>Smaller pieces are dropped.</summary>
    private const float MinArea = 100;

    public override string Group => NodeGroups.Roads;
    public override string Title => "小广场";
    public override string Description =>
        "城门里的一段主街放宽成梯形的门内广场；教堂西门外留一块前庭，城堡朝市场的一边留一块门前空地。都只在城内的可建设格上，离路远的前庭修一段街巷接上。";
    public override IReadOnlyList<IKey> Reads => [Keys.RoadZones, Keys.Wall, Keys.Gates, Keys.Landmarks, Keys.Plaza];
    public override IReadOnlyList<IKey> Writes => [Keys.Squares];
    public override IReadOnlyList<IKey> Edits => [Keys.RoadLinks];

    protected override void Run(NodeContext c, SquaresOptions o)
    {
        var zones = c.Get(Keys.RoadZones);
        var wall = c.Get(Keys.Wall);
        var landmarks = c.Get(Keys.Landmarks).Landmarks;
        var plaza = c.Get(Keys.Plaza);
        var grid = zones.Grid;
        var links = c.Get(Keys.RoadLinks).Copy();
        var plots = landmarks.Aggregate(Region.Empty, (all, l) => all.Union(l.Plot));
        var inside = wall.Interior.Offset(-WallMargin);
        var squares = new List<Square>();
        int skipped = 0;

        // The shape cut to buildable cells inside the wall, off plots and earlier squares; the part holding the anchor.
        Region? Cut(Vector2[] shape, Vector2 anchor)
        {
            var area = Region.Polygon(shape).Intersect(inside).Subtract(plots);
            var cells = grid.CellsIn(area.Offset(grid.CellSize)).Where(k => zones.Buildable.Buildable(k));
            area = area.Intersect(grid.Squares(cells));
            foreach (var square in squares)
                area = area.Subtract(square.Area);
            var part = area.Parts().OrderBy(p => p.Contains(anchor) ? 0 : 1).ThenByDescending(p => p.Area).FirstOrDefault();
            if (part == null || part.Area < MinArea)
            {
                skipped++;
                return null;
            }

            return part;
        }

        if (o.GateDepth > 0)
        {
            foreach (var gate in c.Get(Keys.Gates).Gates)
            {
                var inward = -gate.Outward.Normalized();
                var across = inward.Orthogonal();
                var far = gate.Position + inward * o.GateDepth;
                var shape = new[]
                {
                    gate.Position - across * GateMouth / 2, gate.Position + across * GateMouth / 2,
                    far + across * o.GateWidth / 2, far - across * o.GateWidth / 2,
                };
                if (Cut(shape, gate.Position + inward * o.GateDepth / 2) is { } area)
                    squares.Add(new Square(SquareKind.Gate, gate.Id, area));
            }
        }

        foreach (var landmark in landmarks)
        {
            bool parvis = landmark.Kind == LandmarkKind.Church && o.ParvisDepth > 0;
            bool forecourt = landmark.Kind == LandmarkKind.Castle && o.ForecourtDepth > 0;
            if (!parvis && !forecourt)
                continue;

            // The side the square stands before: the church's west end, or the castle's side toward the market.
            var toward = parvis ? -landmark.Axis.Normalized()
                : new[] { Vector2.Right, Vector2.Left, Vector2.Down, Vector2.Up }
                    .MaxBy(n => n.Dot(plaza.Center - landmark.Center));
            var size = landmark.Plot.Bounds.Size;
            float half = (MathF.Abs(toward.X) * size.X + MathF.Abs(toward.Y) * size.Y) / 2;
            float width = parvis ? MathF.Abs(toward.Y) * size.X + MathF.Abs(toward.X) * size.Y : ForecourtWidth;
            float depth = parvis ? o.ParvisDepth : o.ForecourtDepth;
            var mid = landmark.Center + toward * half;
            var across = toward.Orthogonal();
            var shape = new[]
            {
                mid - across * width / 2, mid + across * width / 2,
                mid + across * width / 2 + toward * depth, mid - across * width / 2 + toward * depth,
            };
            if (Cut(shape, mid + toward * depth / 2) is not { } area)
                continue;
            squares.Add(new Square(parvis ? SquareKind.Parvis : SquareKind.Forecourt, landmark.Id, area));
            Connect(area, mid);
        }

        if (skipped > 0)
            c.Notice($"{skipped} 处小广场放不下（城内可建设地不够），已跳过");
        c.Set(Keys.RoadLinks, links);
        c.Set(Keys.Squares, new SquareSet(squares));

        // A square with no road within a cell gets a street from its cell farthest from the landmark to the nearest road.
        void Connect(Region area, Vector2 door)
        {
            var roads = links.Cells.ToHashSet();
            var cells = grid.CellsIn(area);
            // A road's center line within a meter of the square: its paving overlaps the square.
            if (grid.CellsIn(area.Offset(1)).Any(roads.Contains))
                return;
            var start = cells.Where(k => zones.Allowed(k, RoadZone.Inside))
                .OrderByDescending(k => grid.Center(k).DistanceTo(door)).ThenBy(k => k).Cast<int?>().FirstOrDefault();
            if (start is not int from
                || RoadSearch.FindNearest(zones, links, from, roads.Contains, RoadZone.Inside, RoadKind.Street, c.Cancellation)
                    is not { } path)
            {
                c.Notice("有一处小广场没有接上道路");
                return;
            }

            links.Mark(path, RoadKind.Street);
        }
    }
}
