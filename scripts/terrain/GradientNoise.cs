using System;
using Godot;

namespace PcgCitySandbox.Terrain;

/// <summary>
/// 2D gradient (Perlin) noise in pure C#: deterministic for a seed, allocation-free and safe to call from many
/// threads at once. Output is roughly in [-1, 1]. Each instance is one octave; combine them for detail.
/// </summary>
public readonly struct GradientNoise
{
    private static readonly Vector2[] Gradients = CreateGradients();

    private readonly uint _seed;
    private readonly float _frequency;
    private readonly float _cos, _sin;

    /// <param name="seed">Any integer; different seeds give unrelated fields.</param>
    /// <param name="wavelength">Feature size in meters.</param>
    /// <param name="rotation">Rotates the lattice so stacked octaves don't line up along the axes.</param>
    public GradientNoise(int seed, float wavelength, float rotation = 0)
    {
        _seed = Mix(unchecked((uint)seed) ^ 0x9E3779B9u);
        _frequency = 1 / wavelength;
        _cos = MathF.Cos(rotation);
        _sin = MathF.Sin(rotation);
    }

    public float Sample(Vector2 p) => Sample(p.X, p.Y);

    public float Sample(float worldX, float worldY)
    {
        float x = (worldX * _cos - worldY * _sin) * _frequency;
        float y = (worldX * _sin + worldY * _cos) * _frequency;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        float n00 = Dot(ix, iy, fx, fy);
        float n10 = Dot(ix + 1, iy, fx - 1, fy);
        float n01 = Dot(ix, iy + 1, fx, fy - 1);
        float n11 = Dot(ix + 1, iy + 1, fx - 1, fy - 1);
        float u = Fade(fx), v = Fade(fy);
        float bottom = n00 + (n10 - n00) * u;
        float top = n01 + (n11 - n01) * u;
        // 2D Perlin noise peaks at sqrt(1/2); scale to about [-1, 1].
        return (bottom + (top - bottom) * v) * 1.4142135f;
    }

    private float Dot(int ix, int iy, float dx, float dy)
    {
        var g = Gradients[Hash(ix, iy) & 63];
        return g.X * dx + g.Y * dy;
    }

    private uint Hash(int x, int y) => Mix(_seed ^ unchecked((uint)x * 0x8DA6B343u) ^ unchecked((uint)y * 0xD8163841u));

    private static uint Mix(uint h)
    {
        h ^= h >> 16;
        h = unchecked(h * 0x7FEB352Du);
        h ^= h >> 15;
        h = unchecked(h * 0x846CA68Bu);
        h ^= h >> 16;
        return h;
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static Vector2[] CreateGradients()
    {
        var gradients = new Vector2[64];
        for (int i = 0; i < gradients.Length; i++)
        {
            float angle = (i + 0.5f) * MathF.Tau / gradients.Length;
            gradients[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        return gradients;
    }
}
