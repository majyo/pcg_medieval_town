using System;
using System.Collections.Generic;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// Grass tufts and wild flowers around the camera, placed entirely on the GPU. Each layer is one MultiMesh with
/// a fixed grid of instances that follows the camera in whole-cell steps; the vertex shader jitters each cell,
/// reads ground height, ground masks and the exclusion map, and collapses instances that should not grow there.
/// No per-frame CPU work beyond moving the node.
/// </summary>
public partial class GroundCover : Node3D
{
    private const string ShaderPath = "res://shaders/grass.gdshader";

    /// <summary>Hidden when the camera is farther than this from its focus; the cover is too small to matter.</summary>
    private const float VisibleCameraDistance = 320;

    private readonly List<(MultiMeshInstance3D Node, ShaderMaterial Material, float Spacing)> _layers = new();
    private ImageTexture _exclusion = null!;
    private int _side;

    public void Build(TemperateMapData map, Image masks)
    {
        _side = map.HeightSide;
        var heights = Image.CreateFromData(_side, _side, false, Image.Format.Rf,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(map.HeightGrid.AsSpan()).ToArray());
        var heightTexture = ImageTexture.CreateFromImage(heights);
        var maskTexture = ImageTexture.CreateFromImage(masks);
        _exclusion = ImageTexture.CreateFromImage(Image.CreateEmpty(_side, _side, false, Image.Format.R8));

        var shader = GD.Load<Shader>(ShaderPath);
        AddLayer("Grass", TuftMesh(), shader, kind: 0, gridSize: 300, spacing: 0.33f, map, heightTexture, maskTexture);
        AddLayer("Flowers", FlowerMesh(), shader, kind: 1, gridSize: 100, spacing: 0.9f, map, heightTexture, maskTexture);
    }

    /// <summary>Where nothing may grow, one byte per height-grid point (from <see cref="Exclusion.Rasterize"/>).</summary>
    public void SetExclusion(Image? exclusion)
    {
        _exclusion.Update(exclusion ?? Image.CreateEmpty(_side, _side, false, Image.Format.R8));
    }

    /// <summary>Centers the cover between the camera and its focus, snapped to whole cells.</summary>
    public void Follow(Vector3 camera, Vector3 focus)
    {
        Visible = camera.DistanceTo(focus) < VisibleCameraDistance;
        if (!Visible)
            return;
        var center = camera.Lerp(focus, 0.6f);
        foreach (var (node, _, spacing) in _layers)
            node.Position = new Vector3(MathF.Floor(center.X / spacing) * spacing, 0, MathF.Floor(center.Z / spacing) * spacing);
    }

    private void AddLayer(string name, ArrayMesh mesh, Shader shader, int kind, int gridSize, float spacing,
        TemperateMapData map, Texture2D heights, Texture2D masks)
    {
        int count = gridSize * gridSize;
        var multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = count };
        // Identity transforms: the shader places every instance itself.
        var buffer = new float[count * 12];
        for (int i = 0; i < count; i++)
        {
            buffer[i * 12] = 1;
            buffer[i * 12 + 5] = 1;
            buffer[i * 12 + 10] = 1;
        }

        multimesh.Buffer = buffer;
        float radius = gridSize * spacing * 0.5f;
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("kind", kind);
        material.SetShaderParameter("grid_size", gridSize);
        material.SetShaderParameter("spacing", spacing);
        material.SetShaderParameter("radius", radius);
        material.SetShaderParameter("map_half", map.Half);
        material.SetShaderParameter("map_samples", (float)map.HeightSide);
        material.SetShaderParameter("height_map", heights);
        material.SetShaderParameter("mask_map", masks);
        material.SetShaderParameter("exclusion_map", _exclusion);
        var node = new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = multimesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Instances are placed in the shader, so the engine cannot know their bounds.
            CustomAabb = new Aabb(new Vector3(-radius, -50, -radius), new Vector3(2 * radius, 200, 2 * radius)),
        };
        AddChild(node);
        _layers.Add((node, material, spacing));
    }

    /// <summary>A tuft of seven curved blades. UV.y runs 0 at the root to 1 at the tip.</summary>
    private static ArrayMesh TuftMesh()
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        const int blades = 7, segments = 3;
        for (int b = 0; b < blades; b++)
        {
            float R(int k) => ProceduralMesh.Hash(b * 97 + k);
            float angle = b * Mathf.Tau / blades + R(1) * 0.8f;
            var outward = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            var across = new Vector3(-outward.Z, 0, outward.X);
            var root = outward * (0.03f + R(2) * 0.06f);
            float height = 0.32f + R(3) * 0.22f;
            float lean = 0.12f + R(4) * 0.16f;
            float width = 0.035f + R(5) * 0.015f;
            int start = vertices.Count;
            for (int s = 0; s <= segments; s++)
            {
                float t = s / (float)segments;
                var center = root + outward * lean * t * t + Vector3.Up * height * t;
                float w = width * (1 - t * 0.85f);
                foreach (float side in new[] { -1f, 1f })
                {
                    vertices.Add(center + across * w * side);
                    normals.Add((Vector3.Up + outward * 0.4f).Normalized());
                    colors.Add(Colors.White);
                    uvs.Add(new Vector2(side * 0.5f + 0.5f, t));
                }
            }

            for (int s = 0; s < segments; s++)
            {
                int a = start + s * 2;
                indices.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
            }
        }

        return ProceduralMesh.Build(vertices, normals, colors, uvs, indices);
    }

    /// <summary>A thin stem with a small flat six-petal head; COLOR.r is 1 on the head, 0 on the stem.</summary>
    private static ArrayMesh FlowerMesh()
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        const float height = 0.38f;
        foreach (var (a, b) in new[] { (new Vector3(-0.012f, 0, 0), new Vector3(0.012f, 0, 0)), (new Vector3(0, 0, -0.012f), new Vector3(0, 0, 0.012f)) })
        {
            int start = vertices.Count;
            foreach (var v in new[] { a, b, a + Vector3.Up * height, b + Vector3.Up * height })
            {
                vertices.Add(v);
                normals.Add(Vector3.Up);
                colors.Add(new Color(0, 0, 0));
                uvs.Add(new Vector2(0, v.Y / height));
            }

            indices.AddRange(new[] { start, start + 2, start + 1, start + 1, start + 2, start + 3 });
        }

        int center = vertices.Count;
        vertices.Add(new Vector3(0, height + 0.01f, 0));
        normals.Add(Vector3.Up);
        colors.Add(new Color(1, 0, 0));
        uvs.Add(new Vector2(0, 1));
        const int petals = 6;
        for (int i = 0; i < petals * 2; i++)
        {
            float angle = i * Mathf.Tau / (petals * 2);
            float radius = i % 2 == 0 ? 0.06f : 0.025f;
            vertices.Add(new Vector3(Mathf.Cos(angle) * radius, height, Mathf.Sin(angle) * radius));
            normals.Add(Vector3.Up);
            colors.Add(new Color(1, 0, 0));
            uvs.Add(new Vector2(0, 1));
        }

        for (int i = 0; i < petals * 2; i++)
            indices.AddRange(new[] { center, center + 1 + i, center + 1 + (i + 1) % (petals * 2) });
        return ProceduralMesh.Build(vertices, normals, colors, uvs, indices);
    }
}
