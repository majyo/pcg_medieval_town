using System;
using System.Collections.Generic;
using Godot;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// Procedural tree meshes in three kinds: a round broadleaf, a slender birch-like tree and a tiered conifer.
/// Canopies are clusters of lumpy blobs whose normals point away from the canopy center, so light falls on the
/// crown as one soft mass. Vertex colors are sRGB and darken toward the inside and bottom of the crown.
/// UV.y is the height above the base in meters (the shader bends by it for wind).
/// </summary>
public static class TreeMeshes
{
    public const int Kinds = 3;
    public const int VariantsPerKind = 4;

    private sealed class Builder
    {
        public readonly List<Vector3> Vertices = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Color> Colors = new();
        public readonly List<Vector2> Uvs = new();
        public readonly List<int> Indices = new();

        public int Add(Vector3 v, Vector3 n, Color c)
        {
            Vertices.Add(v);
            Normals.Add(n);
            Colors.Add(c);
            Uvs.Add(new Vector2(0, v.Y));
            return Vertices.Count - 1;
        }

        public ArrayMesh Commit() => ProceduralMesh.Build(Vertices, Normals, Colors, Uvs, Indices);
    }

    public static ArrayMesh Create(int kind, int variant)
    {
        int seed = kind * 1000 + variant * 37 + 11;
        float R(int k) => ProceduralMesh.Hash(seed * 131 + k);
        var b = new Builder();
        switch (kind)
        {
            case 0:
                {
                    float height = 4.2f + R(1) * 1.2f;
                    Trunk(b, height, 0.32f, 0.18f, new Color("6b5440"), R(2));
                    var center = new Vector3(0, height + 2.4f, 0);
                    Canopy(b, center, new Vector3(3.6f, 2.8f, 3.6f), 6, 1.9f, new Color("5d8a45"), seed);
                    break;
                }
            case 1:
                {
                    float height = 6.0f + R(1) * 1.5f;
                    Trunk(b, height, 0.2f, 0.11f, new Color("d8d2c4"), R(2));
                    var center = new Vector3(0, height + 1.8f, 0);
                    Canopy(b, center, new Vector3(2.1f, 3.4f, 2.1f), 5, 1.4f, new Color("7fa356"), seed);
                    break;
                }
            default:
                {
                    float height = 2.0f + R(1) * 0.6f;
                    Trunk(b, height + 6, 0.28f, 0.08f, new Color("5a4636"), R(2));
                    Conifer(b, height, 9.5f + R(3) * 2.5f, 3.4f + R(4) * 0.6f, new Color("3f6a4a"), seed);
                    break;
                }
        }

        return b.Commit();
    }

    /// <summary>Tapered seven-sided trunk with a slight bend.</summary>
    private static void Trunk(Builder b, float height, float bottom, float top, Color color, float bend)
    {
        const int sides = 7, rings = 4;
        var bendDirection = new Vector3(Mathf.Cos(bend * Mathf.Tau), 0, Mathf.Sin(bend * Mathf.Tau)) * 0.35f;
        int start = b.Vertices.Count;
        for (int r = 0; r <= rings; r++)
        {
            float t = r / (float)rings;
            float radius = Mathf.Lerp(bottom, top, t);
            var axis = new Vector3(0, height * t, 0) + bendDirection * t * t;
            for (int s = 0; s < sides; s++)
            {
                float a = s * Mathf.Tau / sides;
                var radial = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                // Bark darkens a little toward the ground.
                b.Add(axis + radial * radius, radial, color.Darkened(0.25f * (1 - t)));
            }
        }

        for (int r = 0; r < rings; r++)
            for (int s = 0; s < sides; s++)
            {
                int a0 = start + r * sides + s, a1 = start + r * sides + (s + 1) % sides;
                int b0 = a0 + sides, b1 = a1 + sides;
                b.Indices.AddRange(new[] { a0, b0, a1, a1, b0, b1 });
            }
    }

    /// <summary>A crown of lumpy blobs inside an ellipsoid; normals blend toward "away from the crown center".</summary>
    private static void Canopy(Builder b, Vector3 center, Vector3 extent, int blobs, float blobRadius, Color leaf, int seed)
    {
        var (sphere, triangles) = ProceduralMesh.Icosphere(1);
        for (int k = 0; k < blobs; k++)
        {
            float R(int i) => ProceduralMesh.Hash(seed * 977 + k * 31 + i);
            var offset = new Vector3((R(1) * 2 - 1) * extent.X, (R(2) * 2 - 1) * extent.Y * 0.6f, (R(3) * 2 - 1) * extent.Z) * 0.55f;
            if (k == 0)
                offset = new Vector3(0, extent.Y * 0.25f, 0);
            var blobCenter = center + offset;
            float radius = blobRadius * (0.8f + R(4) * 0.45f);
            int start = b.Vertices.Count;
            foreach (var direction in sphere)
            {
                float lump = 1 + (ProceduralMesh.Hash(seed + k * 7919 + (int)(direction.X * 97 + direction.Y * 389 + direction.Z * 1013)) - 0.5f) * 0.3f;
                var position = blobCenter + direction * radius * lump;
                var away = (position - center).Normalized();
                var normal = direction.Lerp(away, 0.75f).Normalized();
                // Darker underneath and toward the inside of the crown, lighter on top.
                float up = Mathf.Clamp((position.Y - (center.Y - extent.Y)) / (2 * extent.Y), 0, 1);
                float shade = 0.72f + 0.38f * up + 0.08f * (R(5) - 0.5f);
                b.Add(position, normal, new Color(leaf.R * shade, leaf.G * shade, leaf.B * shade));
            }

            foreach (int index in triangles)
                b.Indices.Add(start + index);
        }
    }

    /// <summary>Stacked cones with ragged rims; normals lean outward and up from the axis.</summary>
    private static void Conifer(Builder b, float baseHeight, float crownHeight, float baseRadius, Color needles, int seed)
    {
        const int tiers = 4, sides = 11;
        for (int tier = 0; tier < tiers; tier++)
        {
            float t0 = tier / (float)tiers;
            float bottom = baseHeight + crownHeight * t0 * 0.85f;
            float top = bottom + crownHeight * (0.42f - tier * 0.04f);
            float radius = baseRadius * (1 - t0 * 0.72f);
            var shade = needles.Lightened(0.06f * tier);
            int apex = b.Add(new Vector3(0, top, 0), Vector3.Up, shade.Lightened(0.12f));
            int rim = b.Vertices.Count;
            for (int s = 0; s < sides; s++)
            {
                float a = s * Mathf.Tau / sides + tier * 0.4f;
                float ragged = s % 2 == 0 ? 1.0f : 0.78f + ProceduralMesh.Hash(seed + tier * 53 + s) * 0.12f;
                var radial = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var position = new Vector3(0, bottom - (s % 2) * 0.25f, 0) + radial * radius * ragged;
                var normal = (radial + Vector3.Up * 0.6f).Normalized();
                b.Add(position, normal, shade.Darkened(0.18f));
            }

            for (int s = 0; s < sides; s++)
                b.Indices.AddRange(new[] { apex, rim + (s + 1) % sides, rim + s });
            // Underside so the tier reads as a solid skirt from below.
            int under = b.Add(new Vector3(0, bottom + 0.3f, 0), Vector3.Down, shade.Darkened(0.35f));
            for (int s = 0; s < sides; s++)
                b.Indices.AddRange(new[] { under, rim + s, rim + (s + 1) % sides });
        }
    }
}
