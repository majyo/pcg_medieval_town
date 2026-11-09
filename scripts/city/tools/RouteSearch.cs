using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// What a through route pays to cross the site grid. Each cell costs its travel cost per meter (water only on the
/// cells of a crossing, at the crossing price); on top, a step climbing or descending more steeply than
/// <see cref="FreeGrade"/> pays a penalty growing with the square of the excess. The penalty depends on the
/// direction of travel, so a road crosses a steep valley side on a slant rather than straight up.
/// </summary>
public sealed class RouteCosts
{
    private readonly float[] _cell;
    private readonly ScalarField _height;

    public RouteCosts(ScalarField travelCost, ScalarField height, IEnumerable<Crossing> crossings, float crossingCost,
        float freeGrade, float gradePenalty)
    {
        Grid = travelCost.Grid;
        _height = height;
        _cell = new float[Grid.Count];
        for (int k = 0; k < _cell.Length; k++)
        {
            float v = travelCost.Values[k];
            _cell[k] = travelCost.IsNoData(v) ? float.PositiveInfinity : v;
        }

        foreach (var crossing in crossings)
        {
            foreach (int k in crossing.Cells)
            {
                if (float.IsPositiveInfinity(_cell[k]))
                    _cell[k] = crossingCost;
            }
        }

        FreeGrade = freeGrade;
        GradePenalty = gradePenalty;
    }

    public SiteGrid Grid { get; }

    /// <summary>Rise per meter a road climbs without penalty.</summary>
    public float FreeGrade { get; }

    public float GradePenalty { get; }

    public bool Passable(int k) => !float.IsPositiveInfinity(_cell[k]);

    /// <summary>Cost of the step between neighboring cells <paramref name="k"/> and <paramref name="j"/>.</summary>
    public float Step(int k, int j, float length) =>
        length * ((_cell[k] + _cell[j]) / 2 + Climb(_height.Values[j] - _height.Values[k], length));

    /// <summary>
    /// Cost of the straight line from <paramref name="a"/> to <paramref name="b"/> at its true length, priced in
    /// cell-sized pieces like <see cref="Step"/> (heights interpolated between cell centers).
    /// </summary>
    public float Line(Vector2 a, Vector2 b)
    {
        float length = a.DistanceTo(b);
        int pieces = Math.Max(1, (int)MathF.Ceiling(length / Grid.CellSize));
        float piece = length / pieces, total = 0;
        var p = a;
        float hp = _height.Sample(p), cp = _cell[Grid.CellAt(p)];
        for (int i = 1; i <= pieces; i++)
        {
            var q = a.Lerp(b, i / (float)pieces);
            float hq = _height.Sample(q), cq = _cell[Grid.CellAt(q)];
            total += piece * ((cp + cq) / 2 + Climb(hq - hp, piece));
            (p, hp, cp) = (q, hq, cq);
        }

        return total;
    }

    /// <summary>Extra cost per meter for climbing (or descending) <paramref name="rise"/> over <paramref name="length"/>.</summary>
    private float Climb(float rise, float length)
    {
        float excess = MathF.Max(0, MathF.Abs(rise) / length - FreeGrade) / FreeGrade;
        return GradePenalty * excess * excess;
    }
}

/// <summary>
/// Least-cost paths of through routes over the whole site grid (8 neighbors; a diagonal step may not cut the corner
/// of an impassable cell). Arrays instead of dictionaries, since the searches span the map.
/// </summary>
public static class RouteSearch
{
    private static readonly (int Dx, int Dz, float Length)[] Steps =
    {
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.4142f), (1, -1, 1.4142f), (-1, 1, 1.4142f), (-1, -1, 1.4142f),
    };

    /// <summary>The cheapest path from <paramref name="start"/> to <paramref name="goal"/>, or null.</summary>
    public static List<int>? Find(RouteCosts costs, int start, int goal, CancellationToken cancel)
    {
        int reached = Search(costs, start, goal, null, cancel, out var from, out _);
        return reached < 0 ? null : Trace(from, start, reached);
    }

    /// <summary>The cheapest path from <paramref name="start"/> to any cell marked in <paramref name="targets"/>, and its cost.</summary>
    public static (List<int>? Path, float Cost) FindToAny(RouteCosts costs, int start, bool[] targets, CancellationToken cancel)
    {
        int reached = Search(costs, start, -1, targets, cancel, out var from, out var cost);
        return reached < 0 ? (null, float.PositiveInfinity) : (Trace(from, start, reached), cost[reached]);
    }

    /// <summary>
    /// Pulls a grid path straight: from each kept cell, jumps to the farthest later cell (looking up to
    /// <see cref="Lookahead"/> cells past the last success) whose straight line, priced at its true length, costs no
    /// more than the grid path it replaces and whose cells are all passable. A grid path is up to 8% longer than the
    /// straight line, so on open ground the runs of straight and 45° steps become one line at any angle; detours
    /// around steep ground or to a crossing stay. The result is the cells of those lines, and in
    /// <paramref name="corners"/> the cells where the lines meet (including both ends).
    /// </summary>
    public static List<int> Straighten(RouteCosts costs, List<int> path, out List<int> corners)
    {
        var grid = costs.Grid;
        // along[i] = cost of the path from its start to cell i.
        var along = new float[path.Count];
        for (int i = 1; i < path.Count; i++)
            along[i] = along[i - 1] + costs.Step(path[i - 1], path[i], grid.Center(path[i - 1]).DistanceTo(grid.Center(path[i])));

        var result = new List<int> { path[0] };
        corners = new List<int> { path[0] };
        int anchor = 0;
        while (anchor < path.Count - 1)
        {
            int best = anchor + 1;
            List<int>? bestLine = null;
            for (int j = anchor + 2; j < path.Count && j - best <= Lookahead; j++)
            {
                var line = Line(grid, path[anchor], path[j]);
                if (Open(costs, line) && costs.Line(grid.Center(path[anchor]), grid.Center(path[j])) <= along[j] - along[anchor])
                {
                    best = j;
                    bestLine = line;
                }
            }

            if (bestLine == null)
                result.Add(path[best]);
            else
                result.AddRange(bestLine.Skip(1));
            corners.Add(path[best]);
            anchor = best;
        }

        return result;
    }

    private const int Lookahead = 24;

    /// <summary>True if every cell of the chain is passable and no diagonal step cuts the corner of an impassable cell.</summary>
    private static bool Open(RouteCosts costs, List<int> cells)
    {
        var grid = costs.Grid;
        for (int i = 1; i < cells.Count; i++)
        {
            int k = cells[i - 1], j = cells[i];
            int x = k % grid.Nx, z = k / grid.Nx, jx = j % grid.Nx, jz = j / grid.Nx;
            if (!costs.Passable(j) || x != jx && z != jz && (!costs.Passable(grid.Index(jx, z)) || !costs.Passable(grid.Index(x, jz))))
                return false;
        }

        return true;
    }

    /// <summary>The cells of a straight line between two cells, each an 8-neighbor of the one before (Bresenham).</summary>
    private static List<int> Line(SiteGrid grid, int a, int b)
    {
        int x0 = a % grid.Nx, z0 = a / grid.Nx, x1 = b % grid.Nx, z1 = b / grid.Nx;
        int dx = Math.Abs(x1 - x0), dz = -Math.Abs(z1 - z0), sx = Math.Sign(x1 - x0), sz = Math.Sign(z1 - z0);
        int error = dx + dz;
        var cells = new List<int> { a };
        while (x0 != x1 || z0 != z1)
        {
            int twice = 2 * error;
            if (twice >= dz)
            {
                error += dz;
                x0 += sx;
            }

            if (twice <= dx)
            {
                error += dx;
                z0 += sz;
            }

            cells.Add(grid.Index(x0, z0));
        }

        return cells;
    }

    private static List<int> Trace(int[] from, int start, int end)
    {
        var path = new List<int> { end };
        for (int k = end; k != start;)
            path.Add(k = from[k]);
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Dijkstra, or A* toward a single <paramref name="goal"/> (every step costs at least its length, so the straight
    /// distance is an admissible estimate). Returns the first goal or target cell settled, or -1.
    /// </summary>
    private static int Search(RouteCosts costs, int start, int goal, bool[]? targets, CancellationToken cancel,
        out int[] from, out float[] cost)
    {
        var grid = costs.Grid;
        int n = grid.Count;
        cost = new float[n];
        Array.Fill(cost, float.PositiveInfinity);
        from = new int[n];
        var closed = new bool[n];
        var open = new PriorityQueue<int, float>();
        var target = goal >= 0 ? grid.Center(goal) : default;
        float Estimate(int k) => goal >= 0 ? grid.Center(k).DistanceTo(target) : 0;
        if (!costs.Passable(start) || goal >= 0 && !costs.Passable(goal))
            return -1;

        cost[start] = 0;
        open.Enqueue(start, Estimate(start));
        int expanded = 0;
        while (open.TryDequeue(out int k, out _))
        {
            if (closed[k])
                continue;
            closed[k] = true;
            if (k == goal || targets != null && targets[k])
                return k;
            if (++expanded % 8192 == 0)
                cancel.ThrowIfCancellationRequested();

            int x = k % grid.Nx, z = k / grid.Nx;
            foreach (var (dx, dz, unit) in Steps)
            {
                int jx = x + dx, jz = z + dz;
                if (!grid.InGrid(jx, jz))
                    continue;
                int j = grid.Index(jx, jz);
                if (closed[j] || !costs.Passable(j))
                    continue;
                if (dx != 0 && dz != 0 && (!costs.Passable(grid.Index(jx, z)) || !costs.Passable(grid.Index(x, jz))))
                    continue;
                float next = cost[k] + costs.Step(k, j, unit * grid.CellSize);
                if (next >= cost[j])
                    continue;
                cost[j] = next;
                from[j] = k;
                open.Enqueue(j, next + Estimate(j));
            }
        }

        return -1;
    }
}
