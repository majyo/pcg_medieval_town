using Godot;
using System;
using System.Collections.Generic;

namespace PcgCitySandbox.Pcg;

public sealed class CityPickIndex
{
    private sealed class Group
    {
        public string Id = ""; public Vector3 Min = Vector3.One * float.PositiveInfinity, Max = Vector3.One * float.NegativeInfinity;
        public readonly List<int> Faces = new();
    }
    private readonly CityGeometry _geometry;
    private readonly Dictionary<(string Owner, int X, int Z), Group> _groups = new();
    public int LastTriangleTests { get; private set; }
    public CityPickIndex(CityGeometry geometry)
    {
        _geometry = geometry;
        for (int i = 0; i < geometry.Positions.Count; i += 3)
        {
            string id = geometry.Owners[i]; var center = (geometry.Positions[i] + geometry.Positions[i + 1] + geometry.Positions[i + 2]) / 3;
            var key = (id, (int)MathF.Floor(center.X / 48), (int)MathF.Floor(center.Z / 48));
            if (!_groups.TryGetValue(key, out var g)) _groups.Add(key, g = new Group { Id = id });
            g.Faces.Add(i);
            for (int j = 0; j < 3; j++) { g.Min = g.Min.Min(geometry.Positions[i + j]); g.Max = g.Max.Max(geometry.Positions[i + j]); }
        }
    }
    private static bool BoxHit(Vector3 origin, Vector3 ray, Vector3 min, Vector3 max, float nearest)
    {
        float lo = 0, hi = nearest;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(ray[axis]) < 1e-8f) { if (origin[axis] < min[axis] - .01f || origin[axis] > max[axis] + .01f) return false; continue; }
            float a = (min[axis] - .01f - origin[axis]) / ray[axis], b = (max[axis] + .01f - origin[axis]) / ray[axis];
            lo = Math.Max(lo, Math.Min(a, b)); hi = Math.Min(hi, Math.Max(a, b)); if (hi < lo) return false;
        }
        return true;
    }
    public (string? Id, float Distance) Pick(Vector3 origin, Vector3 ray, float nearest, bool roofs = true)
    {
        LastTriangleTests = 0; string? found = null;
        foreach (var g in _groups.Values)
        {
            if (!BoxHit(origin, ray, g.Min, g.Max, nearest)) continue;
            foreach (int i in g.Faces)
            {
                if (!roofs && _geometry.Parts[i].StartsWith("roof")) continue; LastTriangleTests++;
                var a = _geometry.Positions[i]; var e1 = _geometry.Positions[i + 1] - a; var e2 = _geometry.Positions[i + 2] - a;
                var p = ray.Cross(e2); float det = e1.Dot(p); if (Math.Abs(det) < 1e-7f) continue;
                var s = origin - a; float u = s.Dot(p) / det; if (u < 0 || u > 1) continue;
                var q = s.Cross(e1); float v = ray.Dot(q) / det; if (v < 0 || u + v > 1) continue;
                float t = e2.Dot(q) / det; if (t > 0 && t < nearest) { nearest = t; found = g.Id; }
            }
        }
        return (found, nearest);
    }
}
