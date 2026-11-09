using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public static class RegionalFrame
{
    public const string Group = "区域格局";
}

public sealed class RiverCrossingsOptions
{
    [Param("候选间距 m", 8, 80, 2)] public float Spacing { get; set; } = 20;
    [Param("最大河宽 m", 20, 150, 5)] public float MaxWidth { get; set; } = 70;
    [Param("引道长度 m", 10, 80, 5)] public float Approach { get; set; } = 30;
    [Param("渡口最小间距 m", 100, 1000, 20)] public float MinGap { get; set; } = 300;
    [Param("渡口数", 1, 6)] public int Count { get; set; } = 3;
}

/// <summary>
/// Good places to cross the river. Candidates sit on the channel's center line (where the distance to the water's
/// edge is lowest across the channel), one every few meters; each is crossed along its shortest line across the water.
/// A crossing scores well where the river is narrow, both banks stand a little above the water (neither a bluff nor a
/// marsh), and the ground beyond both banks is level. The best ones, spread apart, are kept.
/// </summary>
public sealed class RiverCrossings : CityNode<RiverCrossingsOptions>
{
    private const int Directions = 16;

    public override string Group => RegionalFrame.Group;
    public override string Title => "渡口";
    public override IReadOnlyList<IKey> Reads => [Keys.Shore, Keys.AboveWater, Keys.Slope, Keys.TravelCost];
    public override IReadOnlyList<IKey> Writes => [Keys.Crossings];

    protected override void Run(NodeContext c, RiverCrossingsOptions o)
    {
        var shore = c.Get(Keys.Shore);
        var above = c.Get(Keys.AboveWater);
        var slope = c.Get(Keys.Slope);
        var cost = c.Get(Keys.TravelCost);
        var grid = shore.Grid;
        bool Passable(Vector2 p)
        {
            int k = grid.CellAt(p);
            return k >= 0 && !cost.IsNoData(cost.Values[k]);
        }

        var candidates = new List<(Vector2 Center, Vector2 Across, float Width, float Score, Vector2 A, Vector2 B)>();
        foreach (var center in CenterLine(shore, o.Spacing))
        {
            c.Cancellation.ThrowIfCancellationRequested();
            var (across, ta, tb) = Narrowest(shore, center, o.MaxWidth);
            float width = ta + tb;
            if (width > o.MaxWidth)
                continue;

            // Bridgeheads: the first passable land beyond each bank, a little further if the bank itself is a cliff.
            var bankA = center + across * ta;
            var bankB = center - across * tb;
            var headA = Beyond(bankA, across, Passable);
            var headB = Beyond(bankB, -across, Passable);
            if (headA == null || headB == null)
                continue;

            float bank = Math.Min(BankScore(above.Sample(bankA + across * 10)), BankScore(above.Sample(bankB - across * 10)));
            float steep = Math.Max(MeanSlope(slope, bankA, across, o.Approach), MeanSlope(slope, bankB, -across, o.Approach));
            float narrow = 1 - width / o.MaxWidth;
            float level = 1 - Mathf.SmoothStep(2, 12, steep);
            float score = narrow * (0.4f + 0.6f * bank) * (0.4f + 0.6f * level);
            candidates.Add((center, across, width, score, grid.Center(grid.CellAt(headA.Value)), grid.Center(grid.CellAt(headB.Value))));
        }

        var chosen = new List<Crossing>();
        foreach (var candidate in candidates.OrderByDescending(x => x.Score))
        {
            if (chosen.Count >= o.Count)
                break;
            if (chosen.Any(x => x.Center.DistanceTo(candidate.Center) < o.MinGap))
                continue;
            chosen.Add(new Crossing($"crossing_{chosen.Count + 1}", candidate.Center, candidate.A, candidate.B, candidate.Width,
                candidate.Score, Line(grid, candidate.A, candidate.B)));
        }

        if (chosen.Count == 0)
            c.Notice(candidates.Count == 0 ? "河道上没有宽度合适、两岸可通行的位置" : "没有渡口");
        else if (chosen.Count < o.Count)
            c.Notice($"只找到 {chosen.Count} 处相距足够远的渡口");
        c.Set(Keys.Crossings, new CrossingSet(chosen));
    }

    /// <summary>
    /// Cells on the channel's center line, at least <paramref name="spacing"/> apart: water cells at least 2 m from the
    /// edge whose distance to the edge is lowest along a row or a column (ties go to the first cell).
    /// </summary>
    private static List<Vector2> CenterLine(ScalarField shore, float spacing)
    {
        var grid = shore.Grid;
        var v = shore.Values;
        var points = new List<Vector2>();
        for (int z = 1; z < grid.Nz - 1; z++)
            for (int x = 1; x < grid.Nx - 1; x++)
            {
                int k = grid.Index(x, z);
                if (v[k] >= -2)
                    continue;
                bool alongX = v[k] <= v[k - 1] && v[k] < v[k + 1];
                bool alongZ = v[k] <= v[k - grid.Nx] && v[k] < v[k + grid.Nx];
                if (!alongX && !alongZ)
                    continue;
                var p = grid.Center(k);
                if (points.All(q => q.DistanceTo(p) >= spacing))
                    points.Add(p);
            }

        return points;
    }

    /// <summary>
    /// The direction across the water with the shortest crossing through <paramref name="center"/>, and how far the
    /// edge lies each way. Edges farther than <paramref name="limit"/> count as at the limit.
    /// </summary>
    private static (Vector2 Across, float A, float B) Narrowest(ScalarField shore, Vector2 center, float limit)
    {
        var best = (Across: Vector2.Right, A: limit, B: limit);
        for (int i = 0; i < Directions; i++)
        {
            var direction = Vector2.FromAngle(i * MathF.PI / Directions);
            float a = ToEdge(shore, center, direction, limit), b = ToEdge(shore, center, -direction, limit);
            if (a + b < best.A + best.B)
                best = (direction, a, b);
        }

        return best;
    }

    private static float ToEdge(ScalarField shore, Vector2 from, Vector2 direction, float limit)
    {
        for (float t = 0; t < limit; t += 1)
        {
            if (shore.Sample(from + direction * t) >= 0)
                return t;
        }

        return limit;
    }

    /// <summary>The first passable point from <paramref name="bank"/> outward, within 20 m.</summary>
    private static Vector2? Beyond(Vector2 bank, Vector2 outward, Func<Vector2, bool> passable)
    {
        for (float t = 0; t <= 20; t += 1)
        {
            var p = bank + outward * t;
            if (passable(p))
                return p;
        }

        return null;
    }

    /// <summary>1 for banks 1–6 m above the water, falling to 0 for marsh at the water level or a bluff 12 m up.</summary>
    private static float BankScore(float height) =>
        height < 1 ? Mathf.Clamp(height, 0, 1) : height <= 6 ? 1 : 1 - Mathf.Clamp((height - 6) / 6, 0, 1);

    private static float MeanSlope(ScalarField slope, Vector2 bank, Vector2 outward, float length)
    {
        float total = 0;
        int count = 0;
        for (float t = 2; t <= length; t += 2)
        {
            total += slope.Sample(bank + outward * t);
            count++;
        }

        return count == 0 ? 0 : total / count;
    }

    /// <summary>
    /// Every cell the segment from <paramref name="a"/> to <paramref name="b"/> passes through, in order, each a side
    /// neighbor of the one before (grid traversal; at an exact corner the step goes along x first).
    /// </summary>
    private static int[] Line(SiteGrid grid, Vector2 a, Vector2 b)
    {
        var cells = new List<int>();
        var g = (a - grid.Origin) / grid.CellSize;
        var e = (b - grid.Origin) / grid.CellSize;
        int x = (int)MathF.Floor(g.X), z = (int)MathF.Floor(g.Y);
        int ex = (int)MathF.Floor(e.X), ez = (int)MathF.Floor(e.Y);
        var d = e - g;
        int sx = Math.Sign(d.X), sz = Math.Sign(d.Y);
        float tdx = sx == 0 ? float.PositiveInfinity : 1 / MathF.Abs(d.X);
        float tdz = sz == 0 ? float.PositiveInfinity : 1 / MathF.Abs(d.Y);
        float tx = sx == 0 ? float.PositiveInfinity : (sx > 0 ? x + 1 - g.X : g.X - x) * tdx;
        float tz = sz == 0 ? float.PositiveInfinity : (sz > 0 ? z + 1 - g.Y : g.Y - z) * tdz;
        cells.Add(grid.Index(x, z));
        while ((x != ex || z != ez) && Math.Min(tx, tz) <= 1.0001f)
        {
            if (tx <= tz)
            {
                x += sx;
                tx += tdx;
            }
            else
            {
                z += sz;
                tz += tdz;
            }

            if (!grid.InGrid(x, z))
                break;
            cells.Add(grid.Index(x, z));
        }

        return cells.ToArray();
    }
}
