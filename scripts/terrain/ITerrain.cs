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
    /// Where a ray (e.g. through a camera pixel) first meets the ground. Marches by a fraction of the
    /// clearance above the ground, then bisects. Returns false when the ray misses the map.
    /// </summary>
    public static bool Pick(this ITerrain terrain, Vector3 origin, Vector3 direction, out Vector2 hit,
        float maxDistance = 20000)
    {
        hit = Vector2.Zero;
        direction = direction.Normalized();
        float Clearance(float t)
        {
            var p = origin + direction * t;
            return p.Y - terrain.HeightAt(new Vector2(p.X, p.Z));
        }

        float before = 0, after = -1;
        for (int i = 0; i < 1024 && before < maxDistance; i++)
        {
            float clearance = Clearance(before);
            if (clearance <= 0)
            {
                after = before;
                break;
            }

            // Ground slopes stay well below 45°, so half the vertical clearance is a safe step.
            float step = Mathf.Max(0.05f, clearance * 0.5f);
            if (Clearance(before + step) <= 0)
            {
                after = before + step;
                break;
            }

            before += step;
        }

        if (after < 0)
            return false;
        for (int i = 0; i < 12; i++)
        {
            float mid = (before + after) / 2;
            if (Clearance(mid) > 0)
                before = mid;
            else
                after = mid;
        }

        var point = origin + direction * after;
        hit = new Vector2(point.X, point.Z);
        return terrain.Contains(hit);
    }
}
