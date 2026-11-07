using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

public sealed record FacadeOpening(int BuildingId, int Edge, Vector2 Center, float Width, float Bottom, float Top, string Kind);
public sealed class BuildingDetailResult
{
    public CityGeometry Geometry { get; } = new();
    public List<FacadeOpening> Openings { get; } = new();
}

public static class BuildingDetailBuilder
{
    // Facade relief is limited to 2 cm (render skin); source footprints and clearances never change.
    public const float Skin = .02f;
    public static BuildingDetailResult Build(BuildingSet data, TemperateMapData map, ITerrainSurface? terrain = null)
    {
        var result = new BuildingDetailResult(); var mesh = result.Geometry;
        foreach (var b in data.Buildings)
        {
            string owner = $"B{b.Id}"; int role = CityPalette.Role(b.Program);
            Color trim = b.PrecinctId != null ? new("aaa894") : new("89785f"), glass = b.PrecinctId != null ? new("485c60") : new("505b51");
            float top = b.BaseY + b.WallHeight, floorHeight = b.WallHeight / b.Floors;
            for (int edge = 0; edge < 4; edge++)
            {
                var a = b.Corners[edge]; var z = b.Corners[(edge + 1) % 4]; var direction = (z - a).Normalized();
                var outward = new Vector2(-direction.Y, direction.X); if (b.Footprint.Contains((a + z) * .5f + outward * .05f)) outward = -outward;
                var n = new Vector3(outward.X, 0, outward.Y); float length = a.DistanceTo(z);
                var shared = data.SharedWalls.Where(w => (w.A == b.Id || w.B == b.Id) && Math.Abs((w.Start - a).Cross(direction)) < .02f && Math.Abs((w.End - a).Cross(direction)) < .02f).ToArray();
                bool IsShared(float center, float width) => shared.Any(w => center + width * .5f > Math.Min((w.Start - a).Dot(direction), (w.End - a).Dot(direction)) - .03f && center - width * .5f < Math.Max((w.Start - a).Dot(direction), (w.End - a).Dot(direction)) + .03f);
                Vector3 P(float x, float y, float relief = Skin) { var p = a + direction * x + outward * relief; return new(p.X, y, p.Y); }
                void Panel(float x, float y, float width, float height, Color color, string part = "facade_detail", float relief = .012f)
                {
                    if (width <= .01f || height <= .01f || x < -.001f || x + width > length + .001f || y + height > top + .001f) return;
                    mesh.Quad(P(x, y, relief), P(x + width, y, relief), P(x + width, y + height, relief), P(x, y + height, relief), n, color, owner, part, role);
                }
                if (shared.Length == 0)
                {
                    int pieces = Math.Max(1, (int)MathF.Ceiling(length / 1.5f));
                    for (int i = 0; i < pieces; i++)
                    {
                        float x0 = length * i / pieces, x1 = length * (i + 1) / pieces;
                        float y0 = (terrain??data.Terrain).HeightAt(a + direction * x0) - .025f, y1 = (terrain??data.Terrain).HeightAt(a + direction * x1) - .025f;
                        float cap = b.BaseY + .18f;
                        if (cap > Math.Max(y0, y1)) mesh.Quad(P(x0, y0, .009f), P(x1, y1, .009f), P(x1, cap, .009f), P(x0, cap, .009f), n, new("8d8979"), owner, "foundation_detail", role);
                    }
                    if (!b.OpenHall) for (int floor = 1; floor < b.Floors; floor++) Panel(.08f, b.BaseY + floor * floorHeight - .035f, length - .16f, .07f, trim.Lightened(.12f));
                }
                bool front = BuildingGeometry.DistanceToSegment(b.Door, a, z) < .03f;
                float doorX = (b.Door - a).Dot(direction), doorHalf = Math.Min(.5f, b.Corners[0].DistanceTo(b.Corners[1]) * .2f);
                float doorTop = b.BaseY + Math.Min(2.1f, b.WallHeight * .8f);
                if (front && !IsShared(doorX, doorHalf * 2 + .2f))
                {
                    Panel(doorX - doorHalf - .09f, b.BaseY, .09f, doorTop - b.BaseY + .1f, trim);
                    Panel(doorX + doorHalf, b.BaseY, .09f, doorTop - b.BaseY + .1f, trim);
                    Panel(doorX - doorHalf, doorTop, doorHalf * 2, .1f, trim);
                    Panel(doorX - .025f, b.BaseY + .08f, .05f, doorTop - b.BaseY - .12f, new("514a3c"), relief: .018f);
                    result.Openings.Add(new(b.Id, edge, b.Door, doorHalf * 2, b.BaseY, doorTop, "door"));
                }
                int columns = Math.Clamp((int)MathF.Floor((length - .8f) / 2.5f), 1, 8);
                for (int floor = 0; floor < (b.OpenHall ? 1 : b.Floors); floor++) for (int column = 0; column < columns; column++)
                {
                    float center = (column + .5f) * length / columns;
                    float width = Math.Min(b.PrecinctId != null ? .95f : floor == 0 && b.Program == "商住" ? 1.15f : .72f, length / columns * .45f);
                    float height = b.OpenHall ? Math.Min(3.1f, b.WallHeight * .42f) : Math.Min(1.25f, floorHeight * .48f);
                    float bottom = b.BaseY + (b.OpenHall ? b.WallHeight * .38f : floor * floorHeight + floorHeight * .34f);
                    if (b.Program == "教堂塔楼" && floor == b.Floors - 1) { height = Math.Min(1.8f, floorHeight * .66f); bottom = b.BaseY + floor * floorHeight + .5f; }
                    float border = b.PrecinctId != null ? .12f : .075f;
                    if (IsShared(center, width + border * 2) || center - width / 2 - border < .15f || center + width / 2 + border > length - .15f) continue;
                    if (front && floor == 0 && Math.Abs(center - doorX) < width * .5f + doorHalf + .3f) continue;
                    if (bottom + height + border > top - .18f) continue;
                    Panel(center - width * .5f - border, bottom - border, width + border * 2, height + border * 2, trim);
                    Panel(center - width * .5f, bottom, width, height, glass, relief: .016f);
                    Panel(center - .02f, bottom, .04f, height, trim.Lightened(.08f), relief: Skin);
                    if (height > 1.6f) Panel(center - width * .5f, bottom + height * .58f, width, .05f, trim, relief: Skin);
                    result.Openings.Add(new(b.Id, edge, a + direction * center, width, bottom, bottom + height, "window"));
                }
            }
            var tangent = (b.Corners[1] - b.Corners[0]).Normalized(); var inward = b.Inward;
            float e = b.RoofProjection.Area - b.Footprint.Area > .001 ? .25f : 0;
            var corners = BuildingGeometry.Rectangle(b.Corners[0] - (tangent + inward) * e, tangent, inward,
                b.Corners[0].DistanceTo(b.Corners[1]) + e * 2, b.Corners[0].DistanceTo(b.Corners[3]) + e * 2);
            Vector3 Roof(Vector2 p, float y) => new(p.X, y, p.Y);
            Color fascia = new("655f52");
            foreach (int edge in b.RidgeAlongDepth ? new[] { 1, 3 } : new[] { 0, 2 })
            {
                var a = corners[edge]; var z = corners[(edge + 1) % 4]; var dir = (z - a).Normalized(); var n = new Vector3(-dir.Y, 0, dir.X);
                if (b.Footprint.Contains((a + z) * .5f + new Vector2(n.X, n.Z) * .3f)) n = -n;
                var skin = new Vector2(n.X, n.Z) * .006f; a += skin; z += skin;
                mesh.Quad(Roof(a, top - .16f), Roof(z, top - .16f), Roof(z, top), Roof(a, top), n, fascia, owner, "roof_detail", role);
            }
            foreach (int edge in b.RidgeAlongDepth ? new[] { 0, 2 } : new[] { 1, 3 })
            {
                var a = corners[edge]; var z = corners[(edge + 1) % 4]; var mid = (a + z) * .5f; var dir = (z - a).Normalized(); var n = new Vector3(-dir.Y, 0, dir.X);
                if (b.Footprint.Contains(mid + new Vector2(n.X, n.Z) * .3f)) n = -n;
                var skin = new Vector2(n.X, n.Z) * .006f; a += skin; z += skin; mid += skin;
                float ridge = top + b.RoofHeight;
                mesh.Quad(Roof(a, top - .12f), Roof(mid, ridge - .12f), Roof(mid, ridge), Roof(a, top), n, fascia, owner, "roof_detail", role);
                mesh.Quad(Roof(mid, ridge - .12f), Roof(z, top - .12f), Roof(z, top), Roof(mid, ridge), n, fascia, owner, "roof_detail", role);
            }
        }
        return result;
    }
}
