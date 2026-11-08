using Godot;

namespace PcgCitySandbox.Terrain;

/// <summary>
/// The read-only terrain queries other components may use. The city generator sees only this; grids,
/// noise and generation internals stay inside the terrain implementation.
/// World X/Z are horizontal, Y is up; one unit is one meter; the map is centered on the origin.
/// </summary>
public interface ITerrain
{
    /// <summary>Half the side length of the square map.</summary>
    float Half { get; }

    bool Contains(Vector2 p);
    float HeightAt(Vector2 p);
    TerrainSample Sample(Vector2 p);
    (float Distance, float WaterHeight, float HalfWidth) NearestRiver(Vector2 p);
}

public static class TerrainQueries
{
    /// <summary>
    /// Where a straight ray (e.g. from an orthographic camera) meets the ground, by fixed-point refinement
    /// on the ground height. Returns false when the ray misses the map.
    /// </summary>
    public static bool Pick(this ITerrain terrain, Vector3 origin, Vector3 direction, out Vector2 hit)
    {
        hit = Vector2.Zero;
        if (Mathf.Abs(direction.Y) < 1e-4f)
            return false;
        float y = 15;
        for (int i = 0; i < 4; i++)
        {
            var p = origin + direction * ((y - origin.Y) / direction.Y);
            hit = new Vector2(p.X, p.Z);
            if (!terrain.Contains(hit))
                return false;
            y = terrain.HeightAt(hit);
        }

        return true;
    }
}
