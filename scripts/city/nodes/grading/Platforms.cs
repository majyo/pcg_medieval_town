using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class PlatformsOptions
{
    [Param("地块平台",
        Hint = "关掉后房屋照旧立在斜坡上")]
    public bool Plots { get; set; } = true;
    [Param("地标平台",
        Hint = "关掉后教堂、城堡、市政厅、修道院、麻风院的用地照旧贴着原地形")]
    public bool Landmarks { get; set; } = true;
    [Param("平台进深 m", 6, 30, 1,
        Hint = "每块地从临街线起整平多深（房子和它门口的地）；后院不动")]
    public float Depth { get; set; } = 16;
    [Param("平台离原地形 m", 0, 3, 0.1,
        Hint = "平台取街道的高度，但最多比这块地原来的平均高度高出或低出这么多；差出的部分是门口的台阶或半层基座")]
    public float MaxOffset { get; set; } = 1;
    [Param("挡土墙高差 m", 0.2, 2, 0.1,
        Hint = "相邻两块平台、或平台与它后面的地之间高差超过它，就砌一道挡土墙")]
    public float WallStep { get; set; } = 0.5f;
}

/// <summary>
/// Levels the ground the buildings stand on. Each plot's front, as deep as a house and its doorstep, becomes a level
/// platform at the height of its street, kept near the plot's own ground; the back yard stays as it is. A landmark's
/// plot becomes one platform at its middle height (a castle's higher, so it keeps the high ground); the gallows keeps
/// its mound. Where two platforms, or a platform and the ground behind it, differ by more than a step, a retaining
/// wall is recorded.
/// </summary>
public sealed class Platforms : CityNode<PlatformsOptions>
{
    private const float Bucket = 24;

    public override string Group => NodeGroups.Grading;
    public override string Title => "平台";
    public override string Description =>
        "把房子脚下的地整平：每块地临街的一段整成与街同高的平台（不离原地形太远），后院不动；地标用地各整成一块平台，城堡留在高处；" +
        "相邻平台高差大的地方砌挡土墙。";
    public override IReadOnlyList<IKey> Reads => [Keys.Parcels, Keys.Landmarks, Keys.Shore];
    public override IReadOnlyList<IKey> Edits => [Keys.Grading];

    protected override void Run(NodeContext c, PlatformsOptions o)
    {
        var roads = c.Get(Keys.Grading);
        var shore = c.Get(Keys.Shore);
        var terrain = c.Terrain;
        var platforms = roads.Platforms.ToList();

        if (o.Plots)
        {
            foreach (var parcel in c.Get(Keys.Parcels).Parcels)
            {
                c.Cancellation.ThrowIfCancellationRequested();
                var front = Region.Polygon(new[]
                {
                    parcel.FrontA - parcel.Inward * 0.5f, parcel.FrontB - parcel.Inward * 0.5f,
                    parcel.FrontB + parcel.Inward * o.Depth, parcel.FrontA + parcel.Inward * o.Depth,
                }).Intersect(Region.Polygon(parcel.Ring));
                if (front.Area < 4)
                    continue;

                // The street's height where the plot fronts it, held near the plot's own ground.
                var door = (parcel.FrontA + parcel.FrontB) / 2;
                float street = roads.HeightAt(door - parcel.Inward * 1.5f);
                float own = Mean(front, terrain.HeightAt);
                float height = Math.Clamp(street, own - o.MaxOffset, own + o.MaxOffset);
                // A wet trade stands on low ground by the river, but not in it.
                var river = terrain.NearestRiver(door, 200);
                if (float.IsFinite(river.Distance))
                    height = MathF.Max(height, river.WaterHeight + 1);
                platforms.Add(new GradePlatform(PlatformKind.Plot, Name(parcel), front, GradePlane.Level(height)));
            }
        }

        if (o.Landmarks)
        {
            foreach (var landmark in c.Get(Keys.Landmarks).Landmarks.Where(l => l.Kind != LandmarkKind.Gallows))
            {
                var heights = landmark.Cells.Select(k => terrain.HeightAt(landmark.Grid.Center(k))).OrderBy(h => h).ToList();
                float share = landmark.Kind == LandmarkKind.Castle ? 0.75f : 0.5f;
                float height = heights[Math.Min(heights.Count - 1, (int)(heights.Count * share))];
                platforms.Add(new GradePlatform(PlatformKind.Landmark, landmark.Id, landmark.Plot, GradePlane.Level(height)));
            }
        }

        bool Water(Vector2 p) => shore.Nearest(p) < 0;
        var patch = GradeBake.Bake(terrain, roads.Corridors, platforms, Water, c.Cancellation);
        var walls = Walls(platforms, roads.HeightAt, o.WallStep);
        c.Set(Keys.Grading, new GradePlan(terrain, roads.Corridors, platforms, walls, patch, roads.EarthworkLimit));
    }

    /// <summary>What a plot's platform is called in <see cref="GradePlatform.Of"/>.</summary>
    public static string Name(Parcel parcel) => $"parcel_{parcel.Id}";

    private static float Mean(Region area, Func<Vector2, float> ground)
    {
        var box = area.Bounds;
        float sum = 0;
        int count = 0;
        for (float z = box.Position.Y + 1; z < box.End.Y; z += 2)
        {
            for (float x = box.Position.X + 1; x < box.End.X; x += 2)
            {
                var p = new Vector2(x, z);
                if (!area.Contains(p))
                    continue;
                sum += ground(p);
                count++;
            }
        }

        return count > 0 ? sum / count : ground(box.GetCenter());
    }

    /// <summary>
    /// A wall along every stretch of a plot or landmark platform's edge where the ground on the other side — another
    /// such platform, or the ground a little way out (the graded street in front, the terrain behind) — stands more
    /// than <paramref name="step"/> higher or lower. A
    /// stretch between two platforms is recorded once, by the earlier one.
    /// </summary>
    private static List<RetainingWall> Walls(List<GradePlatform> platforms, Func<Vector2, float> ground, float step)
    {
        var raised = platforms.Select((p, i) => (Platform: p, Index: i))
            .Where(x => x.Platform.Kind is PlatformKind.Plot or PlatformKind.Landmark).ToList();
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
                    if (platform.Area.Contains(middle + normal * 0.8f))
                        normal = -normal;
                    int pieces = Math.Max(1, (int)MathF.Ceiling(length / 2));
                    for (int t = 0; t < pieces; t++)
                    {
                        var p = a.Lerp(b, t / (float)pieces);
                        var q = a.Lerp(b, (t + 1) / (float)pieces);
                        var beyond = (p + q) / 2 + normal * 0.8f;
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
