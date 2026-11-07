using Godot;
using Clipper2Lib;
using System;
using System.Linq;

namespace PcgCitySandbox.Pcg;

public static class FortificationPreviewBuilder
{
    public static CityGeometry Build(SettlementLayout layout, TemperateMapData map, float height, ITerrainSurface? terrain = null)
    {
        terrain??=new NativeTerrainSurface(map); var output = new CityGeometry(); var town = layout.Town; if (town == null || town.WallFootprint.Area < .001) return output;
        void Extrude(ParcelRegion shape, float bottom, float top, string owner, Color color)
        {
            var cap = CityGroundMesh.Build(map, new[] { (owner, "wall", shape) },terrain);
            for (int i = 0; i < cap.Positions.Count; i += 3)
            {
                Vector3 P(int index) => cap.Positions[index] + Vector3.Up * (top - CityGroundMesh.Lift);
                output.Triangle(P(i), P(i + 1), P(i + 2), cap.Normals[i], color.Lightened(.12f), owner, "wall_cap", 8);
            }
            foreach (var ring in shape.Rings) for (int edge = 0; edge < ring.Count; edge++)
            {
                var a = ring[edge]; var z = ring[(edge + 1) % ring.Count]; var d = z - a; if (d.LengthSquared() < .000001f) continue;
                var normal = new Vector2(-d.Y, d.X).Normalized(); if (shape.Contains((a + z) * .5f + normal * .03f)) normal = -normal;
                int steps = Math.Max(1, (int)MathF.Ceiling(d.Length() / 1.5f));
                for (int i = 0; i < steps; i++)
                {
                    var p = a.Lerp(z, (float)i / steps); var q = a.Lerp(z, (float)(i + 1) / steps);
                    float py = terrain.HeightAt(p), qy = terrain.HeightAt(q);
                    output.Quad(new(p.X, py + bottom, p.Y), new(q.X, qy + bottom, q.Y), new(q.X, qy + top, q.Y), new(p.X, py + top, p.Y), new(normal.X, 0, normal.Y), color, owner, "wall_body", 8);
                }
            }
        }
        Extrude(town.WallFootprint, -.04f, height * .87f, "wall", new("929486"));
        float spacing = 3.5f;
        for (int edge = 0; edge < town.WallLine.Count; edge++)
        {
            var a = town.WallLine[edge]; var z = town.WallLine[(edge + 1) % town.WallLine.Count]; float length = a.DistanceTo(z); if (length < 1) continue;
            var tangent = (z - a).Normalized(); var across = tangent.Orthogonal();
            int count = Math.Max(1, (int)MathF.Floor(length / spacing)); float width = layout.Options.WallWidth * .82f;
            for (int i = 0; i < count; i++)
            {
                var center = a.Lerp(z, (i + .5f) / count);
                var rectangle = BuildingGeometry.Polygon(BuildingGeometry.Rectangle(center - tangent * .55f - across * width / 2, tangent, across, 1.1f, width));
                var cut = ParcelGeometry.Intersection(rectangle, town.WallFootprint.CopyPaths());
                if (ParcelGeometry.Area(cut) < .05) continue;
                Extrude(new ParcelRegion(cut), height * .87f, height, "wall", new("a4a393"));
            }
        }
        return output;
    }
    public static CityGeometry LowWall(CityGeometry full, TemperateMapData map, ITerrainSurface? terrain = null)
    {
        terrain??=new NativeTerrainSurface(map); var result = new CityGeometry();
        for (int i = 0; i < full.Positions.Count; i += 3)
        {
            Vector3 Lower(int k) { var p = full.Positions[k]; float h = terrain.HeightAt(new(p.X, p.Z)); p.Y = h + (p.Y - h) * .22f; return p; }
            var a = Lower(i); var b = Lower(i + 1); var c = Lower(i + 2);
            var normal = (c - a).Cross(b - a).Normalized();
            result.Triangle(a, b, c, normal, full.Colors[i], full.Owners[i], full.Parts[i], 8);
        }
        return result;
    }
}
