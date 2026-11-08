using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// Trees from the map's placements, drawn with <see cref="TreeMeshes"/>: one MultiMesh per kind and variant.
/// Map species 0 and 1 become the round broadleaf and the birch-like tree, species 2 the conifer.
/// </summary>
public partial class Forest : Node3D
{
    private const string ShaderPath = "res://shaders/tree.gdshader";

    private IReadOnlyList<TreePlacement> _trees = Array.Empty<TreePlacement>();
    private Func<Vector2, bool>? _hidden;
    private ShaderMaterial? _material;

    public void Build(IReadOnlyList<TreePlacement> trees)
    {
        _trees = trees;
        _material ??= new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        Rebuild();
    }

    /// <summary>Hides trees where <paramref name="hidden"/> returns true (inside the town, on roads).</summary>
    public void SetMask(Func<Vector2, bool>? hidden)
    {
        _hidden = hidden;
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var child in GetChildren())
            child.QueueFree();
        var visible = _trees.Where(t => _hidden == null || !_hidden(new Vector2(t.Position.X, t.Position.Z))).ToArray();
        for (int kind = 0; kind < TreeMeshes.Kinds; kind++)
            for (int variant = 0; variant < TreeMeshes.VariantsPerKind; variant++)
            {
                var group = visible.Where(t => t.Species == kind && Variant(t) == variant).ToArray();
                if (group.Length == 0)
                    continue;
                var multimesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseColors = true,
                    Mesh = TreeMeshes.Create(kind, variant),
                    InstanceCount = group.Length,
                };
                for (int i = 0; i < group.Length; i++)
                {
                    var t = group[i];
                    var basis = new Basis(Vector3.Up, t.Rotation).Scaled(Vector3.One * t.Scale);
                    multimesh.SetInstanceTransform(i, new Transform3D(basis, t.Position));
                    multimesh.SetInstanceColor(i, new Color(t.Tint, t.Tint * (0.97f + 0.06f * (t.Scale - 0.8f)), t.Tint));
                }

                AddChild(new MultiMeshInstance3D { Multimesh = multimesh, MaterialOverride = _material });
            }
    }

    private static int Variant(TreePlacement t) =>
        (int)(ProceduralMesh.Hash((int)(t.Position.X * 13) ^ (int)(t.Position.Z * 7919)) * TreeMeshes.VariantsPerKind);
}
