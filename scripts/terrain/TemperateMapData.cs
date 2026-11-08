using Godot;
using System;
using System.Collections.Generic;

namespace PcgCitySandbox.Terrain;

public readonly record struct RiverPoint(Vector2 Position, float WaterHeight, float HalfWidth);
public readonly record struct TreePlacement(Vector3 Position, float Scale, float Rotation, int Species, float Tint);
public readonly record struct TerrainSample(float Height, float SlopeDegrees, bool IsWater,
    float Forest, float Suitability, float RiverDistance);

/// <summary>
/// Generated temperate map: height field, river, forest cover and tree placements. Scene-independent;
/// world X/Z are horizontal and one unit is one meter.
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

    public MapSettings Settings { get; }
    public int Side => Settings.GridSegments + 1;
    public float Spacing => Settings.SizeMeters / Settings.GridSegments;
    public float Half => Settings.SizeMeters * 0.5f;
    public float[] Heights { get; }
    public float[] Slopes { get; }
    public float[] Forest { get; }
    public float[] Suitability { get; }
    public float[] RiverDistances { get; }
    public bool[] Water { get; }
    public Vector3[] Normals { get; }
    public List<RiverPoint> River { get; } = new();
    public List<TreePlacement> Trees { get; } = new();
    public float MinHeight { get; private set; } = float.MaxValue;
    public float MaxHeight { get; private set; } = float.MinValue;
    public float WaterFraction { get; private set; }
    public float ForestFraction { get; private set; }
    public float OpenGentleFraction { get; private set; }
    public float RiverLength { get; private set; }
    private readonly FastNoiseLite _broad;
    private readonly FastNoiseLite _detail;
    private readonly FastNoiseLite _forest;
    private readonly float[] _riverHalfWidths;

    public TemperateMapData(MapSettings settings)
    {
        settings.Validate();
        // Snapshot settings so callers cannot invalidate generated arrays by editing the Resource.
        Settings = (MapSettings)settings.Duplicate();
        _broad = Noise(settings.Seed, 0.00095f);
        _detail = Noise(unchecked(settings.Seed + 137), 0.010f);
        _forest = Noise(unchecked(settings.Seed + 981), 0.0038f);
        int count = Side * Side;
        Heights = new float[count];
        Slopes = new float[count];
        Forest = new float[count];
        Suitability = new float[count];
        RiverDistances = new float[count];
        Water = new bool[count];
        Normals = new Vector3[count];
        _riverHalfWidths = new float[count];
        GenerateRiver();
        GenerateGround();
        GenerateTrees();
        _broad.Dispose();
        _detail.Dispose();
        _forest.Dispose();
    }

    private static FastNoiseLite Noise(int seed, float frequency) => new()
    {
        Seed = seed,
        Frequency = frequency,
        NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
        FractalType = FastNoiseLite.FractalTypeEnum.None
    };

    public int Index(int x, int z) => z * Side + x;
    public Vector2 GridPosition(int x, int z) => new(-Half + x * Spacing, -Half + z * Spacing);
    public bool Contains(Vector2 p) => Mathf.Abs(p.X) <= Half && Mathf.Abs(p.Y) <= Half;

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
    }

    public (float Distance, float WaterHeight, float HalfWidth) NearestRiver(Vector2 p)
    {
        float best = float.MaxValue, height = 0, width = 0;
        for (int i = 0; i < River.Count - 1; i++)
        {
            var a = River[i];
            var b = River[i + 1];
            Vector2 ab = b.Position - a.Position;
            float t = Mathf.Clamp((p - a.Position).Dot(ab) / ab.LengthSquared(), 0, 1);
            float d2 = p.DistanceSquaredTo(a.Position + ab * t);
            if (d2 >= best)
                continue;
            best = d2;
            height = Mathf.Lerp(a.WaterHeight, b.WaterHeight, t);
            width = Mathf.Lerp(a.HalfWidth, b.HalfWidth, t);
        }
        return (Mathf.Sqrt(best), height, width);
    }

    private static float Smooth(float a, float b, float v) => Mathf.SmoothStep(0, 1, Mathf.Clamp((v - a) / (b - a), 0, 1));

    private void GenerateGround()
    {
        int wet = 0, wooded = 0, open = 0;
        for (int z = 0; z < Side; z++)
            for (int x = 0; x < Side; x++)
            {
                int k = Index(x, z);
                Vector2 p = GridPosition(x, z);
                var river = NearestRiver(p);
                float broad = _broad.GetNoise2D(p.X, p.Y);
                float swell = Mathf.Sin(p.X / 460 + 0.7f) * Mathf.Cos(p.Y / 570 - 0.3f);
                float land = 30 + Settings.ReliefMeters * (broad * 0.82f + swell * 0.35f)
                    - p.Y / Settings.SizeMeters * 7 + _detail.GetNoise2D(p.X, p.Y) * 0.65f;
                // Use a continuous regional floor. Nearest river stations can switch across a meander's
                // watershed; their water levels must not introduce steps in distant, otherwise smooth land.
                float floor = Mathf.Lerp(14, 6, (p.Y + Half) / Settings.SizeMeters) + 5;
                land = 0.5f * (land + floor + Mathf.Sqrt((land - floor) * (land - floor) + 16));
                float floodplain = Mathf.Lerp(river.WaterHeight + 2.4f, land,
                    Smooth(river.HalfWidth + 18, river.HalfWidth + 155, river.Distance));
                float bed = river.WaterHeight - 0.65f - 3.2f * (1 - Mathf.Pow(Mathf.Clamp(river.Distance / river.HalfWidth, 0, 1), 2));
                float h = Mathf.Lerp(bed, floodplain, Smooth(river.HalfWidth - 1, river.HalfWidth + 14, river.Distance));
                Heights[k] = h;
                RiverDistances[k] = river.Distance;
                _riverHalfWidths[k] = river.HalfWidth;
                Water[k] = h < river.WaterHeight;
                if (Water[k])
                    wet++;
                MinHeight = Mathf.Min(MinHeight, h);
                MaxHeight = Mathf.Max(MaxHeight, h);

                // Broad woodland masses plus an irregular edge; keep the central river terraces open.
                Vector2 q = p / Settings.SizeMeters;
                float patch = Gaussian(q, new Vector2(-0.30f, -0.25f), 0.18f, 0.19f)
                    + Gaussian(q, new Vector2(0.32f, 0.12f), 0.17f, 0.24f)
                    + 0.7f * Gaussian(q, new Vector2(-0.33f, 0.39f), 0.20f, 0.12f);
                float noise = _forest.GetNoise2D(p.X, p.Y);
                float clearing = 1 - 0.8f * Gaussian(q, new Vector2(-0.10f, 0.04f), 0.16f, 0.18f);
                Forest[k] = Water[k] ? 0 : Smooth(0.24f, 0.92f, patch + noise * 0.40f) * clearing
                    * Smooth(river.HalfWidth + 10, river.HalfWidth + 38, river.Distance) * Settings.ForestDensity;
                if (Forest[k] > 0.24f)
                    wooded++;
            }
        for (int z = 0; z < Side; z++)
            for (int x = 0; x < Side; x++)
            {
                int k = Index(x, z);
                int xl = Math.Max(0, x - 1), xr = Math.Min(Side - 1, x + 1);
                int zu = Math.Max(0, z - 1), zd = Math.Min(Side - 1, z + 1);
                float dx = (Heights[Index(xr, z)] - Heights[Index(xl, z)]) / ((xr - xl) * Spacing);
                float dz = (Heights[Index(x, zd)] - Heights[Index(x, zu)]) / ((zd - zu) * Spacing);
                Normals[k] = new Vector3(-dx, 1, -dz).Normalized();
                Slopes[k] = Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz)));
                float bankSetback = Smooth(_riverHalfWidths[k] + 12, _riverHalfWidths[k] + 55, RiverDistances[k]);
                Suitability[k] = Water[k] ? 0 : (1 - Smooth(3, 13, Slopes[k])) * (1 - Forest[k] * 0.72f) * bankSetback;
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

    // Scalar analysis fields interpolate smoothly between grid samples.
    private float Interpolate(float[] data, Vector2 p)
    {
        float gx = Mathf.Clamp((p.X + Half) / Spacing, 0, Side - 1);
        float gz = Mathf.Clamp((p.Y + Half) / Spacing, 0, Side - 1);
        int x = Math.Min((int)gx, Side - 2), z = Math.Min((int)gz, Side - 2);
        return Mathf.Lerp(Mathf.Lerp(data[Index(x, z)], data[Index(x + 1, z)], gx - x),
            Mathf.Lerp(data[Index(x, z + 1)], data[Index(x + 1, z + 1)], gx - x), gz - z);
    }

    // Match the renderer's two triangles per cell exactly, including near the carved riverbank.
    public float HeightAt(Vector2 p)
    {
        float gx = Mathf.Clamp((p.X + Half) / Spacing, 0, Side - 1);
        float gz = Mathf.Clamp((p.Y + Half) / Spacing, 0, Side - 1);
        int x = Math.Min((int)gx, Side - 2), z = Math.Min((int)gz, Side - 2);
        float u = gx - x, v = gz - z;
        float a = Heights[Index(x, z)], b = Heights[Index(x + 1, z)];
        float c = Heights[Index(x, z + 1)], d = Heights[Index(x + 1, z + 1)];
        return u + v <= 1 ? a + (b - a) * u + (c - a) * v : d + (c - d) * (1 - u) + (b - d) * (1 - v);
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
