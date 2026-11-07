using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace PcgCitySandbox.Pcg;

public static class CityVisualValidation
{
    public static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("CITY_VISUAL_FAILED: " + message); }
    public static string Fingerprint(ScenePlacementSet data)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        void Point(Vector2 p) { w.Write(p.X); w.Write(p.Y); }
        void Shape(ParcelRegion p) { w.Write(p.Area); foreach (var ring in p.Rings) { w.Write(ring.Count); foreach (var v in ring) Point(v); } }
        foreach (var road in data.Roads.Edges) { w.Write(road.Id); w.Write(road.Width); foreach (var p in road.Points) Point(p); }
        Shape(data.Roads.Geometry.Area);
        foreach (var p in data.Parcels.Parcels) { w.Write(p.Id); Shape(p.Shape); Shape(p.Buildable); if (p.Yard != null) Shape(p.Yard); }
        foreach (var b in data.Buildings.Buildings) { w.Write(b.Id); Shape(b.Footprint); Shape(b.RoofProjection); Point(b.Door); w.Write(b.BaseY); w.Write(b.WallHeight); w.Write(b.RoofHeight); w.Write(b.Floors); }
        foreach (var p in data.Props) { w.Write(p.Id); Shape(p.Footprint); Shape(p.Approach); w.Write(p.BaseY); w.Write(p.Height); }
        foreach (var p in data.ProtectedSpaces) Shape(p.Shape);
        if (data.Roads.Settlement.Town is { } town) { Shape(town.WallFootprint); foreach (var gate in town.Gates) { Point(gate.Position); w.Write(gate.Width); Shape(gate.Passage); } }
        w.Flush(); return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    public static object Geometry(CityPresentation view)
    {
        var context = view.Context ?? throw new InvalidOperationException("Missing city context"); var map = context.Map;var terrain=context.Terrain;
        var data = context.Placement ?? throw new InvalidOperationException("Missing full city");
        var buildings = data.Buildings.Buildings.ToDictionary(b => b.Id); var groundAreas = new Dictionary<string, double>();
        var heightCache=new Dictionary<Vector2,float>();float Height(Vector2 p){if(!heightCache.TryGetValue(p,out float h))heightCache[p]=h=terrain.HeightAt(p);return h;}
        double maxGroundError = 0, maxPlaneError = 0;Vector3 worstAt=Vector3.Zero;string worstOwner="";
        for (int i = 0; i < view.Ground.Positions.Count; i += 3)
        {
            var a = view.Ground.Positions[i]; var b = view.Ground.Positions[i + 1]; var c = view.Ground.Positions[i + 2]; var center = (a + b + c) / 3;
            string owner = view.Ground.Owners[i]; float lift = CityGroundMesh.Lift + (owner == "road_shoulders" ? .004f : 0);
            foreach (var p in new[] { a, b, c }) maxGroundError = Math.Max(maxGroundError, Math.Abs(p.Y - Height(new(p.X, p.Z)) - lift));
            double error=Math.Abs(center.Y-Height(new(center.X,center.Z))-lift);if(error>maxPlaneError){maxPlaneError=error;worstAt=center;worstOwner=owner;}
            groundAreas[owner] = groundAreas.GetValueOrDefault(owner) + Math.Abs((b - a).Cross(c - a).Y) * .5;
        }
        Require(maxGroundError < .002 && maxPlaneError < .003, $"terrain triangle fit: vertices={maxGroundError} centers={maxPlaneError} at={worstAt} owner={worstOwner}");
        foreach (var patch in data.GroundPatches.Where(p=>context.Grading==null||p.RoadId==null&&p.Kind!="广场")) Require(Math.Abs(groundAreas.GetValueOrDefault($"ground/{patch.Id}") - patch.Shape.Area) < Math.Max(.05, patch.Shape.Area * .00003), $"ground patch area conserved: {patch.Id}");
        var safeFoot = buildings.ToDictionary(p => p.Key, p => ParcelGeometry.Offset(p.Value.Footprint.CopyPaths(), .025f));
        var safeRoof = buildings.ToDictionary(p => p.Key, p => ParcelGeometry.Offset(p.Value.RoofProjection.CopyPaths(), .01f));
        for (int i = 0; i < view.DetailData.Geometry.Positions.Count; i++)
        {
            var mesh = view.DetailData.Geometry; int id = int.Parse(mesh.Owners[i][1..]); var b = buildings[id]; var p = mesh.Positions[i];
            Require(p.IsFinite() && mesh.Normals[i].IsFinite() && Math.Abs(mesh.Normals[i].Length() - 1) < .002f, "finite detail positions and unit normals");
            bool roof = mesh.Parts[i] == "roof_detail";
            Require(ParcelGeometry.Contains(roof ? safeRoof[id] : safeFoot[id], new(p.X, p.Z)), $"detail envelope B{id} {mesh.Parts[i]}");
            Require(p.Y <= b.BaseY + b.WallHeight + (roof ? b.RoofHeight : 0) + .001f, "detail height does not add storeys or increase ridge");
        }
        foreach (var opening in view.DetailData.Openings)
        {
            var b = buildings[opening.BuildingId]; Require(opening.Bottom >= b.BaseY - .001f && opening.Top <= b.BaseY + b.WallHeight + .001f, "openings within real storeys");
            if (opening.Kind == "door") Require(opening.Center.DistanceTo(b.Door) < .001f, "door position inherited");
            else foreach (var wall in data.Buildings.SharedWalls.Where(w => w.A == b.Id || w.B == b.Id))
            {
                var a = b.Corners[opening.Edge]; var tangent = (b.Corners[(opening.Edge + 1) % 4] - a).Normalized();
                if (Math.Abs((wall.Start - a).Cross(tangent)) > .02f || Math.Abs((wall.End - a).Cross(tangent)) > .02f) continue;
                float center = (opening.Center - a).Dot(tangent), lo = Math.Min((wall.Start - a).Dot(tangent), (wall.End - a).Dot(tangent)), hi = Math.Max((wall.Start - a).Dot(tangent), (wall.End - a).Dot(tangent));
                Require(center + opening.Width / 2 <= lo || center - opening.Width / 2 >= hi, "no windows in shared walls");
            }
        }
        var town = context.Layout.Town!; var safeWall = ParcelGeometry.Offset(town.WallFootprint.CopyPaths(), .002f);
        for (int i = 0; i < view.Walls.Positions.Count; i++)
        {
            var p = view.Walls.Positions[i]; Require(ParcelGeometry.Contains(safeWall, new(p.X, p.Z)), "wall decoration retains original footprint");
            Require(p.Y - terrain.HeightAt(new(p.X, p.Z)) <= view.Settings.WallHeight + .002f, "wall stays inside illustrative height");
        }
        foreach (var gate in town.Gates)
        {
            double intrusion = 0;
            for (int i = 0; i < view.Walls.Positions.Count; i += 3)
            {
                var a = view.Walls.Positions[i]; var b = view.Walls.Positions[i + 1]; var c = view.Walls.Positions[i + 2];
                if (Math.Abs((b - a).Cross(c - a).Y) < 1e-6) continue;
                var tri = new Paths64 { ParcelGeometry.Path(new[] { new Vector2(a.X,a.Z), new Vector2(b.X,b.Z), new Vector2(c.X,c.Z) }) };
                intrusion += ParcelGeometry.Area(ParcelGeometry.Intersection(tri, gate.Passage.CopyPaths()));
            }
            Require(intrusion < .01, $"gate {gate.Id} passage not narrowed by wall geometry");
        }
        return new { passed = true, seed = map.Settings.Seed, groundTriangles = view.Ground.Triangles, buildingTriangles = view.Bodies.Triangles, detailTriangles = view.DetailData.Geometry.Triangles, wallTriangles = view.Walls.Triangles, windows = view.DetailData.Openings.Count(o => o.Kind == "window"), doors = view.DetailData.Openings.Count(o => o.Kind == "door"), maxGroundError, maxPlaneError, gates = town.Gates.Count };
    }
}
