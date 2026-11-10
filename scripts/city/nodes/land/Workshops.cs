using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

public sealed class WorkshopsOptions
{
    [Param("湿作坊面积 ha", 0, 5, 0.1,
        Hint = "鞣革、染坊、屠宰、漂洗：在城的下游河边取这么大一片；0 表示不设")]
    public float WetArea { get; set; } = 1.5f;
    [Param("火作坊面积 ha", 0, 3, 0.1,
        Hint = "陶窑、砖瓦窑、石灰窑、铁匠：每片这么大，在离房屋远、靠过境道路的平地上")]
    public float FireArea { get; set; } = 1;
    [Param("火作坊片数", 0, 4,
        Hint = "火作坊最多几片，片与片相距至少 200 m")]
    public int FireCount { get; set; } = 2;
    [Param("火作坊离房屋 m", 0, 300, 10,
        Hint = "火作坊离任何建成格至少这么远（防火、烟）")]
    public float FireClear { get; set; } = 80;
    [Param("水磨处数", 0, 4,
        Hint = "在离城墙 100–500 m 的河岸取几处水磨，每处约 0.1 ha，相距至少 150 m")]
    public int MillCount { get; set; } = 2;
}

/// <summary>
/// The trades pushed out of the town, each on unassigned land and nearest the town by <c>land.reach</c>: wet trades
/// on the river bank downstream of the town, fire trades away from the houses starting beside a through route, and
/// water mills on the bank away from the crossings. Each grows as one patch from its best cell, nearest first.
/// </summary>
public sealed class Workshops : CityNode<WorkshopsOptions>
{
    private const float WetShore = 60, WetDrop = 0.5f, WetFrom = 50, WetTo = 400;
    private const float FireTo = 600, FireRoad = 60, FireGap = 200;
    private const float MillShore = 12, MillFrom = 100, MillTo = 500, MillCrossing = 80, MillGap = 150, MillArea = 0.1f;
    private const float FlatSlope = 8;

    public override string Group => NodeGroups.Zoning;
    public override string Title => "作坊";
    public override string Description =>
        "把被挤出城的作坊放到城外：湿作坊在城下游的河边，火作坊离房屋远、靠过境道路，水磨在离渡口远的河岸；都从离城最近的合适地块开始取。";
    public override IReadOnlyList<IKey> Reads =>
    [
        Keys.LandUse, Keys.Reach, Keys.Shore, Keys.AboveWater, Keys.Height, Keys.Slope, Keys.Buildable, Keys.Wall,
        Keys.Growth, Keys.Routes, Keys.Crossings,
    ];
    public override IReadOnlyList<IKey> Edits => [Keys.LandUse];

    protected override void Run(NodeContext c, WorkshopsOptions o)
    {
        var land = c.Get(Keys.LandUse);
        var reach = c.Get(Keys.Reach);
        var shore = c.Get(Keys.Shore);
        var slope = c.Get(Keys.Slope);
        var buildable = c.Get(Keys.Buildable);
        var growth = c.Get(Keys.Growth);
        var network = c.Get(Keys.Routes);
        var grid = land.Grid;
        var use = land.Edit();
        var toWall = Proximity.ToRing(grid, c.Get(Keys.Wall).Ring, FireTo + grid.CellSize);
        bool Free(int k) => use[k] == (byte)LandUse.Unassigned && !reach.IsNoData(reach.Values[k]);
        bool Flat(int k) => slope.Values[k] <= FlatSlope;

        // Wet trades: below the town's water level, by the river, clear of the wall.
        if (grid.CellCount(o.WetArea) > 0)
        {
            var level = WaterLevel(c.Get(Keys.Height), c.Get(Keys.AboveWater));
            float townLevel = TownWaterLevel(shore, level, toWall);
            bool found = float.IsFinite(townLevel);
            bool Wet(int k) => Free(k) && Flat(k) && shore.Values[k] >= 0 && shore.Values[k] <= WetShore
                && level[k] <= townLevel - WetDrop && toWall[k] >= WetFrom && toWall[k] <= WetTo
                && (buildable.Buildable(k) || buildable.Block(k) is SiteBlock.Flood or SiteBlock.Bank);
            if (!found || Take(Wet, Wet, grid.CellCount(o.WetArea), LandUse.WetWorks).Count == 0)
                c.Notice("城的下游河边没有合适的地，没有设湿作坊");
        }

        // Fire trades: away from every house, near a road for the fuel and clay, not too far out.
        if (o.FireCount > 0 && grid.CellCount(o.FireArea) > 0)
        {
            var nearHouses = Proximity.From(grid, growth.BuiltCells, o.FireClear);
            var nearRoad = Proximity.From(grid, network.Cells, FireRoad);
            var blocked = new bool[grid.Count];
            int placed = 0;
            for (int i = 0; i < o.FireCount; i++)
            {
                bool Fire(int k) => Free(k) && Flat(k) && buildable.Buildable(k) && !blocked[k]
                    && float.IsPositiveInfinity(nearHouses[k]) && toWall[k] <= FireTo;
                var patch = Take(k => Fire(k) && float.IsFinite(nearRoad[k]), Fire, grid.CellCount(o.FireArea), LandUse.FireWorks);
                if (patch.Count == 0)
                    break;
                placed++;
                var gap = Proximity.From(grid, patch, FireGap);
                for (int k = 0; k < grid.Count; k++)
                    blocked[k] |= float.IsFinite(gap[k]);
            }

            if (placed < o.FireCount)
                c.Notice($"离房屋足够远、靠路的平地只够 {placed} 片火作坊");
        }

        // Mills: on the bank, clear of the wall and of the crossings in use.
        if (o.MillCount > 0)
        {
            var crossings = c.Get(Keys.Crossings).UsedBy(network).ToList();
            var blocked = new bool[grid.Count];
            int placed = 0;
            for (int i = 0; i < o.MillCount; i++)
            {
                bool Mill(int k) => Free(k) && !blocked[k] && shore.Values[k] >= 0 && shore.Values[k] <= MillShore
                    && toWall[k] >= MillFrom && toWall[k] <= MillTo
                    && crossings.All(x => x.Center.DistanceTo(grid.Center(k)) >= MillCrossing);
                var patch = Take(Mill, Mill, Math.Max(1, grid.CellCount(MillArea)), LandUse.Mill);
                if (patch.Count == 0)
                    break;
                placed++;
                var gap = Proximity.From(grid, patch, MillGap);
                for (int k = 0; k < grid.Count; k++)
                    blocked[k] |= float.IsFinite(gap[k]);
            }

            if (placed < o.MillCount)
                c.Notice($"离城墙 {MillFrom:0}–{MillTo:0} m、避开渡口的河岸只够 {placed} 处水磨");
        }

        c.Set(Keys.LandUse, LandUses.Field(grid, use));

        // One patch of `use`: from the seed cell nearest the town whose connected candidates hold at least half the
        // patch (else the one with the most), outward through the candidates, nearest first.
        List<int> Take(Func<int, bool> seedOk, Func<int, bool> candidate, int count, LandUse what)
        {
            var patch = new List<int>();
            var seeds = Enumerable.Range(0, grid.Count).Where(k => seedOk(k) && candidate(k))
                .OrderBy(k => reach.Values[k]).ThenBy(k => k).ToList();
            var part = new int[grid.Count];
            var sizes = new List<int> { 0 };
            int seed = -1, best = -1;
            foreach (int s in seeds)
            {
                if (part[s] == 0)
                    sizes.Add(Flood(s, sizes.Count));
                int size = sizes[part[s]];
                if (best < 0 || size > sizes[part[best]])
                    best = s;
                if (size * 2 >= count)
                {
                    seed = s;
                    break;
                }
            }

            seed = seed >= 0 ? seed : best;
            if (seed < 0)
                return patch;
            var open = new PriorityQueue<int, float>();
            var queued = new HashSet<int> { seed };
            open.Enqueue(seed, reach.Values[seed]);
            while (patch.Count < count && open.TryDequeue(out int k, out _))
            {
                patch.Add(k);
                use[k] = (byte)what;
                grid.ForSides(k, j =>
                {
                    if (queued.Add(j) && candidate(j))
                        open.Enqueue(j, reach.Values[j]);
                });
            }

            return patch;

            // Marks the candidates side-connected to `from` with `label`; returns how many there are.
            int Flood(int from, int label)
            {
                var stack = new Stack<int>();
                stack.Push(from);
                part[from] = label;
                int n = 0;
                while (stack.TryPop(out int k))
                {
                    n++;
                    grid.ForSides(k, j =>
                    {
                        if (part[j] == 0 && candidate(j))
                        {
                            part[j] = label;
                            stack.Push(j);
                        }
                    });
                }

                return n;
            }
        }
    }

    /// <summary>The water level each cell looks onto: its height less its height above the water.</summary>
    public static float[] WaterLevel(ScalarField height, ScalarField aboveWater)
    {
        var level = new float[height.Values.Length];
        for (int k = 0; k < level.Length; k++)
            level[k] = height.Values[k] - aboveWater.Values[k];
        return level;
    }

    /// <summary>
    /// The town's water level: that of the water cell nearest the wall (among those <paramref name="toWall"/> covers),
    /// or NaN when no water is that close. Below it is downstream.
    /// </summary>
    public static float TownWaterLevel(ScalarField shore, float[] level, float[] toWall)
    {
        int best = -1;
        for (int k = 0; k < level.Length; k++)
        {
            if (shore.Values[k] < 0 && float.IsFinite(toWall[k]) && (best < 0 || toWall[k] < toWall[best]))
                best = k;
        }

        return best < 0 ? float.NaN : level[best];
    }
}
