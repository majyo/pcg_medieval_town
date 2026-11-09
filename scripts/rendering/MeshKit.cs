using System;
using System.Collections.Generic;
using Godot;

namespace PcgCitySandbox.Rendering;

/// <summary>Small vertex-colored mesh builder. Materials are double-sided unless a cull mode is given.</summary>
public sealed class MeshKit
{
    private readonly SurfaceTool _tool = new();
    private bool _empty = true;

    public MeshKit(Mesh.PrimitiveType type = Mesh.PrimitiveType.Triangles)
    {
        _tool.Begin(type);
    }

    public static StandardMaterial3D Material(bool unshaded = false, float alpha = 1,
        BaseMaterial3D.CullModeEnum cull = BaseMaterial3D.CullModeEnum.Disabled) => new()
        {
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            Roughness = 1,
            CullMode = cull,
            ShadingMode = unshaded ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
            Transparency = alpha < 1 ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
            AlbedoColor = new Color(1, 1, 1, alpha),
        };

    /// <summary>Normal is taken from the winding, then flipped to face <paramref name="toward"/> if given.</summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color, Vector3? toward = null)
    {
        var normal = (b - a).Cross(c - a);
        if (normal.LengthSquared() < 1e-10f)
            return;
        normal = normal.Normalized();
        if (toward is { } t && normal.Dot(t) < 0)
            normal = -normal;
        foreach (var v in new[] { a, b, c })
        {
            _tool.SetColor(color);
            _tool.SetNormal(normal);
            _tool.AddVertex(v);
        }

        _empty = false;
    }

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, Vector3? toward = null)
    {
        Triangle(a, b, c, color, toward);
        Triangle(a, c, d, color, toward);
    }

    public void Line(Vector3 a, Vector3 b, Color color)
    {
        _tool.SetColor(color);
        _tool.AddVertex(a);
        _tool.SetColor(color);
        _tool.AddVertex(b);
        _empty = false;
    }

    /// <summary>Upright box over a planar quad footprint (corners in order), from base to top.</summary>
    public void Prism(IReadOnlyList<Vector2> corners, float bottom, float top, Color side, Color cap)
    {
        var center = Vector2.Zero;
        foreach (var c in corners)
            center += c / corners.Count;
        for (int i = 0; i < corners.Count; i++)
        {
            var p = corners[i];
            var q = corners[(i + 1) % corners.Count];
            var mid = (p + q) / 2 - center;
            var outward = new Vector3(mid.X, 0, mid.Y);
            Quad(new(p.X, bottom, p.Y), new(q.X, bottom, q.Y), new(q.X, top, q.Y), new(p.X, top, p.Y), side, outward);
        }

        for (int i = 1; i < corners.Count - 1; i++)
        {
            Triangle(new(corners[0].X, top, corners[0].Y), new(corners[i].X, top, corners[i].Y),
                new(corners[i + 1].X, top, corners[i + 1].Y), cap, Vector3.Up);
        }
    }

    /// <summary>Planar triangles, split until no edge exceeds <paramref name="maxEdge"/>, lifted onto the ground.</summary>
    public void Drape(Vector2[] triangles, Func<Vector2, float> height, float lift, Color color, float maxEdge = 1.5f)
    {
        var stack = new Stack<(Vector2, Vector2, Vector2, int)>();
        for (int i = 0; i + 2 < triangles.Length; i += 3)
            stack.Push((triangles[i], triangles[i + 1], triangles[i + 2], 0));
        while (stack.Count > 0)
        {
            var (a, b, c, depth) = stack.Pop();
            float longest = Math.Max(a.DistanceSquaredTo(b), Math.Max(b.DistanceSquaredTo(c), c.DistanceSquaredTo(a)));
            if (longest > maxEdge * maxEdge && depth < 10)
            {
                Vector2 ab = (a + b) / 2, bc = (b + c) / 2, ca = (c + a) / 2;
                stack.Push((a, ab, ca, depth + 1));
                stack.Push((ab, b, bc, depth + 1));
                stack.Push((ca, bc, c, depth + 1));
                stack.Push((ab, bc, ca, depth + 1));
                continue;
            }

            Vector3 Lift(Vector2 p) => new(p.X, height(p) + lift, p.Y);
            Triangle(Lift(a), Lift(b), Lift(c), color, Vector3.Up);
        }
    }

    /// <summary>
    /// An indexed, vertex-colored triangle mesh straight from arrays, without normals (for unshaded materials).
    /// Much faster than adding vertices one by one when there are hundreds of thousands of them.
    /// </summary>
    public static ArrayMesh? Indexed(Vector3[] vertices, Color[] colors, int[] indices)
    {
        if (indices.Length == 0)
            return null;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public ArrayMesh? Commit()
    {
        if (_empty)
            return null;
        return _tool.Commit();
    }
}
