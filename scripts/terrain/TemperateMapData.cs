using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PcgCitySandbox.Terrain;

public readonly record struct RiverPoint(Vector2 Position, float WaterHeight, float HalfWidth);
public readonly record struct TreePlacement(Vector3 Position, float Scale, float Rotation, int Species, float Tint);
public readonly record struct TerrainSample(float Height, float SlopeDegrees, bool IsWater,
    float Forest, float Suitability, float RiverDistance);

/// <summary>
/// Generated temperate map: height field, river, forest cover and tree placements. Scene-independent;
/// world X/Z are horizontal and one unit is one meter.
/// <para>
/// Two grids: the <b>height grid</b> (about 1 m, <see cref="HeightSide"/>) is the single source of ground height
/// for everything, including <see cref="HeightAt"/> and the rendered ground. The coarser <b>analysis grid</b>
/// (<see cref="Side"/>, about 5 m) holds slope, forest, suitability and river distance for planning and coloring.
/// </para>
/// </summary>
public sealed class TemperateMapData : ITerrain
{
    /// <summary>Generates a map from <paramref name="settings"/> with its seed replaced by <paramref name="seed"/>.</summary>
    public static TemperateMapData Create(MapSettings settings, int seed)
    {
        var copy = (MapSettings)settings.Duplicate();
        copy.Seed = seed;
        return new TemperateMapData(copy);
    }

    /// <summary>Small relief added on top of the landform: (wavelength m, amplitude m), largest first.</summary>
    private static readonly (float Wavelength, float Amplitude)[] DetailOctaves =
    {
        (100, 0.65f), (38, 0.28f), (15, 0.12f), (6, 0.05f), (2.5f, 0.02f),
    };

    private readonly GradientNoise _broad;
    private readonly GradientNoise _forest;
    private readonly GradientNoise _warpX, _warpZ;
    private readonly GradientNoise[] _detail;
    private readonly float[] _riverHalfWidths;

    /// <summary>Side of the square buckets that index river segments for <see cref="NearestRiver"/>.</summary>
    private const float RiverBucketSize = 16;
    private int _riverBuckets;
    private int[] _riverCandidateStart = Array.Empty<int>();
    private int[] _riverCandidates = Array.Empty<int>();

    public TemperateMapData(MapSettings settings)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        settings.Validate();
        // Snapshot settings so callers cannot invalidate generated arrays by editing the Resource.
        Settings = (MapSettings)settings.Duplicate();
        int seed = Settings.Seed;
        _broad = new GradientNoise(seed, 1050);
        _forest = new GradientNoise(unchecked(seed + 981), 260, 0.9f);
        _warpX = new GradientNoise(unchecked(seed + 311), 60, 0.3f);
        _warpZ = new GradientNoise(unchecked(seed + 313), 60, 1.9f);
        _detail = new GradientNoise[DetailOctaves.Length];
        for (int i = 0; i < _detail.Length; i++)
            _detail[i] = new GradientNoise(unchecked(seed + 137 + i * 7919), DetailOctaves[i].Wavelength, 0.7f + i * 1.3f);

        int halfIndex = Math.Max(1, (int)MathF.Round(Half / Settings.HeightSpacingMeters));
        HeightSide = 2 * halfIndex + 1;
        HeightSpacing = Half / halfIndex;
        HeightGrid = new float[HeightSide * HeightSide];

        int count = Side * Side;
        Heights = new float[count];
        Slopes = new float[count];
        Forest = new float[count];
        Suitability = new float[count];
        RiverDistances = new float[count];
        Water = new bool[count];
        _riverHalfWidths = new float[count];

        GenerateRiver();
        GenerateHeights();
        GenerateAnalysis();
        GenerateTrees();
        GenerationSeconds = timer.Elapsed.TotalSeconds;
    }

    public MapSettings Settings { get; }
    public float Half => Settings.SizeMeters * 0.5f;
    public double GenerationSeconds { get; }

    // Height grid: the one source of ground height.
    public int HeightSide { get; }
    public float HeightSpacing { get; }
    public float[] HeightGrid { get; }

    // Analysis grid.
    public int Side => Settings.GridSegments + 1;
    public float Spacing => Settings.SizeMeters / Settings.GridSegments;

    /// <summary>Ground height at each analysis grid vertex (sampled from the height grid).</summary>
    public float[] Heights { get; }
    public float[] Slopes { get; }
    public float[] Forest { get; }
    public float[] Suitability { get; }
    public float[] RiverDistances { get; }
    public bool[] Water { get; }

    public List<RiverPoint> River { get; } = new();
    public List<TreePlacement> Trees { get; } = new();
    public float MinHeight { get; private set; } = float.MaxValue;
    public float MaxHeight { get; private set; } = float.MinValue;
    public float WaterFraction { get; private set; }
    public float ForestFraction { get; private set; }
    public float OpenGentleFraction { get; private set; }
    public float RiverLength { get; private set; }

    public int Index(int x, int z) => z * Side + x;
    public Vector2 GridPosition(int x, int z) => new(-Half + x * Spacing, -Half + z * Spacing);
    public int HeightIndex(int x, int z) => z * HeightSide + x;
    public Vector2 HeightPosition(int x, int z) => new(-Half + x * HeightSpacing, -Half + z * HeightSpacing);
    public bool Contains(Vector2 p) => Mathf.Abs(p.X) <= Half && Mathf.Abs(p.Y) <= Half;

    /// <summary>Bilinear interpolation of the height grid, clamped to the map.</summary>
    public float HeightAt(Vector2 p)
    {
        float gx = Mathf.Clamp((p.X + Half) / HeightSpacing, 0, HeightSide - 1);
        float gz = Mathf.Clamp((p.Y + Half) / HeightSpacing, 0, HeightSide - 1);
        int x = Math.Min((int)gx, HeightSide - 2), z = Math.Min((int)gz, HeightSide - 2);
        float u = gx - x, v = gz - z;
        int k = HeightIndex(x, z);
        float top = HeightGrid[k] + (HeightGrid[k + 1] - HeightGrid[k]) * u;
        float bottom = HeightGrid[k + HeightSide] + (HeightGrid[k + HeightSide + 1] - HeightGrid[k + HeightSide]) * u;
        return top + (bottom - top) * v;
    }

    public TerrainSample Sample(Vector2 p)
    {
        if (!Contains(p))
            throw new ArgumentOutOfRangeException(nameof(p), "位置位于地图范围外。");
        var river = NearestRiver(p);
        float h = HeightAt(p);
        bool water = h < river.WaterHeight && river.Distance < river.HalfWidth + Spacing * 2;
        return new TerrainSample(h, Interpolate(Slopes, p), water, Interpolate(Forest, p),
            water ? 0 : Interpolate(Suitability, p), river.Distance);
    }

    /// <summary>
    /// Nearest point on the river centerline. Inside the map, only the segments listed for the point's bucket are
    /// tested; the list is built so that it always contains the true nearest segment (see
    /// <see cref="IndexRiver"/>). With <paramref name="maxDistance"/>, farther segments are ignored; if none is
    /// closer, the result is an infinite distance with zero water height and width.
    /// </summary>
    public (float Distance, float WaterHeight, float HalfWidth) NearestRiver(Vector2 p,
        float maxDistance = float.PositiveInfinity)
    {
        if (!Contains(p))
            return NearestRiverExhaustive(p, maxDistance);
        int bx = Math.Min((int)((p.X + Half) / RiverBucketSize), _riverBuckets - 1);
        int bz = Math.Min((int)((p.Y + Half) / RiverBucketSize), _riverBuckets - 1);
        int bucket = bz * _riverBuckets + bx;
        float best = maxDistance * maxDistance, height = 0, width = 0;
        for (int c = _riverCandidateStart[bucket]; c < _riverCandidateStart[bucket + 1]; c++)
            TestSegment(_riverCandidates[c], p, ref best, ref height, ref width);
        return width > 0 ? (Mathf.Sqrt(best), height, width) : (float.PositiveInfinity, 0, 0);
    }

    /// <summary>Reference implementation of <see cref="NearestRiver"/> that tests every segment.</summary>
    internal (float Distance, float WaterHeight, float HalfWidth) NearestRiverExhaustive(Vector2 p,
        float maxDistance = float.PositiveInfinity)
    {
        float best = maxDistance * maxDistance, height = 0, width = 0;
        for (int i = 0; i < River.Count - 1; i++)
            TestSegment(i, p, ref best, ref height, ref width);
        return width > 0 ? (Mathf.Sqrt(best), height, width) : (float.PositiveInfinity, 0, 0);
    }

    /// <summary>Keeps segment <paramref name="i"/> if it is strictly closer than <paramref name="best"/> (squared).</summary>
    private void TestSegment(int i, Vector2 p, ref float best, ref float height, ref float width)
    {
        var a = River[i];
        var b = River[i + 1];
        Vector2 ab = b.Position - a.Position;
        float t = Mathf.Clamp((p - a.Position).Dot(ab) / ab.LengthSquared(), 0, 1);
        float d2 = p.DistanceSquaredTo(a.Position + ab * t);
        if (d2 >= best)
            return;
        best = d2;
        height = Mathf.Lerp(a.WaterHeight, b.WaterHeight, t);
        width = Mathf.Lerp(a.HalfWidth, b.HalfWidth, t);
    }

    /// <summary>
    /// Lists, for each bucket, the segments that can be nearest to some point in it. A point is within half a
    /// bucket diagonal (r) of the bucket center c, so its nearest segment is at most d_min + r away, where d_min is
    /// the smallest segment distance from c; a segment at distance d from c is at least d - r from the point.
    /// Hence segments with d &gt; d_min + 2r can never be nearest and are left out.
    /// </summary>
    private void IndexRiver()
    {
        _riverBuckets = (int)MathF.Ceiling(Settings.SizeMeters / RiverBucketSize);
        int buckets = _riverBuckets * _riverBuckets;
        float slack = RiverBucketSize * MathF.Sqrt(2);
        var lists = new int[buckets][];
        Parallel.For(0, buckets, bucket =>
        {
            var center = new Vector2(-Half + (bucket % _riverBuckets + 0.5f) * RiverBucketSize,
                -Half + (bucket / _riverBuckets + 0.5f) * RiverBucketSize);
            var distances = new float[River.Count - 1];
            float nearest = float.MaxValue;
            for (int i = 0; i < distances.Length; i++)
            {
                float best = float.MaxValue, h = 0, w = 0;
                TestSegment(i, center, ref best, ref h, ref w);
                distances[i] = MathF.Sqrt(best);
                nearest = MathF.Min(nearest, distances[i]);
            }

            var list = new List<int>();
            for (int i = 0; i < distances.Length; i++)
            {
                if (distances[i] <= nearest + slack + 1e-3f)
                    list.Add(i);
            }

            lists[bucket] = list.ToArray();
        });

        _riverCandidateStart = new int[buckets + 1];
        for (int b = 0; b < buckets; b++)
            _riverCandidateStart[b + 1] = _riverCandidateStart[b] + lists[b].Length;
        _riverCandidates = new int[_riverCandidateStart[buckets]];
        for (int b = 0; b < buckets; b++)
            lists[b].CopyTo(_riverCandidates, _riverCandidateStart[b]);
    }

    private void GenerateRiver()
    {
        float phase = (Settings.Seed & 65535) * 0.000037f;
        int sections = 256;
        for (int i = 0; i <= sections; i++)
        {
            float t = i / (float)sections;
            float z = Mathf.Lerp(-Half, Half, t);
            float x = Settings.SizeMeters * (0.105f * Mathf.Sin(t * Mathf.Tau * 1.18f + phase)
                + 0.060f * Mathf.Sin(t * Mathf.Tau * 2.1f - phase * 0.6f));
            float width = Settings.RiverWidthMeters * 0.5f * (1 + 0.18f * Mathf.Sin(t * 11 + phase));
            River.Add(new RiverPoint(new Vector2(x, z), Mathf.Lerp(14, 6, t), width));
            if (i > 0)
                RiverLength += River[i].Position.DistanceTo(River[i - 1].Position);
        }

        IndexRiver();
    }

    private static float Smooth(float a, float b, float v) => Mathf.SmoothStep(0, 1, Mathf.Clamp((v - a) / (b - a), 0, 1));

    /// <summary>Beyond this distance from the centerline the river no longer shapes the ground.</summary>
    private const float RiverReach = 260;

    /// <summary>Ground height at any point: regional landform, river valley and channel, then small relief.</summary>
    private float GroundHeight(Vector2 p)
    {
        var river = NearestRiver(p, RiverReach);
        float broad = _broad.Sample(p);
        float swell = Mathf.Sin(p.X / 460 + 0.7f) * Mathf.Cos(p.Y / 570 - 0.3f);
        float land = 30 + Settings.ReliefMeters * (broad * 0.82f + swell * 0.35f) - p.Y / Settings.SizeMeters * 7;
        // Use a continuous regional floor. Nearest river stations can switch across a meander's
        // watershed; their water levels must not introduce steps in distant, otherwise smooth land.
        float floor = Mathf.Lerp(14, 6, (p.Y + Half) / Settings.SizeMeters) + 5;
        land = 0.5f * (land + floor + Mathf.Sqrt((land - floor) * (land - floor) + 16));
        float floodplain = Mathf.Lerp(river.WaterHeight + 2.4f, land,
            Smooth(river.HalfWidth + 18, river.HalfWidth + 155, river.Distance));
        float bed = river.WaterHeight - 0.65f - 3.2f * (1 - Mathf.Pow(Mathf.Clamp(river.Distance / river.HalfWidth, 0, 1), 2));
        float h = Mathf.Lerp(bed, floodplain, Smooth(river.HalfWidth - 1, river.HalfWidth + 14, river.Distance));
        // Small relief everywhere but the deep channel, which stays smooth under the water.
        return h + Detail(p) * Smooth(river.HalfWidth - 8, river.HalfWidth + 2, river.Distance);
    }

    /// <summary>Stacked gradient octaves; the middle octaves are domain-warped so the relief is not blobby.</summary>
    private float Detail(Vector2 p)
    {
        var warped = p + new Vector2(_warpX.Sample(p), _warpZ.Sample(p)) * 8;
        float sum = 0;
        for (int i = 0; i < _detail.Length; i++)
        {
            var q = i is 1 or 2 ? warped : p;
            sum += _detail[i].Sample(q) * DetailOctaves[i].Amplitude;
        }

        return sum;
    }

    private void GenerateHeights()
    {
        Parallel.For(0, HeightSide, z =>
        {
            for (int x = 0; x < HeightSide; x++)
                HeightGrid[HeightIndex(x, z)] = GroundHeight(HeightPosition(x, z));
        });
        foreach (float h in HeightGrid)
        {
            MinHeight = Mathf.Min(MinHeight, h);
            MaxHeight = Mathf.Max(MaxHeight, h);
        }
    }

    private void GenerateAnalysis()
    {
        Parallel.For(0, Side, z =>
        {
            for (int x = 0; x < Side; x++)
            {
                int k = Index(x, z);
                Vector2 p = GridPosition(x, z);
                var river = NearestRiver(p);
                float h = HeightAt(p);
                Heights[k] = h;
                RiverDistances[k] = river.Distance;
                _riverHalfWidths[k] = river.HalfWidth;
                Water[k] = h < river.WaterHeight;

                // Broad woodland masses plus an irregular edge; keep the central river terraces open.
                Vector2 q = p / Settings.SizeMeters;
                float patch = Gaussian(q, new Vector2(-0.30f, -0.25f), 0.18f, 0.19f)
                    + Gaussian(q, new Vector2(0.32f, 0.12f), 0.17f, 0.24f)
                    + 0.7f * Gaussian(q, new Vector2(-0.33f, 0.39f), 0.20f, 0.12f);
                float noise = _forest.Sample(p);
                float clearing = 1 - 0.8f * Gaussian(q, new Vector2(-0.10f, 0.04f), 0.16f, 0.18f);
                Forest[k] = Water[k] ? 0 : Smooth(0.24f, 0.92f, patch + noise * 0.40f) * clearing
                    * Smooth(river.HalfWidth + 10, river.HalfWidth + 38, river.Distance) * Settings.ForestDensity;
            }
        });

        int wet = 0, wooded = 0, open = 0;
        for (int z = 0; z < Side; z++)
            for (int x = 0; x < Side; x++)
            {
                int k = Index(x, z);
                // Slope over the analysis spacing: the planning-scale gradient, not the meter-scale relief.
                int xl = Math.Max(0, x - 1), xr = Math.Min(Side - 1, x + 1);
                int zu = Math.Max(0, z - 1), zd = Math.Min(Side - 1, z + 1);
                float dx = (Heights[Index(xr, z)] - Heights[Index(xl, z)]) / ((xr - xl) * Spacing);
                float dz = (Heights[Index(x, zd)] - Heights[Index(x, zu)]) / ((zd - zu) * Spacing);
                Slopes[k] = Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz)));
                float bankSetback = Smooth(_riverHalfWidths[k] + 12, _riverHalfWidths[k] + 55, RiverDistances[k]);
                Suitability[k] = Water[k] ? 0 : (1 - Smooth(3, 13, Slopes[k])) * (1 - Forest[k] * 0.72f) * bankSetback;
                if (Water[k])
                    wet++;
                if (Forest[k] > 0.24f)
                    wooded++;
                if (!Water[k] && Slopes[k] < 5 && Forest[k] < 0.24f && Suitability[k] > 0.55f)
                    open++;
            }

        WaterFraction = wet / (float)Heights.Length;
        ForestFraction = wooded / (float)Heights.Length;
        OpenGentleFraction = open / (float)Heights.Length;
    }

    private static float Gaussian(Vector2 p, Vector2 center, float sx, float sz)
    {
        Vector2 d = p - center;
        return Mathf.Exp(-0.5f * (d.X * d.X / (sx * sx) + d.Y * d.Y / (sz * sz)));
    }

    // Analysis fields interpolate smoothly between grid samples.
    private float Interpolate(float[] data, Vector2 p)
    {
        float gx = Mathf.Clamp((p.X + Half) / Spacing, 0, Side - 1);
        float gz = Mathf.Clamp((p.Y + Half) / Spacing, 0, Side - 1);
        int x = Math.Min((int)gx, Side - 2), z = Math.Min((int)gz, Side - 2);
        return Mathf.Lerp(Mathf.Lerp(data[Index(x, z)], data[Index(x + 1, z)], gx - x),
            Mathf.Lerp(data[Index(x, z + 1)], data[Index(x + 1, z + 1)], gx - x), gz - z);
    }

    private void GenerateTrees()
    {
        var rng = new RandomNumberGenerator { Seed = unchecked((ulong)(long)Settings.Seed) ^ 0xA531UL };
        float step = Settings.TreeSpacingMeters;
        for (float z = -Half + 12; z < Half - 12; z += step)
            for (float x = -Half + 12; x < Half - 12; x += step)
            {
                Vector2 p = new(x + rng.RandfRange(-step * 0.40f, step * 0.40f), z + rng.RandfRange(-step * 0.40f, step * 0.40f));
                if (Mathf.Abs(p.X) > Half - 10 || Mathf.Abs(p.Y) > Half - 10)
                    continue;
                float coverage = Interpolate(Forest, p);
                if (coverage < 0.10f || rng.Randf() > coverage * 0.94f)
                    continue;
                var sample = Sample(p);
                var river = NearestRiver(p);
                if (sample.IsWater || sample.SlopeDegrees > 18 || river.Distance < river.HalfWidth + 18)
                    continue;
                int species = rng.Randf() < 0.15f ? 2 : (rng.Randf() < 0.5f ? 0 : 1);
                Trees.Add(new TreePlacement(new Vector3(p.X, sample.Height - 0.12f, p.Y),
                    rng.RandfRange(0.8f, 1.4f), rng.RandfRange(0, Mathf.Tau), species, rng.RandfRange(0.86f, 1.12f)));
            }

        rng.Dispose();
    }
}
