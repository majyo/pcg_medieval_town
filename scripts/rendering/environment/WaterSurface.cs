using System.Collections.Generic;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// The river surface: one continuous strip along the centerline at the water height, wide enough to run under the
/// banks so the shoreline is where the ground rises through it. Vertices on both sides share an averaged
/// direction, so there are no gaps at bends. UV.x is the signed distance from the centerline in meters and UV.y the
/// distance downstream, which the water shader uses for flow.
/// </summary>
public partial class WaterSurface : MeshInstance3D
{
    private const string ShaderPath = "res://shaders/water.gdshader";

    public void Build(TemperateMapData map)
    {
        var river = map.River;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        float along = 0;
        for (int i = 0; i < river.Count; i++)
        {
            var point = river[i];
            var previous = river[System.Math.Max(0, i - 1)].Position;
            var next = river[System.Math.Min(river.Count - 1, i + 1)].Position;
            var direction = (next - previous).Normalized();
            var side = direction.Orthogonal();
            float half = point.HalfWidth * TemperateMapData.WaterSurfaceWidthFactor;
            if (i > 0)
                along += point.Position.DistanceTo(river[i - 1].Position);
            // Run the ends a little past the map edge.
            var center = point.Position + direction * (i == 0 ? -20 : i == river.Count - 1 ? 20 : 0);
            foreach (float s in new[] { -1f, 1f })
            {
                var p = center + side * half * s;
                vertices.Add(new Vector3(p.X, point.WaterHeight, p.Y));
                normals.Add(Vector3.Up);
                colors.Add(Colors.White);
                uvs.Add(new Vector2(half * s, along));
            }

            if (i == 0)
                continue;
            int a = 2 * (i - 1), b = a + 1, c = a + 2, d = a + 3;
            indices.AddRange(new[] { a, c, b, b, c, d });
        }

        Mesh = ProceduralMesh.Build(vertices, normals, colors, uvs, indices);
        MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        CastShadow = ShadowCastingSetting.Off;
    }
}
