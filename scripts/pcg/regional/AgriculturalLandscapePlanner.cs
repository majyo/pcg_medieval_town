using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PcgCitySandbox.Pcg;

// A field is a coherent cultivation group. Its internal lines are cultivation marks,
// not individually fenced ownership parcels.
public static class AgriculturalLandscapePlanner
{
    public const float FieldSeparation = 12;
    private sealed record Candidate(Vector2 Center, Vector2 RoadDirection, uint Key);

    public static ReadOnlyCollection<ReadOnlyCollection<Vector2>> ClipLines(
        IEnumerable<ReadOnlyCollection<Vector2>> lines, Paths64 area)
    {
        var source = new Paths64(lines.Where(p => p.Count > 1)
            .Select(p => ParcelGeometry.Path(p, positive: false)));
        if (source.Count == 0 || area.Count == 0)
            return Array.AsReadOnly(Array.Empty<ReadOnlyCollection<Vector2>>());

        var clipper = new Clipper64();
        clipper.AddOpenSubject(source);
        clipper.AddClip(area);
        var clipped = new Paths64();
        if (!clipper.Execute(ClipType.Intersection, FillRule.NonZero, new Paths64(), clipped))
            throw new InvalidOperationException("耕作线裁切失败");
        return Array.AsReadOnly(clipped.Where(p => p.Count > 1)
            .Select(p => Array.AsReadOnly(p.Select(ParcelGeometry.V).ToArray()))
            .Where(p => Enumerable.Range(1, p.Count - 1)
                .Sum(i => p[i].DistanceTo(p[i - 1])) > 5).ToArray());
    }

    public static async Task<List<LandscapeParcel>> Generate(
        CityEnvironmentData environment, SettlementOptions options, Vector2 core,
        Paths64 land, Paths64 development, Paths64 reserved,
        IReadOnlyList<RegionalLink> links, HashSet<int> reachable,
        Func<Vector2, Vector2, Task<List<Vector2>>> route,
        Func<float, string, Task> progress)
    {
        uint Hash(int value)
        {
            unchecked
            {
                uint h = (uint)(value ^ options.Seed ^ 0x17435);
                h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15;
                return h;
            }
        }
        float Unit(uint h, int shift) => ((h >> shift) & 1023) / 1023f;
        bool Reachable(Vector2 p)
        {
            if (!environment.Contains(p)) return false;
            int x = Math.Clamp((int)((p.X - environment.Bounds.Position.X) / environment.StepX), 0, environment.CellsX - 1);
            int z = Math.Clamp((int)((p.Y - environment.Bounds.Position.Y) / environment.StepZ), 0, environment.CellsZ - 1);
            return reachable.Contains(z * environment.CellsX + x);
        }

        var roadReserve = ParcelGeometry.Union(new Paths64(links.SelectMany(link =>
            link.Points.Skip(1).Select((p, i) => ParcelGeometry.Path(
                RoadSpace.Strip(link.Points[i], p, 12))))));
        var excluded = ParcelGeometry.Union(new Paths64(
            ParcelGeometry.Offset(development, 9).Concat(reserved).Concat(roadReserve)));
        var available = ParcelGeometry.Difference(land, excluded);
        var candidates = new List<Candidate>();

        // Stratify along actual routes, with unequal intervals and lateral offsets.
        // No world-axis raster is used to place the fields.
        for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
        {
            var points = links[linkIndex].Points;
            var cumulative = new float[points.Count];
            for (int i = 1; i < points.Count; i++)
                cumulative[i] = cumulative[i - 1] + points[i].DistanceTo(points[i - 1]);
            if (points.Count < 2 || cumulative[^1] < 60) continue;
            Vector2 At(float distance)
            {
                distance = Math.Clamp(distance, 0, cumulative[^1]);
                int i = Array.FindIndex(cumulative, value => value >= distance);
                if (i <= 0) return points[0];
                return points[i - 1].Lerp(points[i], (distance - cumulative[i - 1]) /
                    Math.Max(.001f, cumulative[i] - cumulative[i - 1]));
            }
            int sample = 0;
            for (float distance = 60 + Unit(Hash(linkIndex), 0) * 80;
                 distance < cumulative[^1] && sample < 30;
                 distance += 130 + Unit(Hash(linkIndex * 97 + sample++), 10) * 100)
            {
                var along = At(distance);
                if (along.DistanceTo(core) > options.CoreRadius + options.SuburbLength + 270) continue;
                var tangent = (At(distance + 45) - At(distance - 45)).Normalized();
                if (tangent.LengthSquared() < .5f) continue;
                foreach (int side in new[] { -1, 1 })
                {
                    uint key = Hash(linkIndex * 7879 + sample * 73 + side * 31);
                    var center = along + new Vector2(-tangent.Y, tangent.X) * side *
                        (105 + Unit(key, 0) * 150) + tangent * ((Unit(key, 10) - .5f) * 90);
                    if (Reachable(center) && ParcelGeometry.Contains(available, center))
                        candidates.Add(new(center, tangent, key));
                }
            }
        }

        var fields = new List<LandscapeParcel>();
        var occupied = new Paths64();
        int attempted = 0;
        foreach (var candidate in candidates.OrderBy(c => c.Key).Take(180))
        {
            if (fields.Count >= 14) break;
            if (++attempted % 8 == 0)
                await progress((float)attempted / Math.Max(1, candidates.Count), "整理田组与耕作方向");

            var center = candidate.Center;
            if (!ParcelGeometry.Contains(available, center) || ParcelGeometry.Contains(occupied, center)) continue;
            float nearDevelopment = links.SelectMany(link => link.Points)
                .Min(p => p.DistanceTo(center));
            bool nearWater = false;
            for (int z = -3; z <= 3; z++)
            for (int x = -3; x <= 3; x++)
                if (environment.TryGetCell(center + new Vector2(x * 15, z * 15), out var cell) && cell.Water == true)
                    nearWater = true;

            float Gradient(Vector2 delta) => environment.TryHeightAt(center + delta, out float a) &&
                environment.TryHeightAt(center - delta, out float b) ? (a - b) / (2 * delta.Length()) : 0;
            var gradient = new Vector2(Gradient(new(25, 0)), Gradient(new(0, 25)));
            var direction = candidate.RoadDirection;
            if (gradient.Length() > .025f)
            {
                var contour = new Vector2(-gradient.Y, gradient.X).Normalized();
                if (contour.Dot(direction) < 0) contour = -contour;
                direction = (direction * .45f + contour * .55f).Normalized();
            }
            else if (candidate.Key % 3 == 0)
                direction = new Vector2(-direction.Y, direction.X);
            direction = direction.Rotated((Unit(candidate.Key, 20) - .5f) * .20f);
            var normal = new Vector2(-direction.Y, direction.X);

            string pattern = nearWater ? "pasture" : nearDevelopment < 180 && candidate.Key % 4 == 0 ? "orchard" : "arable";
            float length = pattern == "orchard" ? 100 + Unit(candidate.Key, 0) * 80 : 210 + Unit(candidate.Key, 0) * 140;
            float width = pattern == "orchard" ? 55 + Unit(candidate.Key, 10) * 45 : 75 + Unit(candidate.Key, 10) * 65;
            float bend = pattern == "arable" ? 4 + Unit(candidate.Key, 20) * 8 : 2;
            bool reverse = candidate.Key % 2 == 0;
            Vector2 ShapePoint(float t, float across)
            {
                float curve = MathF.Sin(t * (reverse ? Mathf.Tau : Mathf.Pi)) * bend;
                float taper = Mathf.Lerp(.8f + Unit(candidate.Key, 0) * .25f, .75f + Unit(candidate.Key, 10) * .3f, t);
                return center + direction * ((t - .5f) * length) + normal * (curve + across * width * taper);
            }
            var perimeter = Enumerable.Range(0, 13).Select(i => ShapePoint(i / 12f, -.5f))
                .Concat(Enumerable.Range(0, 13).Reverse().Select(i => ShapePoint(i / 12f, .5f)));
            var envelope = new Paths64 { ParcelGeometry.Path(perimeter) };
            var pieces = ParcelGeometry.Components(ParcelGeometry.Intersection(envelope, available));
            var selected = pieces.Where(p => ParcelGeometry.Contains(p, center))
                .OrderByDescending(ParcelGeometry.Area).FirstOrDefault();
            if (selected == null || ParcelGeometry.Area(selected) < Math.Max(1800, ParcelGeometry.Area(envelope) * .42)) continue;
            // Competing field groups must be relocated, rather than cutting a notch
            // around an existing orchard or field and making them appear superimposed.
            if (ParcelGeometry.Area(ParcelGeometry.Intersection(selected, occupied)) > .001) continue;

            var shape = new ParcelRegion(selected);
            var triangles = shape.Triangles;
            int triangle = Enumerable.Range(0, triangles.Count / 3).OrderByDescending(i =>
                Math.Abs((triangles[i * 3 + 1] - triangles[i * 3]).Cross(triangles[i * 3 + 2] - triangles[i * 3]))).First() * 3;
            var fieldPoint = (triangles[triangle] + triangles[triangle + 1] + triangles[triangle + 2]) / 3;
            var access = links.SelectMany(link => link.Points).OrderBy(p => p.DistanceSquaredTo(fieldPoint)).First();
            if (access.DistanceTo(fieldPoint) > 370) continue;
            List<Vector2> accessRoute;
            try { accessRoute = await route(fieldPoint, access); }
            catch (InvalidOperationException) { continue; }
            if (accessRoute.Count < 2) continue;

            var marks = new List<ReadOnlyCollection<Vector2>>();
            if (pattern != "pasture")
            {
                int count = Math.Max(4, (int)(width / (pattern == "orchard" ? 13 : 8)));
                for (int strip = 1; strip < count; strip++)
                    marks.Add(Array.AsReadOnly(Enumerable.Range(0, 29)
                        .Select(i => ShapePoint(.03f + .94f * i / 28, -.5f + (float)strip / count)).ToArray()));
            }
            var lines = ClipLines(marks, selected);
            fields.Add(new($"field_{fields.Count}", pattern == "arable" ? "耕作田组" : pattern == "orchard" ? "果园" : "草地",
                shape, access, Array.AsReadOnly(accessRoute.ToArray()), pattern, direction, lines));
            // The base available land stays unchanged: only terrain, roads and town
            // reservations may crop a field. Other fields impose whole-group clearance.
            occupied = ParcelGeometry.Union(new Paths64(occupied.Concat(
                ParcelGeometry.Offset(selected, FieldSeparation))));
        }
        await progress(1, "田园形态检查完成");
        return fields;
    }
}
