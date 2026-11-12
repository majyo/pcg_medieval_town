using System;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.City;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// The map with the town's grading applied: the terrain's height grid plus the grading's offsets, point for point.
/// It is the one height source of everything drawn — the ground imports <see cref="Heights"/> as they are and the
/// city overlays interpolate in the same grid — while generation keeps reading the terrain it wraps.
/// </summary>
public sealed class GradedTerrain : ITerrain
{
    private readonly TemperateMapData _map;

    public GradedTerrain(TemperateMapData map, GradePlan? grading = null)
    {
        _map = map;
        Grading = grading;
        if (grading == null || grading.Patch.IsEmpty)
        {
            Heights = map.HeightGrid;
            return;
        }

        Heights = (float[])map.HeightGrid.Clone();
        var patch = grading.Patch.Bounds;
        int Index(float world) => Math.Clamp((int)MathF.Round((world + map.Half) / map.HeightSpacing), 0, map.HeightSide - 1);
        int x0 = Index(patch.Position.X - 1), x1 = Index(patch.End.X + 1);
        int z0 = Index(patch.Position.Y - 1), z1 = Index(patch.End.Y + 1);
        Changed = new Rect2I(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
        Parallel.For(z0, z1 + 1, z =>
        {
            for (int x = x0; x <= x1; x++)
                Heights[map.HeightIndex(x, z)] += grading.Patch.Sample(map.HeightPosition(x, z));
        });
    }

    /// <summary>The grading applied, or null for the terrain as generated.</summary>
    public GradePlan? Grading { get; }

    /// <summary>Ground height at every point of the map's height grid, graded.</summary>
    public float[] Heights { get; }

    /// <summary>The height-grid points (columns and rows) whose height differs from the terrain's; empty without grading.</summary>
    public Rect2I Changed { get; }

    public float Half => _map.Half;

    public bool Contains(Vector2 p) => _map.Contains(p);

    /// <summary>Bilinear interpolation of <see cref="Heights"/>, clamped to the map, as the terrain interpolates its own.</summary>
    public float HeightAt(Vector2 p)
    {
        int side = _map.HeightSide;
        float gx = Mathf.Clamp((p.X + Half) / _map.HeightSpacing, 0, side - 1);
        float gz = Mathf.Clamp((p.Y + Half) / _map.HeightSpacing, 0, side - 1);
        int x = Math.Min((int)gx, side - 2), z = Math.Min((int)gz, side - 2);
        float u = gx - x, v = gz - z;
        int k = _map.HeightIndex(x, z);
        float top = Heights[k] + (Heights[k + 1] - Heights[k]) * u;
        float bottom = Heights[k + side] + (Heights[k + side + 1] - Heights[k + side]) * u;
        return top + (bottom - top) * v;
    }

    public TerrainSample Sample(Vector2 p) => _map.Sample(p) with { Height = HeightAt(p) };

    public (float Distance, float WaterHeight, float HalfWidth) NearestRiver(Vector2 p, float maxDistance = float.PositiveInfinity) =>
        _map.NearestRiver(p, maxDistance);
}
