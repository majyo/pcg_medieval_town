using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class TerracesOptions
{
    [Param("台地",
        Hint = "关掉后没有台地：地块按原来的方式切，房屋地坪取四角处修整后地面的最高点")]
    public bool Enabled { get; set; } = true;
    [Param("台阶高差上限 m", 0.3, 3, 0.1,
        Hint = "沿街相邻两块台地的高差上限。调小台阶更矮、更密，台地更碎；街太陡时做不到，记为超过上限的台阶")]
    public float StepMax { get; set; } = 2;
    [Param("最短台地长 m", 4, 40, 1,
        Hint = "一块台地沿街至少多长（两三块地的面宽）；短于它的台地并进邻块。"
            + "台阶高差上限 ÷ 它是一级台阶能应付的街道坡度（默认 10%），调小坡街上台阶更密")]
    public float MinLength { get; set; } = 20;
    [Param("门前高差 m", 0.1, 1.5, 0.05,
        Hint = "台地比它门前的街高出或低出多少以内（门口的几级台阶）；调大台地可以更长")]
    public float Doorstep { get; set; } = 1;
    [Param("离原地形 m", 0, 3, 0.1,
        Hint = "台地最多比它沿街一段的原地形平均高度高出或低出这么多")]
    public float MaxOffset { get; set; } = 1;
    [Param("台地进深 m", 6, 30, 1,
        Hint = "台地从临街线起整平多深（房子和它门口的地）；后面的后院不整平")]
    public float Depth { get; set; } = 16;
    [Param("作坊台地进深 m", 6, 40, 1,
        Hint = "作坊的台地整平多深：作坊的棚屋可以从临街边退后，院子也要平")]
    public float TradeDepth { get; set; } = 28;
    [Param("合并高差 m", 0, 2, 0.05,
        Hint = "同一街区里相邻两块台地高差小于它、有一个高度能让两边的门前高差和离原地形都在限度内时，"
            + "合成一块（取这些高度里最接近两块按临街长加权平均的一个）；调大台地更少更大，门前高差更大")]
    public float MergeStep { get; set; } = 1;
    [Param("台阶代价", 0, 20, 0.5,
        Hint = "沿街每多一级台阶的代价（与门前高差的平方和相比）。调大台地更长、门前高差更大，调小台阶更多")]
    public float StepCost { get; set; } = 2;
    [Param("地标台地",
        Hint = "关掉后教堂、城堡、市政厅、修道院、麻风院的用地贴着原地形")]
    public bool Landmarks { get; set; } = true;
}

/// <summary>
/// Cuts the blocks into terraces along their streets, before any plot is cut. Each side of each road is walked as the
/// plots walk it (main streets first, longer before shorter); where it runs beside one block, a dynamic program splits
/// it into stretches no shorter than a minimum, each level at the street's height at its middle (held near its own
/// ground), trading the steps between stretches against how far each stands from its street. Heights are kept in the
/// street's order (no step against the slope). Each stretch then claims the block behind it and grows back as the
/// plots do; its front part, as deep as a house, is level, the rest is back land it may reach. Neighbors in one block
/// whose heights differ little are joined. Each landmark's plot is one terrace.
/// </summary>
public sealed class Terraces : CityNode<TerracesOptions>
{
    /// <summary>How deep behind its front a terrace's reach may grow: as deep as a plot may.</summary>
    public const float ReachDepth = 50;

    /// <summary>
    /// How far a terrace may stand from the ground under it, in meters: ground farther off (a bank behind a deep
    /// terrace) is left out of its level part and eased to by the solved ground.
    /// </summary>
    public const float MaxEarthwork = 2.5f;

    /// <summary>
    /// Terraces whose level part is smaller than this (or whose front is shorter than the shortest terrace) join the
    /// neighbor they share most border with: too small for more than one house.
    /// </summary>
    private const float MinArea = 150;

    private const float Step = 2;
    private const float MaxGap = 1500;

    public override string Group => NodeGroups.Grading;
    public override string Title => "台地";
    public override string Description =>
        "在切地块之前把街区沿街分成台地：每条街两侧按段整平，每段与它中点处的街同高（不离原地形太远），坡街上一级级下去；" +
        "台地从临街线起一个进深是水平的，后院不动；高差小的相邻台地合成一块；地标用地各一块。";
    public override IReadOnlyList<IKey> Reads =>
        [Keys.Blocks, Keys.Roads, Keys.Grading, Keys.Landmarks, Keys.LandUse, Keys.Wall, Keys.Shore];
    public override IReadOnlyList<IKey> Writes => [Keys.Terraces];

    private sealed class Stretch
    {
        public required int Edge;
        public required int Side;
        public required float From, To;
        public required float Mid, Low, High, Ground, Floor;
        public required float Height;
        public required bool Trade;
        public bool Steep;
        public readonly List<int> Claims = new();
        public int Cluster = -1;

        /// <summary>The stretches before and after it along its street that have ground of their own.</summary>
        public Stretch? Before, After;
    }

    protected override void Run(NodeContext c, TerracesOptions o)
    {
        if (!o.Enabled)
        {
            c.Set(Keys.Terraces, new TerraceSet([], [], o.StepMax, o.Doorstep, o.MaxOffset, 0));
            return;
        }

        var blocks = c.Get(Keys.Blocks).Blocks;
        var roads = c.Get(Keys.Roads);
        var plan = c.Get(Keys.Grading);
        var land = c.Get(Keys.LandUse);
        var interior = c.Get(Keys.Wall).Interior;
        var terrain = c.Terrain;
        var sweep = new FrontSweep(blocks, c.Cancellation);
        var stretches = new List<Stretch>();
        var runs = new List<List<int>>();

        // The sides of the roads, in the plots' order: who claims a corner first is the same for terraces and plots.
        foreach (var edge in roads.Edges.OrderBy(e => e.Kind).ThenByDescending(e => e.Length).ThenBy(e => e.Id))
        {
            c.Cancellation.ThrowIfCancellationRequested();
            var line = new Polyline(edge.Points);
            float front = edge.Width / 2;
            int n = (int)MathF.Floor(line.Length);
            if (n < 1)
                continue;
            foreach (int side in new[] { 1, -1 })
            {
                // A station every meter: the block beside it, the street's graded height, the ground behind.
                var block = new int[n];
                var street = new float[n];
                var ground = new float[n];
                for (int k = 0; k < n; k++)
                {
                    var (p, inward) = line.At(k + 0.5f, side);
                    block[k] = sweep.BlockAt(p + inward * (front + 1.5f));
                    if (block[k] < 0)
                        continue;
                    street[k] = plan.HeightAt(p);
                    float sum = 0;
                    foreach (float d in new[] { 2f, 6f, 10f, 14f })
                        sum += terrain.HeightAt(p + inward * (front + d));
                    ground[k] = sum / 4;
                }

                for (int k0 = 0; k0 < n;)
                {
                    if (block[k0] < 0)
                    {
                        k0++;
                        continue;
                    }

                    int k1 = k0;
                    while (k1 < n && block[k1] == block[k0])
                        k1++;
                    var run = Split(edge, side, line, street, ground, k0, k1);
                    foreach (var s in run)
                        Claim(s, line, front);
                    runs.Add(run.Select(s => { stretches.Add(s); return stretches.Count - 1; }).ToList());
                    k0 = k1;
                }
            }

            void Claim(Stretch s, Polyline line, float front)
            {
                foreach (var (a, b, inward) in line.Pieces(s.From, s.To, s.Side, front - 0.5f))
                {
                    if (a.DistanceTo(b) < 1)
                        continue;
                    int id = sweep.Claim(a, b, inward, o.Depth + 0.5f, 1);
                    if (id >= 0)
                        s.Claims.Add(id);
                }
            }
        }

        sweep.GrowBack(o.Depth, ReachDepth, Step);
        sweep.FillGaps(MaxGap);

        // A ring meeting a diagonal block edge can pinch back on itself and poke into its neighbor: each claim gives up
        // what the claims before it hold.
        var shapes = new List<Region>();
        for (int id = 0; id < sweep.Shapes.Count; id++)
        {
            var shape = sweep.Shapes[id];
            foreach (var before in shapes)
            {
                if (before.Bounds.Intersects(shape.Bounds) && before.Intersect(shape).Area > 0.01)
                    shape = shape.Subtract(before);
            }

            shapes.Add(shape);
        }

        // Each stretch's ground: its claims, and the front part of them made level.
        var clusters = new List<Cluster>();
        foreach (var s in stretches.Where(s => s.Claims.Count > 0))
        {
            c.Cancellation.ThrowIfCancellationRequested();
            Region? reach = null, level = null;
            foreach (int id in s.Claims)
            {
                var shape = shapes[id];
                var (a, b, inward) = sweep.Fronts[id];
                var along = (b - a).Normalized();
                float depth = s.Trade ? o.TradeDepth : o.Depth;
                var band = Region.Polygon(new[]
                {
                    a - along * 40 - inward, b + along * 40 - inward, b + along * 40 + inward * depth, a - along * 40 + inward * depth,
                });
                var front = shape.Intersect(band);
                reach = reach == null ? shape : reach.Union(shape);
                level = level == null ? front : level.Union(front);
            }

            if (level == null || reach == null || level.Area < 1)
                continue;
            s.Cluster = clusters.Count;
            clusters.Add(new Cluster(sweep.BlockOf[s.Claims[0]], level, reach, [s]));
        }

        // The level part keeps off the water and the map's rim (the ground solver leaves both as they are) and off
        // ground too far above or below the terrace: lattice points by the solver's own rule, each taking its square meter.
        var shore = c.Get(Keys.Shore);
        float rim = terrain.Half - 2 * GradeBake.Rim;
        var inside = Region.Polygon(new[]
        {
            new Vector2(-rim, -rim), new Vector2(rim, -rim), new Vector2(rim, rim), new Vector2(-rim, rim),
        });
        void Trim(Cluster g)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            var level = g.Full.Intersect(inside);
            var off = new List<Vector2[]>();
            var box = level.Bounds;
            for (int z = (int)MathF.Floor(box.Position.Y); z <= box.End.Y; z++)
            {
                for (int x = (int)MathF.Floor(box.Position.X); x <= box.End.X; x++)
                {
                    var p = new Vector2(x, z);
                    if ((shore.Nearest(p) < 0 || MathF.Abs(terrain.HeightAt(p) - g.Height) > MaxEarthwork) && level.Contains(p))
                    {
                        off.Add([p + new Vector2(-0.5f, -0.5f), p + new Vector2(0.5f, -0.5f), p + new Vector2(0.5f, 0.5f),
                            p + new Vector2(-0.5f, 0.5f)]);
                    }
                }
            }

            g.Level = off.Count > 0 ? level.Subtract(Region.Polygons(off)) : level;
        }

        // Neighbors along each street, for keeping the heights in the street's order while joining.
        foreach (var run in runs)
        {
            var kept = run.Select(i => stretches[i]).Where(s => s.Cluster >= 0).ToList();
            for (int i = 1; i < kept.Count; i++)
                (kept[i - 1].After, kept[i].Before) = (kept[i], kept[i - 1]);
        }

        int merges = Merge(clusters, o, c, Trim);

        // The steps along each run, between neighbors that stayed apart.
        var steps = new List<TerraceStep>();
        foreach (var run in runs)
        {
            var kept = run.Select(i => stretches[i]).Where(s => s.Cluster >= 0).ToList();
            for (int i = 1; i < kept.Count; i++)
            {
                var (before, after) = (kept[i - 1], kept[i]);
                int g0 = Root(clusters, before.Cluster), g1 = Root(clusters, after.Cluster);
                if (g0 == g1)
                    continue;
                float rise = clusters[g1].Height - clusters[g0].Height;
                var line = new Polyline(roads.Edges[before.Edge].Points);
                var (at, _) = line.At(before.To, before.Side);
                bool steep = MathF.Abs(rise) > o.StepMax + 0.01f;
                if (steep)
                {
                    clusters[g0].Steep = true;
                    clusters[g1].Steep = true;
                }

                steps.Add(new TerraceStep(at, rise, after.Mid - before.Mid, steep));
            }
        }

        var terraces = new List<Terrace>();
        foreach (var g in clusters.Where(g => g.Alive))
        {
            var members = g.Members;
            float low = members.Min(s => s.Low), high = members.Max(s => s.High);
            // Marked where a limit could not be kept: a doorstep, the ground, or a step to a neighbor.
            bool steep = g.Steep || g.Height < high - o.Doorstep - 0.01f || g.Height > low + o.Doorstep + 0.01f
                || g.Height < g.Ground - o.MaxOffset - 0.01f || g.Height > MathF.Max(g.Ground + o.MaxOffset, g.Floor) + 0.01f;
            // Each union along the way may add a sliver of clipping error past the block.
            g.Reach = g.Reach.Intersect(blocks[g.Block]);
            g.Level = g.Level.Intersect(g.Reach);
            terraces.Add(new Terrace(terraces.Count, TerraceKind.Block, g.Block.ToString(), g.Level, g.Reach, g.Height,
                members.Select(s => new TerraceFront(s.Edge, s.Side, s.From, s.To)).ToList(), low, high, g.Ground, g.Floor, steep));
        }

        if (o.Landmarks)
        {
            foreach (var landmark in c.Get(Keys.Landmarks).Landmarks.Where(l => l.Kind != LandmarkKind.Gallows))
            {
                var heights = landmark.Cells.Select(k => terrain.HeightAt(landmark.Grid.Center(k))).OrderBy(h => h).ToList();
                float share = landmark.Kind == LandmarkKind.Castle ? 0.75f : 0.5f;
                float height = heights[Math.Min(heights.Count - 1, (int)(heights.Count * share))];
                // The town hall stands partly in the market; the market's paving keeps its own plane.
                var level = landmark.Plot.Subtract(roads.Footprint);
                terraces.Add(new Terrace(terraces.Count, TerraceKind.Landmark, landmark.Id, level, level, height,
                    [], float.NaN, float.NaN, height, float.NegativeInfinity, false));
            }
        }

        c.Set(Keys.Terraces, new TerraceSet(terraces, steps, o.StepMax, o.Doorstep, o.MaxOffset, merges));

        Stretch Make(RoadEdge edge, int side, Polyline line, float[] street, float[] ground, int i, int j, float height,
            bool steep)
        {
            var (probe, inward) = line.At((i + j) / 2f, side);
            probe += inward * (edge.Width / 2 + 1.5f);
            var use = land.PlotUse(probe, interior.Contains(probe));
            var river = terrain.NearestRiver(probe, 200);
            float floor = float.IsFinite(river.Distance) ? river.WaterHeight + 1 : float.NegativeInfinity;
            float low = float.MaxValue, high = float.MinValue, sum = 0;
            for (int k = i; k < j; k++)
            {
                low = MathF.Min(low, street[k]);
                high = MathF.Max(high, street[k]);
                sum += ground[k];
            }

            return new Stretch
            {
                Edge = edge.Id,
                Side = side,
                From = i,
                To = j,
                Mid = MidOf(street, i, j),
                Low = low,
                High = high,
                Ground = sum / (j - i),
                Floor = floor,
                Height = MathF.Max(height, floor),
                Trade = LandUses.Trades.Contains(use),
                Steep = steep,
            };
        }

        // Splits stations [k0, k1) beside one block into stretches; heights in the street's order.
        List<Stretch> Split(RoadEdge edge, int side, Polyline line, float[] street, float[] ground, int k0, int k1)
        {
            int n = k1 - k0;
            int min = Math.Max(1, (int)MathF.Ceiling(o.MinLength));
            var result = new List<Stretch>();
            if (n <= min)
            {
                float h = Hold(MidOf(street, k0, k1), Mean(ground, k0, k1));
                result.Add(Make(edge, side, line, street, ground, k0, k1, h, Violates(street, k0, k1, h)));
            }
            else
            {
                var s1 = new double[n + 1];
                var s2 = new double[n + 1];
                var g1 = new double[n + 1];
                for (int k = 0; k < n; k++)
                {
                    s1[k + 1] = s1[k] + street[k0 + k];
                    s2[k + 1] = s2[k] + (double)street[k0 + k] * street[k0 + k];
                    g1[k + 1] = g1[k] + ground[k0 + k];
                }

                var best = new double[n + 1];
                var from = new int[n + 1];
                Array.Fill(best, double.PositiveInfinity);
                best[0] = 0;
                for (int j = min; j <= n; j++)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int k = j - 1; k >= j - min; k--)
                    {
                        lo = MathF.Min(lo, street[k0 + k]);
                        hi = MathF.Max(hi, street[k0 + k]);
                    }

                    for (int i = j - min; i >= 0; i--)
                    {
                        if (i < j - min)
                        {
                            lo = MathF.Min(lo, street[k0 + i]);
                            hi = MathF.Max(hi, street[k0 + i]);
                        }

                        // A stretch may not leave a piece shorter than the minimum before it, unless it starts the run.
                        if (i > 0 && i < min || double.IsPositiveInfinity(best[i]))
                            continue;
                        int len = j - i;
                        float h = Hold(MidOf(street, k0 + i, k0 + j), (float)((g1[j] - g1[i]) / len));
                        double squares = len * (double)h * h - 2 * h * (s1[j] - s1[i]) + (s2[j] - s2[i]);
                        float off = MathF.Max(0, MathF.Max(hi - h, h - lo) - o.Doorstep);
                        double cost = best[i] + squares + o.StepCost + 1000.0 * off * off * len;
                        if (cost < best[j])
                            (best[j], from[j]) = (cost, i);
                    }
                }

                var cuts = new List<int>();
                for (int j = n; j > 0; j = from[j])
                    cuts.Add(j);
                cuts.Add(0);
                cuts.Reverse();
                for (int t = 1; t < cuts.Count; t++)
                {
                    int i = k0 + cuts[t - 1], j = k0 + cuts[t];
                    float h = Hold(MidOf(street, i, j), Mean(ground, i, j));
                    result.Add(Make(edge, side, line, street, ground, i, j, h, false));
                }
            }

            Order(result);
            foreach (var s in result)
                s.Steep |= Violates(street, (int)s.From, (int)s.To, s.Height);
            return result;
        }

        float Hold(float street, float ground) => Math.Clamp(street, ground - o.MaxOffset, ground + o.MaxOffset);

        bool Violates(float[] street, int i, int j, float h)
        {
            for (int k = i; k < j; k++)
            {
                if (MathF.Abs(street[k] - h) > o.Doorstep + 0.01f)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Pools neighbors whose heights run against the street's slope (a step down where the street climbs).</summary>
    private static void Order(List<Stretch> run)
    {
        for (int round = 0; round < run.Count * 2; round++)
        {
            bool changed = false;
            for (int i = 1; i < run.Count; i++)
            {
                var (a, b) = (run[i - 1], run[i]);
                float street = b.Mid - a.Mid;
                if (MathF.Abs(street) < 0.05f || (b.Height - a.Height) * MathF.Sign(street) >= -0.01f)
                    continue;
                float h = (a.Height * (a.To - a.From) + b.Height * (b.To - b.From)) / (b.To - a.From);
                a.Height = MathF.Max(h, a.Floor);
                b.Height = MathF.Max(h, b.Floor);
                changed = true;
            }

            if (!changed)
                break;
        }
    }

    private sealed class Cluster(int block, Region level, Region reach, List<Stretch> members)
    {
        public int Block { get; } = block;
        /// <summary>The level part as cut, before it keeps off the water, the rim and ground too far off.</summary>
        public Region Full { get; set; } = level;
        public Region Level { get; set; } = level;
        public Region Reach { get; set; } = reach;
        public List<Stretch> Members { get; } = members;
        public int Parent { get; set; } = -1;
        public bool Steep { get; set; } = members.Any(m => m.Steep);
        public bool Alive => Parent < 0;
        public float Length => Members.Sum(m => m.To - m.From);
        public float Height { get; set; } = members[0].Height;
        public float Ground => Members.Sum(m => m.Ground * (m.To - m.From)) / Length;
        public float Floor => Members.Max(m => m.Floor);
    }

    private static int Root(List<Cluster> clusters, int g)
    {
        while (clusters[g].Parent >= 0)
            g = clusters[g].Parent;
        return g;
    }

    /// <summary>
    /// Joins neighbors in one block, the closest in height first, while the joined height keeps every doorstep and the
    /// ground limit; then joins pieces too small to stand alone to the neighbor they border most. Returns the joins.
    /// </summary>
    private static int Merge(List<Cluster> clusters, TerracesOptions o, NodeContext c, Action<Cluster> trim)
    {
        bool Touch(Cluster a, Cluster b) => a.Block == b.Block && a.Level.Bounds.Grow(1).Intersects(b.Level.Bounds)
            && a.Level.Offset(0.3f).Intersect(b.Level).Area > 0.5;

        // How far a stretch's doorstep and ground are from their limits at a height (0 within them).
        float Door(Stretch m, float h) => MathF.Max(0, MathF.Max(m.High - h, h - m.Low) - o.Doorstep);
        float Earth(Stretch m, float h) =>
            MathF.Max(0, MathF.Max(m.Ground - o.MaxOffset - h, h - MathF.Max(m.Ground + o.MaxOffset, m.Floor)));

        // The heights a member allows: its doorstep and ground within their limits, or, where it is already past one,
        // no farther past it than it is now.
        (float Low, float High) Allows(Stretch m, float now)
        {
            float door = Door(m, now) + 0.01f, earth = Earth(m, now) + 0.01f;
            return (MathF.Max(m.High - o.Doorstep - door, m.Ground - o.MaxOffset - earth),
                MathF.Min(m.Low + o.Doorstep + door, MathF.Max(m.Ground + o.MaxOffset, m.Floor) + earth));
        }

        // Whether a, joined with b (if given), at height h keeps every step to its neighbors along the streets with the
        // street's slope (as the steps are measured: a step against a slope of more than 0.1 m is one of more than 0.1 m).
        bool Ordered(Cluster a, Cluster? b, float h)
        {
            int ra = clusters.IndexOf(a), rb = b == null ? -1 : clusters.IndexOf(b);
            foreach (var m in b == null ? a.Members : a.Members.Concat(b.Members))
            {
                foreach (var (n, after) in new[] { (m.Before, false), (m.After, true) })
                {
                    if (n == null)
                        continue;
                    int rn = Root(clusters, n.Cluster);
                    if (rn == ra || rn == rb)
                        continue;
                    float street = after ? n.Mid - m.Mid : m.Mid - n.Mid;
                    float rise = after ? clusters[rn].Height - h : h - clusters[rn].Height;
                    if (MathF.Abs(street) > 0.1f && rise * MathF.Sign(street) < -0.05f)
                        return false;
                }
            }

            return true;
        }

        // A height for both, or null: the heights every member of either allows have one in common (above both floors);
        // of those, the nearest to their mean by front length.
        float? Joined(Cluster a, Cluster b)
        {
            float low = MathF.Max(a.Floor, b.Floor), high = float.PositiveInfinity;
            foreach (var (g, m) in a.Members.Select(m => (a, m)).Concat(b.Members.Select(m => (b, m))))
            {
                var (l, u) = Allows(m, g.Height);
                (low, high) = (MathF.Max(low, l), MathF.Min(high, u));
            }

            if (low > high)
                return null;
            float h = Math.Clamp((a.Height * a.Length + b.Height * b.Length) / (a.Length + b.Length), low, high);
            return Ordered(a, b, h) ? h : null;
        }

        void Absorb(int into, int from, float height)
        {
            var (a, b) = (clusters[into], clusters[from]);
            a.Full = a.Full.Union(b.Full);
            a.Level = a.Full;
            a.Reach = a.Reach.Union(b.Reach);
            a.Members.AddRange(b.Members);
            a.Steep |= b.Steep;
            a.Height = height;
            b.Parent = into;
        }

        int merges = 0;
        var pairs = new List<(int A, int B)>();
        for (int i = 0; i < clusters.Count; i++)
        {
            for (int j = i + 1; j < clusters.Count; j++)
            {
                if (Touch(clusters[i], clusters[j]))
                    pairs.Add((i, j));
            }
        }

        while (true)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            (int A, int B, float H, float Gap)? best = null;
            var seen = new HashSet<(int, int)>();
            foreach (var (i, j) in pairs)
            {
                int a = Root(clusters, i), b = Root(clusters, j);
                if (a == b || !seen.Add((Math.Min(a, b), Math.Max(a, b))))
                    continue;
                float gap = MathF.Abs(clusters[a].Height - clusters[b].Height);
                if (gap >= o.MergeStep || best is { } current && current.Gap <= gap)
                    continue;
                if (Joined(clusters[a], clusters[b]) is { } h)
                    best = (a, b, h, gap);
            }

            if (best is not { } pick)
                break;
            Absorb(pick.A, pick.B, pick.H);
            merges++;
        }

        // What is left of each level part off the water, the rim and ground too far off decides what is too small.
        foreach (var g in clusters.Where(g => g.Alive))
            trim(g);

        // Pieces too small to stand alone, by area (a thin strip, a corner's cut, a part mostly too far off its ground)
        // or by front (a block's short side between two junctions), go to a neighbor in the block, the one they share
        // most border with first, at its height, if the step is within the limit and their ground is within reach of
        // it. Their own front's doorstep may get worse: their houses front the longer street, or there is no room for one.
        foreach (int i in Enumerable.Range(0, clusters.Count).Where(i => clusters[i].Alive).OrderBy(i => clusters[i].Level.Area))
        {
            var small = clusters[i];
            if (!small.Alive || small.Level.Area >= MinArea && small.Length >= o.MinLength)
                continue;
            var around = small.Full.Offset(0.3f);
            var neighbors = pairs.Select(p => (A: Root(clusters, p.A), B: Root(clusters, p.B)))
                .Where(p => p.A == i ^ p.B == i).Select(p => p.A == i ? p.B : p.A).Distinct()
                .Select(j => (j, Border: around.Intersect(clusters[j].Full).Area)).Where(x => x.Border > 0.5)
                .OrderByDescending(x => x.Border).ToList();
            foreach (var (j, _) in neighbors)
            {
                float h = clusters[j].Height;
                if (MathF.Abs(h - small.Height) > o.StepMax || h < small.Floor - 0.01f || !Ordered(clusters[j], small, h)
                    || small.Members.Any(m => MathF.Abs(h - m.Ground) > MaxEarthwork && Earth(m, h) > Earth(m, small.Height) + 0.01f))
                    continue;
                Absorb(j, i, h);
                trim(clusters[j]);
                merges++;
                break;
            }
        }

        return merges;
    }

    private static float MidOf(float[] street, int i, int j)
    {
        int a = (i + j - 1) / 2, b = (i + j) / 2;
        return (street[a] + street[b]) / 2;
    }

    private static float Mean(float[] values, int i, int j)
    {
        float sum = 0;
        for (int k = i; k < j; k++)
            sum += values[k];
        return sum / (j - i);
    }

    /// <summary>A road's center line measured along its length.</summary>
    private sealed class Polyline
    {
        private readonly Vector2[] _points;
        private readonly float[] _at;

        public Polyline(Vector2[] points)
        {
            _points = points;
            _at = new float[points.Length];
            for (int i = 1; i < points.Length; i++)
                _at[i] = _at[i - 1] + points[i].DistanceTo(points[i - 1]);
        }

        public float Length => _at[^1];

        /// <summary>The point <paramref name="s"/> along the line and the direction away from it on the given side.</summary>
        public (Vector2 Point, Vector2 Inward) At(float s, int side)
        {
            int i = Segment(s);
            var along = (_points[i + 1] - _points[i]).Normalized();
            float t = (s - _at[i]) / MathF.Max(1e-4f, _at[i + 1] - _at[i]);
            return (_points[i].Lerp(_points[i + 1], Math.Clamp(t, 0, 1)), along.Orthogonal() * side);
        }

        /// <summary>
        /// The stretch from <paramref name="from"/> to <paramref name="to"/>, one piece per segment of the line, each moved
        /// <paramref name="offset"/> to the given side: its ends and the direction away from the line.
        /// </summary>
        public IEnumerable<(Vector2 A, Vector2 B, Vector2 Inward)> Pieces(float from, float to, int side, float offset)
        {
            for (int i = 0; i < _points.Length - 1; i++)
            {
                float a = MathF.Max(from, _at[i]), b = MathF.Min(to, _at[i + 1]);
                if (b - a <= 0)
                    continue;
                var along = (_points[i + 1] - _points[i]) / MathF.Max(1e-4f, _at[i + 1] - _at[i]);
                var inward = along.Orthogonal() * side;
                yield return (_points[i] + along * (a - _at[i]) + inward * offset, _points[i] + along * (b - _at[i]) + inward * offset,
                    inward);
            }
        }

        private int Segment(float s)
        {
            for (int i = 0; i < _points.Length - 2; i++)
            {
                if (s < _at[i + 1])
                    return i;
            }

            return _points.Length - 2;
        }
    }
}
