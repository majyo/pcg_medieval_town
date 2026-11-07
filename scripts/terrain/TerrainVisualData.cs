using Godot;
using System;
using System.Collections.Generic;

namespace PcgCitySandbox;

// Render-only data: never writes to TemperateMapData or its settings.
public sealed class TerrainVisualData : IDisposable
{
    public const int Resolution = 1024;
    public byte[] NaturalBytes { get; }
    public float[] RiverValues { get; }
    public ImageTexture Natural { get; }
    public ImageTexture River { get; }
    public ImageTexture Heights { get; }
    public double BuildMilliseconds { get; }
    public long TextureBytes { get; }
    public bool ControlHasMipmaps { get; }

    public TerrainVisualData(TemperateMapData map)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        NaturalBytes = new byte[Resolution * Resolution * 4];
        RiverValues = new float[Resolution * Resolution * 2];
        var riverIndex = new VisualRiverIndex(map);
        using var macro = new FastNoiseLite { Seed = map.Settings.Seed ^ 17291, Frequency = .006f, FractalType = FastNoiseLite.FractalTypeEnum.None };
        using var patch = new FastNoiseLite { Seed = map.Settings.Seed ^ 68323, Frequency = .052f, FractalType = FastNoiseLite.FractalTypeEnum.None };
        for (int z = 0; z < Resolution; z++)
        for (int x = 0; x < Resolution; x++)
        {
            Vector2 p = new(-map.Half + (x + .5f) * map.Settings.SizeMeters / Resolution, -map.Half + (z + .5f) * map.Settings.SizeMeters / Resolution);
            int k = z * Resolution + x;
            float forest = Field(map, map.Forest, p), slope = Field(map, map.Slopes, p);
            float broad = macro.GetNoise2D(p.X, p.Y) * .5f + .5f;
            // Warp and mix scales in world space, avoiding uniformly sized speckles across the plain.
            Vector2 q = p + new Vector2(macro.GetNoise2D(p.X + 617, p.Y - 923), macro.GetNoise2D(p.X - 437, p.Y + 281)) * 28;
            float local = .5f + .5f * (patch.GetNoise2D(q.X, q.Y) * .66f + patch.GetNoise2D(q.X * .417f + 137, q.Y * .417f - 293) * .34f);
            float height = Mathf.InverseLerp(map.MinHeight, map.MaxHeight, map.HeightAt(p));
            NaturalBytes[k * 4] = Byte(forest);
            NaturalBytes[k * 4 + 1] = Byte(broad);
            NaturalBytes[k * 4 + 2] = Byte(Smooth(.57f, .8f, local) * .25f + Smooth(12, 30, slope) * .65f + Smooth(.65f, .95f, height) * .05f);
            NaturalBytes[k * 4 + 3] = Byte(local);
            var river = riverIndex.Query(p);
            RiverValues[k * 2] = river.WaterHeight;
            RiverValues[k * 2 + 1] = river.Found ? 1 - Smooth(river.HalfWidth + 20, river.HalfWidth + 42, river.Distance) : 0;
        }
        using var natural = Image.CreateFromData(Resolution, Resolution, false, Image.Format.Rgba8, NaturalBytes);
        natural.GenerateMipmaps(); ControlHasMipmaps = natural.HasMipmaps(); Natural = ImageTexture.CreateFromImage(natural);
        byte[] riverBytes = new byte[RiverValues.Length * sizeof(float)]; Buffer.BlockCopy(RiverValues, 0, riverBytes, 0, riverBytes.Length);
        using var rivers = Image.CreateFromData(Resolution, Resolution, false, Image.Format.Rgf, riverBytes);
        River = ImageTexture.CreateFromImage(rivers);
        byte[] heights = new byte[map.Heights.Length * sizeof(float)]; Buffer.BlockCopy(map.Heights, 0, heights, 0, heights.Length);
        using var heightImage = Image.CreateFromData(map.Side, map.Side, false, Image.Format.Rf, heights);
        Heights = ImageTexture.CreateFromImage(heightImage);
        TextureBytes = natural.GetData().LongLength + riverBytes.LongLength + heights.LongLength;
        BuildMilliseconds = timer.Elapsed.TotalMilliseconds;
    }

    public static float Field(TemperateMapData map, float[] values, Vector2 p)
    {
        float gx = Mathf.Clamp((p.X + map.Half) / map.Spacing, 0, map.Side - 1), gz = Mathf.Clamp((p.Y + map.Half) / map.Spacing, 0, map.Side - 1);
        int x = Math.Min((int)gx, map.Side - 2), z = Math.Min((int)gz, map.Side - 2), i = map.Index(x, z);
        return Mathf.Lerp(Mathf.Lerp(values[i], values[i + 1], gx - x), Mathf.Lerp(values[i + map.Side], values[i + map.Side + 1], gx - x), gz - z);
    }
    internal static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
    internal static float Smooth(float a, float b, float v) { float t = Math.Clamp((v - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    public void Dispose() { Natural.Dispose(); River.Dispose(); Heights.Dispose(); }
}

public readonly record struct VisualRiverSample(bool Found, float Distance, float WaterHeight, float HalfWidth);

// Spatial acceleration is restricted to the rendered river neighbourhood; it is not an environment query replacement.
public sealed class VisualRiverIndex
{
    private readonly TemperateMapData _map;
    private readonly int _side;
    private readonly float _step;
    private readonly List<int>[] _cells;
    public VisualRiverIndex(TemperateMapData map)
    {
        _map = map; _side = (int)Math.Ceiling(map.Settings.SizeMeters / 100); _step = map.Settings.SizeMeters / _side;
        _cells = new List<int>[_side * _side]; for (int i = 0; i < _cells.Length; i++) _cells[i] = new();
        for (int i = 0; i < map.River.Count - 1; i++)
        {
            var a = map.River[i]; var b = map.River[i + 1]; float radius = Math.Max(a.HalfWidth, b.HalfWidth) + 45;
            int x0 = Cell(Math.Min(a.Position.X, b.Position.X) - radius), x1 = Cell(Math.Max(a.Position.X, b.Position.X) + radius);
            int z0 = Cell(Math.Min(a.Position.Y, b.Position.Y) - radius), z1 = Cell(Math.Max(a.Position.Y, b.Position.Y) + radius);
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) _cells[z * _side + x].Add(i);
        }
    }
    private int Cell(float p) => Math.Clamp((int)MathF.Floor((p + _map.Half) / _step), 0, _side - 1);
    public VisualRiverSample Query(Vector2 p)
    {
        float best = float.MaxValue, height = 0, width = 0;
        foreach (int i in _cells[Cell(p.Y) * _side + Cell(p.X)])
        {
            var a = _map.River[i]; var b = _map.River[i + 1]; var ab = b.Position - a.Position;
            float t = Math.Clamp((p - a.Position).Dot(ab) / ab.LengthSquared(), 0, 1);
            float distance = p.DistanceSquaredTo(a.Position + ab * t);
            if (distance >= best) continue;
            best = distance; height = Mathf.Lerp(a.WaterHeight, b.WaterHeight, t); width = Mathf.Lerp(a.HalfWidth, b.HalfWidth, t);
        }
        return new(best < float.MaxValue, MathF.Sqrt(best), height, width);
    }
}
