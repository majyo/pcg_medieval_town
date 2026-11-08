using System;
using System.Collections.Generic;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// Boulders and stones: mostly on steep ground and along the river bank, a few scattered elsewhere. Placement is
/// seeded by the map, so it is the same every time; a few lumpy rock shapes are shared by all instances.
/// </summary>
public partial class Rocks : Node3D
{
    private const string ShaderPath = "res://shaders/rock.gdshader";
    private const int Shapes = 4;

    private readonly List<(Vector3 Position, float Scale, float Rotation, int Shape)> _placements = new();
    private ShaderMaterial? _material;

    public void Build(TemperateMapData map)
    {
        _placements.Clear();
        var rng = new Random(map.Settings.Seed ^ 0x5A17);
        for (int i = 0; i < 40000; i++)
        {
            var p = new Vector2((float)(rng.NextDouble() * 2 - 1) * (map.Half - 5), (float)(rng.NextDouble() * 2 - 1) * (map.Half - 5));
            double roll = rng.NextDouble(), size = rng.NextDouble(), turn = rng.NextDouble(), shape = rng.NextDouble();
            var sample = map.Sample(p);
            if (sample.IsWater)
                continue;
            var river = map.NearestRiver(p, 80);
            float aboveWater = float.IsPositiveInfinity(river.Distance) ? 99 : sample.Height - river.WaterHeight;
            float chance = 0.004f + Mathf.SmoothStep(10, 20, sample.SlopeDegrees) * 0.12f
                + (aboveWater is > 0.1f and < 1.6f ? 0.06f : 0) + sample.Forest * 0.01f;
            if (roll > chance)
                continue;
            bool bank = aboveWater < 1.6f;
            float scale = bank ? 0.25f + (float)size * 0.5f : 0.4f + (float)Math.Pow(size, 3) * 1.6f;
            // Sink each rock a little so it sits in the ground rather than on it.
            _placements.Add((new Vector3(p.X, sample.Height - scale * 0.3f, p.Y), scale, (float)turn * Mathf.Tau,
                (int)(shape * Shapes)));
        }

        _material ??= new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        SetMask(null);
    }

    /// <summary>Leaves out rocks where <paramref name="hidden"/> returns true (inside the town, on roads).</summary>
    public void SetMask(Func<Vector2, bool>? hidden)
    {
        foreach (var child in GetChildren())
            child.QueueFree();
        for (int shape = 0; shape < Shapes; shape++)
        {
            var group = _placements.FindAll(r => r.Shape == shape && (hidden == null || !hidden(new Vector2(r.Position.X, r.Position.Z))));
            if (group.Count == 0)
                continue;
            var multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = RockMesh(shape),
                InstanceCount = group.Count,
            };
            for (int i = 0; i < group.Count; i++)
            {
                var r = group[i];
                var basis = new Basis(Vector3.Up, r.Rotation).Scaled(Vector3.One * r.Scale);
                multimesh.SetInstanceTransform(i, new Transform3D(basis, r.Position));
            }

            AddChild(new MultiMeshInstance3D { Multimesh = multimesh, MaterialOverride = _material });
        }
    }

    /// <summary>A flattened, lumpy icosphere about one meter across.</summary>
    private static ArrayMesh RockMesh(int shape)
    {
        var (sphere, triangles) = ProceduralMesh.Icosphere(2);
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var uvs = new List<Vector2>();
        var stretch = new Vector3(0.6f + ProceduralMesh.Hash(shape * 3 + 1) * 0.3f, 0.38f + ProceduralMesh.Hash(shape * 3 + 2) * 0.2f,
            0.5f + ProceduralMesh.Hash(shape * 3 + 3) * 0.3f);
        foreach (var d in sphere)
        {
            // Two scales of lumps, then flatten the bottom slightly.
            float lump = 1 + (Mathf.Sin(d.X * 5.1f + shape) * Mathf.Sin(d.Y * 4.3f) * Mathf.Sin(d.Z * 6.7f - shape) * 0.18f)
                + Mathf.Sin(d.X * 13f + d.Z * 11f + shape * 2) * 0.04f;
            var v = new Vector3(d.X * stretch.X, d.Y * stretch.Y, d.Z * stretch.Z) * lump;
            if (v.Y < -0.1f)
                v.Y = -0.1f + (v.Y + 0.1f) * 0.3f;
            vertices.Add(v);
            normals.Add(d);
            float shade = 0.85f + ProceduralMesh.Hash(vertices.Count * 7 + shape) * 0.15f;
            colors.Add(new Color(0.56f * shade, 0.55f * shade, 0.51f * shade));
            uvs.Add(Vector2.Zero);
        }

        return ProceduralMesh.Build(vertices, normals, colors, uvs, triangles);
    }
}
