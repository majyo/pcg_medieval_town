using Godot;
using System;
using System.IO;
using System.Security.Cryptography;

namespace PcgCitySandbox.Pcg;

// Compatibility surface for isolated planning fixtures. The application pipeline
// passes GradedTerrain explicitly; planning sampling remains immutable.
public sealed class SampledTerrainSurface(CityEnvironmentData data) : ITerrainSurface
{
    public string Fingerprint => $"sample/{data.SourceId}/{data.SourceRevision}";
    public Rect2 Bounds => data.Bounds;
    public float Spacing => Math.Min(data.StepX, data.StepZ);
    public bool Contains(Vector2 p) => data.TryHeightAt(p, out _);
    public float HeightAt(Vector2 p) => data.TryHeightAt(p, out float h) ? h : throw new ArgumentOutOfRangeException(nameof(p));
    public float WaterClearance(Vector2 p) => data.TryGetCell(p, out var c) && c.Water == false ? float.PositiveInfinity : 0;
}

public static class TerrainSurface
{
    public static bool TryHeightAt(this ITerrainSurface terrain, Vector2 p, out float height)
    {
        height = 0;
        if (!terrain.Contains(p)) return false;
        height = terrain.HeightAt(p); return float.IsFinite(height);
    }
    public static float SlopeAt(this ITerrainSurface terrain, Vector2 p)
    {
        float d = Math.Clamp(terrain.Spacing, .25f, 2);
        float H(Vector2 q) => terrain.HeightAt(terrain.Bounds.Position.Max(q).Min(terrain.Bounds.End));
        return Mathf.RadToDeg(Mathf.Atan(new Vector2(H(p + Vector2.Right*d) - H(p - Vector2.Right*d),
            H(p + Vector2.Down*d) - H(p - Vector2.Down*d)).Length() / (2*d)));
    }
    public static string Digest(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        write(writer); writer.Flush(); return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}
