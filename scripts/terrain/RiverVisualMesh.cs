using Godot;
using System;
using System.Collections.Generic;

namespace PcgCitySandbox;

public sealed class RiverVisualMesh
{
    public Vector3[] Positions { get; }
    public Vector2[] FlowCoordinates { get; }
    public Vector2[] FlowDirections { get; }
    public int[] Indices { get; }
    public const int Columns = 16;
    public RiverVisualMesh(TemperateMapData map)
    {
        int rows = (map.River.Count - 1) * 4 + 1;
        Positions = new Vector3[rows * (Columns + 1)]; FlowCoordinates = new Vector2[Positions.Length]; FlowDirections = new Vector2[Positions.Length];
        var index = new VisualRiverIndex(map); var triangles = new List<int>(); float length = 0; Vector2 previous = map.River[0].Position;
        for (int row = 0; row < rows; row++)
        {
            int station = Math.Min(row / 4, map.River.Count - 2); float t = row / 4f - station;
            var a = map.River[station]; var b = map.River[station + 1]; var center = a.Position.Lerp(b.Position, t);
            Vector2 Tangent(int n) => (map.River[Math.Min(n + 1, map.River.Count - 1)].Position - map.River[Math.Max(n - 1, 0)].Position).Normalized();
            var tangent = Tangent(station).Lerp(Tangent(station + 1), t).Normalized(); var across = new Vector2(tangent.Y, -tangent.X);
            length += center.DistanceTo(previous); previous = center;
            float width = Mathf.Lerp(a.HalfWidth, b.HalfWidth, t) + 12;
            for (int col = 0; col <= Columns; col++)
            {
                float lateral = (col / (float)Columns * 2 - 1) * width; Vector2 p = center + across * lateral;
                if (row == 0 || row == rows - 1) p.Y = center.Y;
                p = p.Clamp(Vector2.One * -map.Half, Vector2.One * map.Half);
                var river = index.Query(p); int k = row * (Columns + 1) + col;
                Positions[k] = new(p.X, river.WaterHeight + .015f, p.Y);
                FlowCoordinates[k] = new(lateral, length); FlowDirections[k] = tangent;
            }
        }
        void Triangle(int a, int b, int c)
        {
            // Clipping a row to the map edge may collapse its XZ area while its sampled Y differs.
            // Such vertical slivers are not a water surface and must not be emitted.
            if (Math.Abs((Positions[b] - Positions[a]).Cross(Positions[c] - Positions[a]).Y) < 1e-6f) return;
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
        }
        for (int row = 0; row < rows - 1; row++) for (int col = 0; col < Columns; col++)
        {
            int a = row * (Columns + 1) + col, b = a + 1, c = a + Columns + 1, d = c + 1;
            Triangle(a, b, c); Triangle(b, d, c);
        }
        Indices = triangles.ToArray();
    }
    public ArrayMesh ToMesh()
    {
        var normals = new Vector3[Positions.Length]; Array.Fill(normals, Vector3.Up);
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Positions; arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = FlowCoordinates; arrays[(int)Mesh.ArrayType.TexUV2] = FlowDirections;
        arrays[(int)Mesh.ArrayType.Index] = Indices;
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }
}
