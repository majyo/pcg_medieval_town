using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.City;

/// <summary>
/// How far each cell lies from the town for someone walking out of a gate: a Dijkstra over the whole map from the
/// gates, priced like the through routes (travel cost per meter plus the climbing penalty), crossing the river only
/// on the crossings in use. The wall is crossed only at the gates, so the inside is 0 and not a shortcut.
/// </summary>
public sealed class TownReach : CityNode<NoOptions>
{
    /// <summary>The value of cells no one can walk to from a gate (water, cliffs, land cut off by the river).</summary>
    public const float NoData = -1;

    public override string Group => NodeGroups.Zoning;
    public override string Title => "离城路程";
    public override string Description =>
        "从各城门出发，按过境道路的通行代价走到每一格的路程：爬坡、林地都算远，过河只能走在用的渡口；城内为 0。作坊和农业带用它判断“离城多远”。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.TravelCost, Keys.Height, Keys.Wall, Keys.Gates, Keys.Routes, Keys.Crossings];
    public override IReadOnlyList<IKey> Writes => [Keys.Reach];

    protected override void Run(NodeContext c, NoOptions o)
    {
        // Priced as the through routes are by default, so "far" means the same to a farmer as to a carter.
        var prices = new RegionalRoutesOptions();
        var costs = new RouteCosts(c.Get(Keys.TravelCost), c.Get(Keys.Height), c.Get(Keys.Crossings).UsedBy(c.Get(Keys.Routes)),
            prices.CrossingCost, prices.FreeGrade / 100, prices.GradePenalty);
        var grid = costs.Grid;
        var inside = c.Get(Keys.Wall).Inside(grid);

        var reach = new float[grid.Count];
        Array.Fill(reach, float.PositiveInfinity);
        var open = new PriorityQueue<int, float>();
        foreach (var gate in c.Get(Keys.Gates).Gates)
        {
            int k = grid.CellAt(gate.Position);
            if (k >= 0 && reach[k] > 0)
            {
                reach[k] = 0;
                open.Enqueue(k, 0);
            }
        }

        float diagonal = grid.CellSize * MathF.Sqrt(2);
        int popped = 0;
        while (open.TryDequeue(out int k, out float d))
        {
            if (d > reach[k])
                continue;
            if (++popped % 8192 == 0)
                c.Cancellation.ThrowIfCancellationRequested();
            int x = k % grid.Nx, z = k / grid.Nx;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dz) == 0 || !grid.InGrid(x + dx, z + dz))
                        continue;
                    int j = grid.Index(x + dx, z + dz);
                    if (inside[j] || !costs.Passable(j))
                        continue;
                    float nd = d + costs.Step(k, j, dx != 0 && dz != 0 ? diagonal : grid.CellSize);
                    if (nd < reach[j])
                    {
                        reach[j] = nd;
                        open.Enqueue(j, nd);
                    }
                }
            }
        }

        for (int k = 0; k < grid.Count; k++)
        {
            if (inside[k])
                reach[k] = 0;
            else if (float.IsPositiveInfinity(reach[k]))
                reach[k] = NoData;
        }

        c.Set(Keys.Reach, new ScalarField(grid, reach, "离城路程", "m", NoData));
    }
}
