using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// Where vegetation and props must not appear: an area (with its bounds, for speed) plus strips around polylines.
/// Generic shapes only, so rendering code does not need to know what the caller derived them from (the town).
/// </summary>
public sealed class Exclusion
{
    private readonly Func<Vector2, bool> _area;
    private readonly Rect2 _areaBounds;
    private readonly (Vector2[] Line, float HalfWidth, Rect2 Bounds)[] _strips;

    public Exclusion(Func<Vector2, bool> area, Rect2 areaBounds, IEnumerable<(Vector2[] Line, float HalfWidth)> strips)
    {
        _area = area;
        _areaBounds = areaBounds;
        var list = new List<(Vector2[], float, Rect2)>();
        foreach (var (line, halfWidth) in strips)
        {
            if (line.Length == 0)
                continue;
            var bounds = new Rect2(line[0], Vector2.Zero);
            foreach (var p in line)
                bounds = bounds.Expand(p);
            list.Add((line, halfWidth, bounds.Grow(halfWidth)));
        }

        _strips = list.ToArray();
    }

    public bool Contains(Vector2 p)
    {
        if (_areaBounds.HasPoint(p) && _area(p))
            return true;
        foreach (var (line, halfWidth, bounds) in _strips)
        {
            if (!bounds.HasPoint(p))
                continue;
            for (int i = 1; i < line.Length; i++)
            {
                if (DistanceToSegment(p, line[i - 1], line[i]) <= halfWidth)
                    return true;
            }
        }

        return false;
    }

    /// <summary>One byte per height-grid point of <paramref name="map"/>: 255 inside, 0 outside.</summary>
    public Image Rasterize(TemperateMapData map)
    {
        int side = map.HeightSide;
        float spacing = map.HeightSpacing, half = map.Half;
        var pixels = new byte[side * side];
        int ToIndex(float world) => Math.Clamp((int)MathF.Round((world + half) / spacing), 0, side - 1);

        // The area: only test points inside its bounds.
        int x0 = ToIndex(_areaBounds.Position.X), x1 = ToIndex(_areaBounds.End.X);
        int z0 = ToIndex(_areaBounds.Position.Y), z1 = ToIndex(_areaBounds.End.Y);
        Parallel.For(z0, z1 + 1, z =>
        {
            for (int x = x0; x <= x1; x++)
            {
                if (_area(map.HeightPosition(x, z)))
                    pixels[z * side + x] = 255;
            }
        });

        // Strips: visit only the pixels around each segment.
        foreach (var (line, halfWidth, _) in _strips)
        {
            for (int i = 1; i < line.Length; i++)
            {
                var a = line[i - 1];
                var b = line[i];
                int sx0 = ToIndex(MathF.Min(a.X, b.X) - halfWidth), sx1 = ToIndex(MathF.Max(a.X, b.X) + halfWidth);
                int sz0 = ToIndex(MathF.Min(a.Y, b.Y) - halfWidth), sz1 = ToIndex(MathF.Max(a.Y, b.Y) + halfWidth);
                for (int z = sz0; z <= sz1; z++)
                    for (int x = sx0; x <= sx1; x++)
                    {
                        if (DistanceToSegment(map.HeightPosition(x, z), a, b) <= halfWidth)
                            pixels[z * side + x] = 255;
                    }
            }
        }

        return Image.CreateFromData(side, side, false, Image.Format.R8, pixels);
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-8f ? 0 : Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return p.DistanceTo(a + ab * t);
    }
}
