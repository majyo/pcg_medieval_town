using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

public partial class CityPresentation
{
    private MeshInstance3D? _overlayNode;
    private ArrayMesh? _overlayMesh;
    private string? _hover;
    private readonly List<(Label3D Label, Vector3 Anchor, int Priority)> _labels = new();
    private Node3D _labelRoot = null!;
    private int _labelCameraHash;
    public void Hover(string? id) { if (_hover == id) return; _hover = id; _overlayKey = ""; }
    private void UpdateOverlays(Camera3D camera)
    {
        if (Context == null) return;
        int bucket = (int)MathF.Round(MathF.Log2(camera.Size) * 4);
        string key = $"{Mode}/{Theme}/{Selected}/{_hover}/{bucket}/{Context.Stale}/{Roofs}/{WallDisplay}";
        if (key == _overlayKey) return; _overlayKey = key;
        var geometry = new CityGeometry(); var map = Context.Map;
        float width = MathF.Pow(2, bucket / 4f) / Math.Max(1, GetViewport().GetVisibleRect().Size.Y) * 1.3f;
        Vector3 P(Vector2 p, float? height = null) => new(p.X, height ?? Context!.Terrain.HeightAt(p) + .065f, p.Y);
        void Line(Vector2 a, Vector2 b, Color color, float thickness = 1, float? height = null, bool dashed = false)
        {
            var delta = b - a; if (delta.LengthSquared() < .000001f) return;
            var side = delta.Normalized().Orthogonal() * width * thickness * .5f;
            int segments = Math.Max(1, (int)MathF.Ceiling(delta.Length() / (dashed ? 4 : 3)));
            for (int i = 0; i < segments; i++)
            {
                if (dashed && i % 2 == 1) continue; var x = a.Lerp(b, (float)i / segments); var y = a.Lerp(b, (float)(i + 1) / segments);
                geometry.Quad(P(x - side, height), P(x + side, height), P(y + side, height), P(y - side, height), Vector3.Up, color, "overlay", "overlay");
            }
        }
        void Outline(ParcelRegion shape, Color color, float thickness = 1, float? height = null, bool dashed = false)
        { foreach (var ring in shape.Rings) for (int i = 0; i < ring.Count; i++) Line(ring[i], ring[(i + 1) % ring.Count], color, thickness, height, dashed); }
        void Fill(ParcelRegion shape, Color color)
        { for (int i = 0; i < shape.Triangles.Count; i += 3) geometry.Triangle(P(shape.Triangles[i]), P(shape.Triangles[i + 1]), P(shape.Triangles[i + 2]), Vector3.Up, color, "overlay", "overlay"); }
        if (Mode == CityViewMode.Model && Context.Roads != null)
        {
            int stride = camera.Size > 1000 ? 4 : camera.Size > 500 ? 2 : 1;
            foreach (var field in Context.Roads.Landscape) if (field.CultivationLines != null)
                for (int row = 0; row < field.CultivationLines.Count; row += stride)
                { var line = field.CultivationLines[row]; for (int i = 1; i < line.Count; i++) Line(line[i-1], line[i], new("9b957b"), .65f, dashed: field.Pattern == "orchard"); }
        }
        if (Context.Roads == null || Context.Stale || Mode == CityViewMode.Analysis && Context.Buildings == null && Context.Parcels == null)
        {
            if (Context.Layout.Region != null) foreach (var d in Context.Layout.Region.Districts)
                Outline(d.Shape, Context.Stale ? new("959b91") : d.Role == TownDistrictRole.FortifiedCore ? new("d1b579") : new("9eb7a4"), 1.6f, dashed: Context.Stale);
        }
        if (Mode == CityViewMode.Analysis)
        {
            if (Theme == CityAnalysisTheme.Roads && Context.Roads == null && Context.Layout.Region != null)
                foreach (var link in Context.Layout.Region.Links) for (int i = 1; i < link.Points.Count; i++) Line(link.Points[i-1], link.Points[i], new("7caec7"), 2, dashed:true);
            if (Theme == CityAnalysisTheme.Defence && Context.Layout.Town is {} defence)
            {
                Outline(defence.Interior, new("d7bd82"), 2);
                Outline(defence.WallReserve, new("9aaba6"));
                foreach (var gate in defence.Gates) { Fill(gate.Passage, new("bca474")); Outline(gate.Passage, new("e6c682"), 2); }
            }
            if (Theme == CityAnalysisTheme.Roads && Context.Roads != null)
            {
                foreach (var edge in Context.Roads.Edges) for (int i = 1; i < edge.Points.Count; i++) Line(edge.Points[i - 1], edge.Points[i], edge.Main ? new("e9c88d") : new("aa9d88"), edge.Main ? 2.2f : 1.2f);
                if (camera.Size < 700) foreach (var b in Context.Roads.Geometry.Boundaries) Line(b.A, b.B, b.AllowsFrontage ? new("dfd7b7") : new("a99bbd"));
            }
            if ((Theme == CityAnalysisTheme.Parcels || Theme == CityAnalysisTheme.Uses && Context.Buildings == null) && Context.Parcels != null)
            {
                if (camera.Size > 1000) foreach (var block in Context.Parcels.Blocks) Outline(block.Shape, new("c8d4c0"), 1.3f);
                else foreach (var p in Context.Parcels.Parcels)
                {
                    Fill(p.Shape, CityPalette.Use(p.Use).Darkened(.08f)); if (p.Yard != null) Fill(p.Yard, new("81926d"));
                    Fill(p.Buildable, CityPalette.Use(p.Use).Lightened(.08f));
                    Outline(p.Shape, new("c2c9b5"), .8f);
                    if (camera.Size < 250) foreach (var f in p.Frontage) Line(f.A, f.B, new("d2b37b"), 1.8f);
                }
            }
            if (Theme == CityAnalysisTheme.Remainders && Context.Parcels != null)
                foreach (var rest in Context.Parcels.Remainders) { Fill(rest.Shape, new("bc956d")); Outline(rest.Shape, new("dfb780"), 1.4f); }
            if (Theme == CityAnalysisTheme.Access && Context.Buildings != null)
            {
                foreach (var b in Context.Buildings.Buildings) { Fill(b.Footprint, new("b9b6a0")); Outline(b.Footprint, new("d8c79c")); }
                foreach (var access in Context.Buildings.Access) { Fill(access.Corridor, new("639fa2")); Outline(access.Corridor, new("a1c5be")); }
                foreach (var shared in Context.Buildings.SharedWalls) Line(shared.Start, shared.End, new("b49ec0"), 1.8f);
                if (Context.Placement != null) foreach (var prop in Context.Placement.Props) Outline(prop.Footprint, new("b99064"));
            }
        }
        if (Mode == CityViewMode.Issues)
        {
            foreach (var issue in Issues()) { float s = Math.Max(.8f, width * 4); Line(issue.Position - Vector2.One * s, issue.Position + Vector2.One * s, new("db8962"), 2); Line(issue.Position + new Vector2(-s, s), issue.Position + new Vector2(s, -s), new("db8962"), 2); }
        }
        void Highlight(string? id, Color color, float thickness)
        {
            if (id == null) return;
            var b = Context.Buildings?.Buildings.FirstOrDefault(b => $"B{b.Id}" == id);
            if (b != null) { Outline(b.RoofProjection, color, thickness, b.BaseY + b.WallHeight + .035f); if (Selected == id && camera.Size < 250) foreach (var a in Context.Buildings!.Access.Where(a => a.BuildingId == b.Id)) Outline(a.Corridor, new("76c0c3"), 1.8f); return; }
            var prop = Context.Placement?.Props.FirstOrDefault(p => p.Id == id); if (prop != null) { Outline(prop.Footprint, color, thickness, prop.BaseY + prop.Height + .025f); return; }
            var gate = Context.Layout.Town?.Gates.FirstOrDefault(g => g.Id == id); if (gate != null) { Outline(gate.Passage, color, thickness); return; }
            var parcel = Context.Parcels?.Parcels.FirstOrDefault(p => $"P{p.Id}" == id); if (parcel != null) { Outline(parcel.Shape, color, thickness); Outline(parcel.Buildable, new("77bbb3"), 1.5f); return; }
            var road = Context.Roads?.Edges.FirstOrDefault(r => $"R{r.Id}" == id); if (road != null) for (int i = 1; i < road.Points.Count; i++) Line(road.Points[i - 1], road.Points[i], color, thickness);
            if (Context.Roads != null && id.StartsWith("E")) foreach (int roadId in Context.Roads.PathTo(id))
            { var edge = Context.Roads.Edges.First(e => e.Id == roadId); for (int i = 1; i < edge.Points.Count; i++) Line(edge.Points[i - 1], edge.Points[i], color, thickness); }
            if (id.StartsWith("rest:") && Context.Parcels != null && int.TryParse(id[5..], out int index) && index < Context.Parcels.Remainders.Count) Outline(Context.Parcels.Remainders[index].Shape, color, thickness);
        }
        Highlight(_hover != Selected ? _hover : null, new("dfdfcd"), 1.8f); Highlight(Selected, new("efbe62"), 3);
        var mesh = geometry.ToMesh();
        if (_overlayNode == null) { _overlayNode = new MeshInstance3D { Name = "PlanningOverlay", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }; AddChild(_overlayNode); }
        _overlayNode.Mesh = mesh; _overlayMesh?.Dispose(); _overlayMesh = mesh;
        var material = _overlayNode.MaterialOverride as StandardMaterial3D;
        if (material == null) { material = new() { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled }; _overlayNode.MaterialOverride = material; }
        material.NoDepthTest = Mode != CityViewMode.Model;
    }
    public IEnumerable<(Vector2 Position, string Text)> Issues()
    {
        if (Context?.Roads != null) foreach (var n in Context.Roads.Geometry.Notices) yield return (n.Position, $"R{n.RoadId} · {n.Reason}");
        if (Context?.Buildings != null) foreach (var n in Context.Buildings.Issues) yield return (n.Position, $"{n.Source} · {n.Reason}");
        if (Context?.Placement != null) foreach (var n in Context.Placement.Issues) yield return (n.Position, $"{n.Source} · {n.Reason}");
    }
    private void BuildLabelLayer() { _labelRoot = new Node3D { Name = "CityLabels" }; AddChild(_labelRoot); }
    private void ClearLabels() { foreach (Node child in _labelRoot.GetChildren()) { _labelRoot.RemoveChild(child); child.QueueFree(); } _labels.Clear(); _labelCameraHash = 0; }
    private void RebuildLabels()
    {
        if (_labelRoot == null) return; ClearLabels(); if (Context == null) return;
        void Label(string text, Vector2 p, float height, int priority)
        {
            var anchor = new Vector3(p.X, height, p.Y);
            var label = new Label3D { Text = text, Position = anchor, FontSize = 32, PixelSize = .2f, NoDepthTest = true, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Modulate = priority >= 100 ? new("f0cc83") : new("e3e6d8"), OutlineModulate = new("283b31"), OutlineSize = 4 };
            _labelRoot.AddChild(label); _labels.Add((label, anchor, priority));
        }
        if (Context.Layout.Region != null) foreach (var district in Context.Layout.Region.Districts)
            Label(district.Name, district.Anchor, Context.Terrain.HeightAt(district.Anchor) + 8, 10);
        if (Context.Layout.Town != null)
        {
            foreach (var gate in Context.Layout.Town.Gates) Label(gate.Id, gate.Position, Context.Terrain.HeightAt(gate.Position) + Settings.WallHeight + 1, 40);
            foreach (var precinct in Context.Layout.Town.Precincts)
            {
                float height = Context.Buildings?.Buildings.Where(b => b.PrecinctId == precinct.Id).Select(b => b.BaseY + b.WallHeight + b.RoofHeight + 2).DefaultIfEmpty(Context.Terrain.HeightAt(precinct.Shape.Center) + 3).Max() ?? Context.Terrain.HeightAt(precinct.Shape.Center) + 3;
                Label(precinct.Name, precinct.Shape.Center, height, 30);
            }
        }
        var chosen = Context.Buildings?.Buildings.FirstOrDefault(b => $"B{b.Id}" == Selected);
        if (chosen != null) Label($"B{chosen.Id} · {chosen.Program}", chosen.Footprint.Center, chosen.BaseY + chosen.WallHeight + chosen.RoofHeight + 1, 100);
        _labels.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }
    private void UpdateLabels(Camera3D camera)
    {
        _labelRoot.Visible = Labels; if (!Labels) return;
        var size = GetViewport().GetVisibleRect().Size;
        var ui = GetParent().GetNodeOrNull<CanvasLayer>("Interface"); bool uiVisible = ui?.Visible == true;
        bool terrainVisible = ui?.GetNodeOrNull<Control>("MapUI/TerrainPanel")?.Visible == true;
        int hash = HashCode.Combine(camera.Position, camera.Basis, camera.Size, size, Selected, Mode, Theme, uiVisible ? (terrainVisible ? 2 : 1) : 0);
        if (hash == _labelCameraHash) return; _labelCameraHash = hash;
        var occupied = new List<Rect2>(); int count = 0;
        foreach (var entry in _labels)
        {
            entry.Label.Visible = false; if (camera.IsPositionBehind(entry.Anchor)) continue;
            var p = camera.UnprojectPosition(entry.Anchor); var rect = new Rect2(p - new Vector2(entry.Label.Text.Length * 7, 9), new Vector2(entry.Label.Text.Length * 14, 18)).Grow(5);
            if (uiVisible && (rect.Position.Y < 136 || rect.End.Y > size.Y - 116 || rect.End.X > size.X - 356 || terrainVisible && rect.Position.X < 338)) continue;
            if (rect.Position.X < 10 || rect.End.X > size.X - 10 || rect.Position.Y < 12 || rect.End.Y > size.Y - 10 || occupied.Any(r => r.Intersects(rect)) || count >= (camera.Size > 1200 ? 16 : 32)) continue;
            entry.Label.PixelSize = camera.Size / Math.Max(1, size.Y) * .42f; entry.Label.Visible = true; occupied.Add(rect); count++;
        }
    }
}
