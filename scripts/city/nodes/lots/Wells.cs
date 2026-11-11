using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// Public wells: one in the market, then, again and again, the place that brings water within reach of the most
/// built cells still without it, until every built cell has water near (a well, or the river) or no place helps.
/// Places are the market, the gate squares, every junction, and points every so often along the streets and roads;
/// all stand in the paving, apart from each other.
/// </summary>
public sealed class Wells : CityNode<NoOptions>
{
    /// <summary>How far a household carries water: a built cell this near a well or the water has it.</summary>
    public const float Reach = 80;
    /// <summary>The nearest two wells may be.</summary>
    public const float MinSpacing = 40;
    /// <summary>
    /// Points along the streets and roads, so a long arm far from any junction can have a well. Much closer than
    /// <see cref="MinSpacing"/>: with points only that far apart, the last cells without water can have every point
    /// in their reach shut out by a well already dug next to it.
    /// </summary>
    private const float Step = 10;

    public override string Group => NodeGroups.Lots;
    public override string Title => "水井";
    public override string Description =>
        "先在市场放一口井，然后反复挑能让最多还没水的建成格在 80 m 内有井的位置（门内广场、路口、沿街每 10 m 一点），直到每个建成格离井或河都不超过 80 m；井两两相距至少 40 m。";
    public override IReadOnlyList<IKey> Reads => [Keys.Roads, Keys.Plaza, Keys.Squares, Keys.Growth, Keys.Shore];
    public override IReadOnlyList<IKey> Writes => [Keys.Fixtures];

    protected override void Run(NodeContext c, NoOptions o)
    {
        var roads = c.Get(Keys.Roads);
        var plaza = c.Get(Keys.Plaza);
        var growth = c.Get(Keys.Growth);
        var shore = c.Get(Keys.Shore);
        var grid = growth.Grid;
        var paving = roads.Footprint;

        var degree = new int[roads.Nodes.Count];
        foreach (var e in roads.Edges)
        {
            degree[e.From]++;
            degree[e.To]++;
        }

        var places = new List<Vector2> { plaza.Center };
        places.AddRange(c.Get(Keys.Squares).Squares.Where(s => s.Kind == SquareKind.Gate).Select(s => s.Area.Bounds.GetCenter()));
        places.AddRange(roads.Nodes.Where(n => degree[n.Id] >= 3).Select(n => n.Position));
        places.AddRange(roads.Edges.Where(e => e.Kind != RoadKind.Track).SelectMany(e => Geometry.Samples(e.Points, Step)));
        places = places.Where(paving.Contains).ToList();

        var dry = growth.BuiltCells.Where(k => shore.Values[k] > Reach).ToList();
        var isDry = grid.Mask(dry);
        int window = (int)MathF.Ceiling(Reach / grid.CellSize);
        var reaches = places.Select(p =>
        {
            var cells = new List<int>();
            int center = grid.ClampedCellAt(p), cx = center % grid.Nx, cz = center / grid.Nx;
            for (int z = Math.Max(0, cz - window); z <= Math.Min(grid.Nz - 1, cz + window); z++)
            {
                for (int x = Math.Max(0, cx - window); x <= Math.Min(grid.Nx - 1, cx + window); x++)
                {
                    int k = z * grid.Nx + x;
                    if (isDry[k] && grid.Center(k).DistanceTo(p) <= Reach)
                        cells.Add(k);
                }
            }

            return cells;
        }).ToList();
        var watered = new HashSet<int>();
        var wells = new List<Vector2>();
        void Dig(int i)
        {
            wells.Add(places[i]);
            watered.UnionWith(reaches[i]);
        }

        if (places.Count > 0 && places[0] == plaza.Center)
            Dig(0);
        while (watered.Count < dry.Count)
        {
            int best = -1, gain = 0;
            for (int i = 0; i < places.Count; i++)
            {
                if (wells.Any(w => w.DistanceTo(places[i]) < MinSpacing))
                    continue;
                int g = reaches[i].Count(k => !watered.Contains(k));
                if (g > gain)
                    (best, gain) = (i, g);
            }

            if (best < 0)
                break;
            Dig(best);
        }

        if (watered.Count < dry.Count)
            c.Notice($"{dry.Count - watered.Count} 个建成格 {Reach:0} m 内既没有井也没有水");
        c.Set(Keys.Fixtures, new FixtureSet(wells.Select(w => new Fixture(FixtureKind.Well, w)).ToList()));
    }
}
