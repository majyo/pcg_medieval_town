using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class PlatformsOptions
{
    [Param("挡土墙高差 m", 0.2, 2, 0.1,
        Hint = "相邻两块台地、或台地与它外面整平后的地面之间高差超过它，就砌一道挡土墙")]
    public float WallStep { get; set; } = 0.5f;
}

/// <summary>
/// Makes the terraces part of the ground. Each terrace's level part becomes a level platform at its height, and the
/// ground is solved again with the roads (<see cref="GradeBake"/>): the back land and the strips between terraces ease
/// between them. Then, on the solved ground, a retaining wall is recorded wherever a terrace's edge still stands more
/// than a step above or below what lies beyond it.
/// </summary>
public sealed class Platforms : CityNode<PlatformsOptions>
{
    private const float Bucket = 24;

    /// <summary>How far past a platform's edge the ground beyond it is read.</summary>
    private const float Past = 1;

    public override string Group => NodeGroups.Grading;
    public override string Title => "平台";
    public override string Description =>
        "把台地整进地面：每块台地的水平部分成为一块平台，与道路一起重新求解地面，后院和台地之间的空地顺势放坡；" +
        "在求解后的地面上，台地边上高差仍超过一级的地方砌挡土墙。";
    public override IReadOnlyList<IKey> Reads => [Keys.Terraces, Keys.Shore];
    public override IReadOnlyList<IKey> Edits => [Keys.Grading];

    protected override void Run(NodeContext c, PlatformsOptions o)
    {
        var roads = c.Get(Keys.Grading);
        var shore = c.Get(Keys.Shore);
        var terrain = c.Terrain;
        var platforms = roads.Platforms.ToList();
        foreach (var t in c.Get(Keys.Terraces).Terraces)
        {
            c.Cancellation.ThrowIfCancellationRequested();
            var kind = t.Kind == TerraceKind.Landmark ? PlatformKind.Landmark : PlatformKind.Terrace;
            platforms.Add(new GradePlatform(kind, Name(t), t.Level, GradePlane.Level(t.Height)));
        }

        bool Water(Vector2 p) => shore.Nearest(p) < 0;
        var patch = GradeBake.Bake(terrain, roads.Corridors, platforms, Water, c.Cancellation);
        var solved = new GradePlan(terrain, roads.Corridors, platforms, [], patch, roads.EarthworkLimit);
        var walls = Walls(platforms, solved.HeightAt, o.WallStep);
        c.Set(Keys.Grading, new GradePlan(terrain, roads.Corridors, platforms, walls, patch, roads.EarthworkLimit));
    }

    /// <summary>What a terrace's platform is called in <see cref="GradePlatform.Of"/>: a landmark's id, or the terrace's number.</summary>
    public static string Name(Terrace t) => t.Kind == TerraceKind.Landmark ? t.Of : $"terrace_{t.Id}";

    /// <summary>
    /// A wall along every stretch of a plot or landmark platform's edge where the ground on the other side — another
    /// such platform, or the ground a little way out (the graded street in front, the terrain behind) — stands more
    /// than <paramref name="step"/> higher or lower. A
    /// stretch between two platforms is recorded once, by the earlier one.
    /// </summary>
    private static List<RetainingWall> Walls(List<GradePlatform> platforms, Func<Vector2, float> ground, float step)
    {
        var raised = platforms.Select((p, i) => (Platform: p, Index: i))
            .Where(x => x.Platform.Kind is PlatformKind.Terrace or PlatformKind.Landmark).ToList();
        var paved = platforms.Where(p => p.Kind is PlatformKind.Market or PlatformKind.Square).ToList();
        var near = new Dictionary<(int, int), List<int>>();
        foreach (var (platform, index) in raised)
        {
            var box = platform.Area.Bounds.Grow(1);
            for (int z = (int)MathF.Floor(box.Position.Y / Bucket); z <= (int)MathF.Floor(box.End.Y / Bucket); z++)
            {
                for (int x = (int)MathF.Floor(box.Position.X / Bucket); x <= (int)MathF.Floor(box.End.X / Bucket); x++)
                {
                    if (!near.TryGetValue((x, z), out var list))
                        near[(x, z)] = list = new List<int>();
                    list.Add(index);
                }
            }
        }

        var walls = new List<RetainingWall>();
        foreach (var (platform, index) in raised)
        {
            float own = platform.Plane.Height;
            foreach (var ring in platform.Area.Rings)
            {
                var run = new List<Vector2>();
                float low = 0, high = 0;
                void Flush()
                {
                    if (run.Count >= 2)
                        walls.Add(new RetainingWall(run.ToArray(), low, high));
                    run = new List<Vector2>();
                }

                for (int s = 0; s < ring.Length; s++)
                {
                    var a = ring[s];
                    var b = ring[(s + 1) % ring.Length];
                    float length = a.DistanceTo(b);
                    if (length < 0.2f)
                        continue;
                    // Outward of a ring is either side; try the one that leaves the platform.
                    var normal = (b - a).Orthogonal().Normalized();
                    var middle = (a + b) / 2;
                    if (platform.Area.Contains(middle + normal * Past))
                        normal = -normal;
                    int pieces = Math.Max(1, (int)MathF.Ceiling(length / 2));
                    for (int t = 0; t < pieces; t++)
                    {
                        var p = a.Lerp(b, t / (float)pieces);
                        var q = a.Lerp(b, (t + 1) / (float)pieces);
                        var beyond = (p + q) / 2 + normal * Past;
                        float? other = Beyond(beyond, index);
                        if (other is { } h && MathF.Abs(h - own) > step)
                        {
                            float l = MathF.Min(h, own), u = MathF.Max(h, own);
                            if (run.Count > 0 && (MathF.Abs(l - low) > 0.05f || MathF.Abs(u - high) > 0.05f))
                                Flush();
                            if (run.Count == 0)
                            {
                                run.Add(p);
                                (low, high) = (l, u);
                            }

                            run.Add(q);
                        }
                        else
                        {
                            Flush();
                        }
                    }
                }

                Flush();
            }
        }

        return walls;

        // The height of the ground just past a platform's edge, or null where no wall belongs: paving (a road, the
        // market — the house fronts it), or a neighbor that records the shared stretch itself.
        float? Beyond(Vector2 p, int self)
        {
            if (paved.Any(m => m.Area.Contains(p)))
                return null;
            var key = ((int)MathF.Floor(p.X / Bucket), (int)MathF.Floor(p.Y / Bucket));
            if (near.TryGetValue(key, out var list))
            {
                foreach (int j in list)
                {
                    if (j != self && platforms[j].Area.Contains(p))
                        return j < self ? null : platforms[j].Plane.Height;
                }
            }

            return ground(p);
        }
    }
}
