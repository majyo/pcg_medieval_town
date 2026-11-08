using System;
using System.Collections.Generic;
using Godot;

namespace PcgCitySandbox.Rendering;

/// <summary>Small helpers for generating environment meshes in code.</summary>
public static class ProceduralMesh
{
    /// <summary>Unit icosphere: vertices on the unit sphere and triangle indices (counter-clockwise from outside).</summary>
    public static (List<Vector3> Vertices, List<int> Triangles) Icosphere(int subdivisions)
    {
        float t = (1 + MathF.Sqrt(5)) / 2;
        var vertices = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (int i = 0; i < vertices.Count; i++)
            vertices[i] = vertices[i].Normalized();
        var triangles = new List<int>
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };
        for (int s = 0; s < subdivisions; s++)
        {
            var midpoints = new Dictionary<long, int>();
            int Midpoint(int a, int b)
            {
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                if (midpoints.TryGetValue(key, out int index))
                    return index;
                vertices.Add(((vertices[a] + vertices[b]) / 2).Normalized());
                midpoints[key] = vertices.Count - 1;
                return vertices.Count - 1;
            }

            var next = new List<int>(triangles.Count * 4);
            for (int i = 0; i < triangles.Count; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }

            triangles = next;
        }

        return (vertices, triangles);
    }

    /// <summary>Deterministic pseudo-random value in [0, 1) for an integer key.</summary>
    public static float Hash(int key)
    {
        uint h = unchecked((uint)key * 0x9E3779B1u);
        h ^= h >> 15;
        h = unchecked(h * 0x85EBCA77u);
        h ^= h >> 13;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    /// <summary>
    /// Builds an indexed mesh with per-vertex normals, colors and UVs. Each triangle's winding is set so its front
    /// face (clockwise in Godot) faces the average of its vertex normals, so callers need not care about order.
    /// </summary>
    public static ArrayMesh Build(List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<Vector2> uvs,
        List<int> indices)
    {
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            // Counter-clockwise triangles face along the cross product; Godot's front faces are clockwise.
            var ccwNormal = (vertices[b] - vertices[a]).Cross(vertices[c] - vertices[a]);
            if (ccwNormal.Dot(normals[a] + normals[b] + normals[c]) > 0)
            {
                indices[i + 1] = c;
                indices[i + 2] = b;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
