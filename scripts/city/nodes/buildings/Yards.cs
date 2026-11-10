using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class YardsOptions
{
    [Param("后屋需要的后院 m", 4, 30, 1,
        Hint = "房子后面到地块尾端至少这么深，才在尾端放一间后屋")]
    public float MinBackYard { get; set; } = 10;
    [Param("后屋最浅 m", 3, 8, 0.5,
        Hint = "后屋进深的下限")]
    public float DepthMin { get; set; } = 4;
    [Param("后屋最深 m", 3, 10, 0.5,
        Hint = "后屋进深的上限")]
    public float DepthMax { get; set; } = 6;
    [Param("种子", 0, 99999,
        Hint = "只影响后屋进深的随机选择")]
    public int Seed { get; set; } = 1;
}

/// <summary>
/// What a plot holds besides its house. Its boundary off the street becomes a wall (inside the town wall) or a hedge
/// (outside), once per shared side, left out where a house wall stands on it. A deep back yard gets a back building at
/// the rear: a store by the market, a workshop in the housing, a barn in the suburbs, a stable in an inn's court, a
/// shed in the trades. What is left behind and beside the buildings of a house plot is its kitchen garden.
/// </summary>
public sealed class Yards : CityNode<YardsOptions>
{
    /// <summary>Boundary this near a road's paving is the street front, not a fence.</summary>
    private const float RoadReach = 1.2f;
    /// <summary>A house wall this near the boundary stands in for the fence.</summary>
    private const float HouseReach = 0.3f;
    private const float MaxWidth = 8, MinWidth = 3, SideGap = 0.5f, Gap = 1, MinGarden = 30, GardenWidth = 3;
    private const float Bucket = 16;

    public override string Group => NodeGroups.Buildings;
    public override string Title => "院落";
    public override string Description =>
        "地块不临街的边是院墙（城内）或篱笆（城外），相邻地块共用的边只算一次；房子后面够深的在地块尾端放一间后屋（仓房、作坊间、畜棚、马厩或料棚）；房子和后屋以外的地是菜园。";
    public override IReadOnlyList<IKey> Reads => [Keys.Parcels, Keys.Buildings, Keys.Roads, Keys.Wall];
    public override IReadOnlyList<IKey> Writes => [Keys.Yards];

    protected override void Run(NodeContext c, YardsOptions o)
    {
        if (o.DepthMax < o.DepthMin)
            throw new ArgumentException("后屋最深不能小于最浅");
        var parcels = c.Get(Keys.Parcels).Parcels;
        var houses = c.Get(Keys.Buildings).Buildings.ToDictionary(b => b.ParcelId);
        var edges = c.Get(Keys.Roads).Edges;
        var interior = c.Get(Keys.Wall).Interior;
        var regions = parcels.Select(p => Region.Polygon(p.Ring)).ToList();
        var edgeBounds = edges.Select(e => e.Points.Aggregate(new Rect2(e.Points[0], Vector2.Zero), (r, v) => r.Expand(v))
            .Grow(e.Width / 2 + RoadReach)).ToList();
        var houseAreas = houses.ToDictionary(h => h.Key, h => Region.Polygon(h.Value.Corners).Offset(HouseReach));
        var near = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < parcels.Count; i++)
        {
            foreach (var key in Buckets(regions[i].Bounds.Grow(1)))
            {
                if (!near.TryGetValue(key, out var list))
                    near[key] = list = new List<int>();
                list.Add(i);
            }
        }

        IEnumerable<int> Around(Vector2 p) => near.TryGetValue(Key(p), out var list) ? list : Enumerable.Empty<int>();
        bool OnRoad(Vector2 p, List<RoadEdge> local) => local.Any(e => Geometry.DistanceToLine(p, e.Points) <= e.Width / 2 + RoadReach);

        var fences = new List<Fence>();
        var outbuildings = new List<Outbuilding>();
        var gardens = new List<Garden>();
        var rng = new Random(o.Seed);
        for (int i = 0; i < parcels.Count; i++)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            var plot = parcels[i];
            var box = regions[i].Bounds.Grow(RoadReach + 8);
            var local = edges.Where((e, k) => edgeBounds[k].Intersects(box)).ToList();

            // Fences: each side off the street, once (an earlier plot sharing it has it), not where a house wall is.
            bool ByHouse(Vector2 m) => Around(m).Any(j => houseAreas.TryGetValue(j, out var h) && h.Contains(m));
            bool Keep(Vector2 p, Vector2 q)
            {
                var m = (p + q) / 2;
                return !OnRoad(m, local) && !ByHouse(p) && !ByHouse(m) && !ByHouse(q)
                    && !Around(m).Any(j => j < i && Geometry.DistanceToRing(m, parcels[j].Ring) < HouseReach);
            }
            var run = new List<Vector2>();
            void Flush()
            {
                if (run.Count >= 2)
                    fences.Add(new Fence(run.ToArray(), interior.Contains(run[run.Count / 2]) ? FenceKind.Wall : FenceKind.Hedge));
                run = new List<Vector2>();
            }

            for (int s = 0; s < plot.Ring.Length; s++)
            {
                var a = plot.Ring[s];
                var b = plot.Ring[(s + 1) % plot.Ring.Length];
                int steps = Math.Max(1, (int)MathF.Ceiling(a.DistanceTo(b)));
                for (int t = 0; t < steps; t++)
                {
                    var p = a.Lerp(b, t / (float)steps);
                    var q = a.Lerp(b, (t + 1) / (float)steps);
                    if (Keep(p, q))
                    {
                        if (run.Count == 0)
                            run.Add(p);
                        run.Add(q);
                    }
                    else
                        Flush();
                }
            }

            Flush();

            if (!houses.TryGetValue(plot.Id, out var house))
                continue;

            // The back building at the rear of a deep yard.
            float depth = plot.Depth;
            float houseBack = house.Corners.Max(v => (v - plot.FrontA).Dot(plot.Inward));
            float wanted = o.DepthMin + rng.NextSingle() * (o.DepthMax - o.DepthMin);
            Vector2[]? shed = null;
            if (depth - houseBack >= o.MinBackYard && plot.Frontage >= MinWidth + 2)
                shed = Place(plot, regions[i], houseBack, MathF.Min(plot.Frontage - 1, MaxWidth), wanted, o.DepthMin);
            if (shed != null)
            {
                var back = (shed[2] + shed[3]) / 2;
                bool facesBack = local.Any(e => Geometry.DistanceToLine(back, e.Points) <= e.Width / 2 + 1.5f);
                outbuildings.Add(new Outbuilding(plot.Id, shed, KindOf(plot), facesBack));
            }

            // The garden: what is left, if big and wide enough. Inns' courts and the trades' yards are worked ground.
            if (plot.Kind is ParcelKind.Court or ParcelKind.Trade)
                continue;
            var left = regions[i].Subtract(Region.Polygon(house.Corners).Offset(Gap));
            if (shed != null)
                left = left.Subtract(Region.Polygon(shed).Offset(Gap));
            foreach (var part in left.Parts().Where(p => p.Area >= MinGarden && p.Offset(-GardenWidth / 2).Area > 0))
                gardens.Add(new Garden(plot.Id, part));
        }

        c.Set(Keys.Yards, new YardSet(fences, outbuildings, gardens));
    }

    /// <summary>
    /// A back building against the rear of the plot: as far back as it fits, centered on the front or slid along it, at
    /// least a meter behind the house; narrower, then shallower, when it does not fit.
    /// </summary>
    private static Vector2[]? Place(Parcel plot, Region region, float houseBack, float width, float depth, float minDepth)
    {
        var along = (plot.FrontB - plot.FrontA).Normalized();
        var middle = (plot.FrontA + plot.FrontB) / 2;
        float plotDepth = plot.Depth;
        var widths = new List<float>();
        for (float w = width; w > MinWidth; w *= 0.75f)
            widths.Add(w);
        widths.Add(MinWidth);
        foreach (float w in widths)
        {
            foreach (float d in new[] { depth, minDepth }.Distinct())
            {
                for (float back = plotDepth - SideGap; back - d >= houseBack + Gap; back -= 1)
                {
                    float slack = MathF.Max(0, (plot.Frontage - w) / 2 - SideGap);
                    for (float s = 0; s <= slack; s = s == 0 ? 1.5f : s + 1.5f)
                    {
                        foreach (float sign in s == 0 ? new[] { 1f } : new[] { 1f, -1f })
                        {
                            var center = middle + along * (sign * s);
                            var fl = center - along * (w / 2) + plot.Inward * (back - d);
                            var corners = new[] { fl, fl + along * w, fl + along * w + plot.Inward * d, fl + plot.Inward * d };
                            if (corners.All(region.Contains) && Region.Polygon(corners).Subtract(region).Area < 0.2)
                                return corners;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static OutbuildingKind KindOf(Parcel plot) => plot.Kind switch
    {
        ParcelKind.Court => OutbuildingKind.Stable,
        ParcelKind.Trade => OutbuildingKind.Shed,
        _ => plot.Use switch
        {
            LandUse.Core => OutbuildingKind.Store,
            LandUse.Suburb => OutbuildingKind.Barn,
            _ => OutbuildingKind.Workshop,
        },
    };

    private static (int, int) Key(Vector2 p) => ((int)MathF.Floor(p.X / Bucket), (int)MathF.Floor(p.Y / Bucket));

    private static IEnumerable<(int, int)> Buckets(Rect2 r)
    {
        var (x0, z0) = Key(r.Position);
        var (x1, z1) = Key(r.End);
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
                yield return (x, z);
        }
    }
}
