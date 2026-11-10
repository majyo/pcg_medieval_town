using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class SuburbsOptions : SpreadOptions
{
    /// <summary>Deeper and cheaper off the road than the earlier phases, so the suburbs thicken at the gates.</summary>
    public SuburbsOptions()
    {
        Depth = 80;
        DepthCost = 1.5f;
    }

    [Param("面积比例", 0, 1, 0.05,
        Hint = "这一期新建的面积 = 城内建成面积 × 这个比例；城内剩下的可建设地先填，余下的往城门外长")]
    public float Share { get; set; } = 0.35f;
    [Param("城内离墙 m", 0, 30, 1,
        Hint = "城内离城墙这么近的地不建房，与三期的离墙距离一致")]
    public float InsideSetback { get; set; } = 8;
    [Param("城外离墙 m", 0, 60, 1,
        Hint = "城外离城墙这么近的地不建房（墙外留一条防守用的空地），与街区的城墙外侧退让一致")]
    public float OutsideSetback { get; set; } = 15;
    [Param("桥头面积 ha", 0, 3, 0.1,
        Hint = "离城最近的使用中渡口，在对岸桥头另长这么大一片；0 表示不长")]
    public float BridgeArea { get; set; } = 0.5f;
    [Param("桥头距离 m", 0, 1500, 10,
        Hint = "对岸桥头离城墙不超过这么远，才长桥头镇")]
    public float BridgeReach { get; set; } = 400;
}

/// <summary>
/// Phase four: the town outgrows its wall. Buildable land still free inside the wall fills first (only when phase
/// three left gardens); the rest of the area grows outward from the gates and the arms already outside, clear of the
/// wall. Deeper and cheaper off the road than the earlier phases, it thickens beside the gates rather than lengthening
/// the arms. Once the town spills outside, a small bridgehead suburb grows across the
/// nearest crossing in use.
/// </summary>
public sealed class Suburbs : CityNode<SuburbsOptions>
{
    /// <summary>The name of the phase this node adds.</summary>
    public const string PhaseName = "城外街区";

    private const float BridgeRadius = 200;
    private const float BridgeTaken = 40;

    public override string Group => NodeGroups.Town;
    public override string Title => "四期 城外街区";
    public override string Description =>
        "城里满了就往城外长：先填城内剩下的可建设地，再从城门和墙外已有的房子向外长，离墙留出空地；比前几期离路更深，所以在城门口长成一团。离城最近的渡口，对岸桥头另长一小片。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Wall, Keys.Gates, Keys.Plaza, Keys.Routes, Keys.Crossings, Keys.Habitability, Keys.Buildable, Keys.Landmarks];
    public override IReadOnlyList<IKey> Edits => [Keys.Growth];

    protected override void Run(NodeContext c, SuburbsOptions o)
    {
        var history = c.Get(Keys.Growth);
        var wall = c.Get(Keys.Wall);
        var network = c.Get(Keys.Routes);
        var buildable = c.Get(Keys.Buildable);
        var habitability = c.Get(Keys.Habitability);
        var grid = buildable.Grid;

        // Inside cells, and the cells too close to the wall on either side.
        var inside = wall.Inside(grid);
        var nearWall = new bool[grid.Count];
        float reach = Math.Max(o.InsideSetback, o.OutsideSetback);
        foreach (int k in grid.CellsIn(wall.Interior.Offset(reach)))
        {
            float d = Geometry.DistanceToRing(grid.Center(k), wall.Ring);
            nearWall[k] = d < (inside[k] ? o.InsideSetback : o.OutsideSetback);
        }

        // The landmarks' plots count as too near the wall: nothing grows on them.
        foreach (int k in c.Get(Keys.Landmarks).Cells())
            nearWall[k] = true;

        var built = history.BuiltMask();
        int target = (int)MathF.Round(o.Share * Enumerable.Range(0, grid.Count).Count(k => built[k] && inside[k]));
        var gates = c.Get(Keys.Gates).Cells(grid).Where(network.Cells.Contains).ToList();
        var picked = new List<int>();
        List<int> Grow(IEnumerable<int> seeds, int count, Func<int, bool> allowed)
        {
            if (count <= 0)
                return new List<int>();
            var cells = Growth.Spread(network, buildable, habitability, built, seeds, count, o.Rules, allowed, c.Cancellation);
            foreach (int k in cells)
                built[k] = true;
            picked.AddRange(cells);
            return cells;
        }

        // Inside first, from the market and the gates, as phase three did.
        int filled = Grow(Growth.MarketSeeds(network, c.Get(Keys.Plaza)).Concat(gates), target, k => inside[k] && !nearWall[k]).Count;

        // Then out of the gates; the built arms outside join the frontier by themselves.
        bool Outside(int k) => !inside[k] && !nearWall[k];
        int outside = Grow(gates, target - filled, Outside).Count;
        if (filled + outside < target)
            c.Notice($"可建设地不足，这一期只长了 {grid.Hectares(filled + outside):0.0} ha");

        if (filled < target)
            Bridgehead(c, o, wall, network, buildable, grid, built, Grow, Outside);
        c.Set(Keys.Growth, history.Add(PhaseName, picked));
    }

    /// <summary>
    /// Across the crossing in use whose far bank is nearest the wall (within reach), grows a small patch from the
    /// route cell at that bank, on that side of the river, unless the town already stands there.
    /// </summary>
    private static void Bridgehead(NodeContext c, SuburbsOptions o, WallRing wall, RouteNetwork network,
        CategoryField buildable, SiteGrid grid, bool[] built, Func<IEnumerable<int>, int, Func<int, bool>, List<int>> grow,
        Func<int, bool> outside)
    {
        int count = grid.CellCount(o.BridgeArea);
        if (count == 0)
            return;

        var best = c.Get(Keys.Crossings).UsedBy(network)
            .Select(x =>
            {
                float a = Geometry.DistanceToRing(x.BankA, wall.Ring), b = Geometry.DistanceToRing(x.BankB, wall.Ring);
                return a >= b ? (Far: x.BankA, Near: x.BankB, Distance: a, x.Id) : (Far: x.BankB, Near: x.BankA, Distance: b, x.Id);
            })
            .Where(x => x.Distance <= o.BridgeReach && !wall.Interior.Contains(x.Far))
            .OrderBy(x => x.Distance).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => ((Vector2 Far, Vector2 Near)?)(x.Far, x.Near))
            .FirstOrDefault();
        if (best is not { } bank)
            return;

        int around = grid.CellAt(bank.Far);
        if (around >= 0 && grid.AnyNear(around, grid.Span(BridgeTaken, 0) + 1,
                k => built[k] && grid.Center(k).DistanceTo(bank.Far) <= BridgeTaken))
            return;

        var seed = network.Cells.Where(buildable.Buildable)
            .MinBy(k => (grid.Center(k).DistanceSquaredTo(bank.Far), k));
        bool FarSide(int k)
        {
            var p = grid.Center(k);
            float d = p.DistanceTo(bank.Far);
            return outside(k) && d <= BridgeRadius && d < p.DistanceTo(bank.Near);
        }

        if (!FarSide(seed))
            return;
        var cells = grow(new[] { seed }, count, FarSide);
        if (cells.Count < count)
            c.Notice($"对岸桥头可建设地不足，只长了 {grid.Hectares(cells.Count):0.0} ha");
    }
}
