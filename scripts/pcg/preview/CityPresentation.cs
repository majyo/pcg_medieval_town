using Godot;
using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PcgCitySandbox.Pcg;

public sealed record CityContext(TemperateMapData Map, SettlementLayout Layout, RoadNetwork? Roads, ParcelLayout? Parcels, BuildingSet? Buildings, ScenePlacementSet? Placement, object Source, bool Stale, RoadGrading? Grading=null)
{
    private ITerrainSurface? _terrain;
    public ITerrainSurface Terrain=>_terrain??=Grading?.Graded??(ITerrainSurface)new NativeTerrainSurface(Map);
}
public enum CityAnalysisTheme { Uses, Roads, Parcels, Access, Remainders, Defence }

public partial class CityPresentation : Node3D
{
    public CityContext? Context { get; private set; }
    public CityVisualSettings Settings { get; set; } = new();
    public CityViewMode Mode { get; private set; }
    public CityAnalysisTheme Theme { get; private set; }
    public string? Selected { get; private set; }
    public bool Roofs { get; private set; } = true;
    public int WallDisplay { get; private set; }
    public bool Details { get; private set; } = true;
    public bool Labels { get; set; } = true;
    public int RebuildCount { get; private set; }
    public int LastPickTriangles { get; private set; }
    public RoadSceneData? Roads {get;private set;}
    public CityGeometry Ground { get; private set; } = new();
    public CityGeometry Bodies { get; private set; } = new();
    public BuildingDetailResult DetailData { get; private set; } = new();
    public CityGeometry Walls { get; private set; } = new();
    private CityGeometry _lowWalls = new(), _props = new();
    private readonly List<ArrayMesh> _ownedMeshes = new();
    private readonly List<MeshInstance3D> _detailNodes = new(), _roofDetailNodes = new();
    private ShaderMaterial _material = null!, _groundMaterial = null!;
    private MeshInstance3D? _bodyNode, _roofNode, _floorNode, _wallNode, _lowWallNode, _propsNode;
    private Node3D _geometryRoot = null!;
    private CityPickIndex? _bodyPick, _detailPick, _propPick, _wallPick, _lowWallPick;
    private bool _detailVisible;
    private string _overlayKey = "";
    private Node3D? _roadNode;
    public void RebuildRoads(RoadConstructionSettings settings)
    {
        if(Context?.Grading==null)return;
        if(_roadNode!=null){_geometryRoot.RemoveChild(_roadNode);SceneMeshCleanup.DisposeMeshes(_roadNode);_roadNode.QueueFree();}
        Roads=RoadSceneBuilder.Build(Context.Grading,settings.Snapshot());_roadNode=RoadSceneBuilder.Instantiate(Roads,settings.MaterialOverride);_geometryRoot.AddChild(_roadNode);
    }
    public override void _Ready()
    {
        Settings.Validate();
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/city_surface.gdshader") };
        _groundMaterial = new ShaderMaterial { Shader = _material.Shader }; _groundMaterial.SetShaderParameter("ground_surface", true);
        _geometryRoot = new Node3D { Name = "Geometry" }; AddChild(_geometryRoot);
        BuildLabelLayer();
    }
    private MeshInstance3D AddMesh(string name, CityGeometry data, bool ground = false, Func<string, bool>? filter = null)
    {
        var mesh = data.ToMesh(filter,lod:ground); _ownedMeshes.Add(mesh);
        var node = new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = ground ? _groundMaterial : _material, CastShadow = ground ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On };
        _geometryRoot.AddChild(node); return node;
    }
    private void ClearGeometry()
    {
        foreach (Node child in _geometryRoot.GetChildren()) { _geometryRoot.RemoveChild(child);SceneMeshCleanup.DisposeMeshes(child); child.QueueFree(); }
        foreach (var mesh in _ownedMeshes) mesh.Dispose(); _ownedMeshes.Clear(); _detailNodes.Clear(); _roofDetailNodes.Clear();
        _bodyNode = _roofNode = _floorNode = _wallNode = _lowWallNode = _propsNode = null;_roadNode=null;
    }
    public void Display(CityContext? context)
    {
        if (context == null) { Visible = false; Context = null; ClearGeometry(); ClearLabels(); return; }
        Visible = true;
        if (Context != null && ReferenceEquals(Context.Source, context.Source) && ReferenceEquals(Context.Map, context.Map) && ReferenceEquals(Context.Grading,context.Grading))
        { Context = context; ApplyState(); return; }
        var timer = System.Diagnostics.Stopwatch.StartNew(); ClearGeometry(); Context = context; Selected = null; _overlayKey = ""; RebuildCount++;
        var map = context.Map; var layout = context.Layout;
        var patches = new List<(string, string, ParcelRegion)>();
        if (context.Placement != null) patches.AddRange(context.Placement.GroundPatches.Select(p => ($"ground/{p.Id}", p.Kind, p.Shape)));
        else
        {
            var landscape = context.Roads?.Landscape ?? layout.Region?.Landscape;
            if (landscape != null) patches.AddRange(landscape.Select(p => (p.Id, p.Pattern == "orchard" ? "果园" : p.Pattern == "pasture" ? "草地" : "田地", p.Shape)));
            if (context.Parcels != null) foreach (var p in context.Parcels.Parcels) if (p.Yard != null) patches.Add(($"yard/{p.Id}", p.Use == ParcelUse.Production ? "作坊院地" : "后园", p.Yard));
            if (context.Roads != null)
            {
                var byId = context.Roads.Edges.ToDictionary(e => e.Id);
                patches.AddRange(context.Roads.Geometry.Surfaces.Select(s => ($"R{s.RoadId}", byId[s.RoadId].Main ? "主路" : "街巷", s.Shape)));
                var square = ParcelGeometry.Difference(ParcelGeometry.Rect(layout.Plaza), context.Roads.Geometry.Area.CopyPaths());
                if (ParcelGeometry.Area(square) > .01) patches.Add(("plaza", "广场", new ParcelRegion(square)));
            }
        }
        if(context.Grading!=null){patches=patches.Where(p=>p.Item2 is not "主路" and not "街巷" and not "广场").ToList();
            RebuildRoads(GD.Load<RoadConstructionSettings>("res://configs/road_construction.tres"));}
        else Roads=null;
        CityGeometry? farGround=null;
        if(context.Grading!=null){var groundLod=CityGroundLod.Geometry(context.Source,map,context.Terrain,patches);Ground=groundLod.Near;farGround=groundLod.Far;}
        else Ground = CityGroundMesh.Build(map, patches,context.Terrain);
        if (context.Roads != null && context.Grading == null)
        {
            var area = context.Roads.Geometry.Area.CopyPaths(); var shoulder = ParcelGeometry.Difference(area, ParcelGeometry.Offset(area, -.25f));
            if (ParcelGeometry.Area(shoulder) > .01)
            {
                var strip = CityGroundMesh.Build(map, new[] { ("road_shoulders", "路肩", new ParcelRegion(shoulder)) },context.Terrain);
                for (int i = 0; i < strip.Positions.Count; i++) strip.Positions[i] += Vector3.Up * .004f;
                Ground.Append(strip);
            }
        }
        if(farGround!=null)_geometryRoot.AddChild(CityGroundLod.Instantiate(Ground,farGround,_groundMaterial));else AddMesh("Ground", Ground, true);
        Bodies = context.Buildings != null ? CityGeometry.Buildings(context.Buildings) : new();
        DetailData = context.Buildings != null ? BuildingDetailBuilder.Build(context.Buildings, map,context.Terrain) : new();
        _bodyPick = new(Bodies); _detailPick = new(DetailData.Geometry);
        _bodyNode = AddMesh("Buildings", Bodies, filter: part => part != "roof"); _roofNode = AddMesh("Roofs", Bodies, filter: part => part == "roof");
        var floors = new CityGeometry();
        if (context.Buildings != null) foreach (var b in context.Buildings.Buildings) for (int i = 0; i < b.Footprint.Triangles.Count; i += 3)
        {
            Vector3 P(int k) { var p = b.Footprint.Triangles[k]; return new(p.X, b.BaseY + .03f, p.Y); }
            floors.Triangle(P(i), P(i + 1), P(i + 2), Vector3.Up, new("a29a85"), $"B{b.Id}", "floor", CityPalette.Role(b.Program));
        }
        _floorNode = AddMesh("FootprintFloors", floors);
        var chunks = new Dictionary<Vector2I, CityGeometry>();
        var data = DetailData.Geometry;
        for (int i = 0; i < data.Positions.Count; i += 3)
        {
            var p = data.Positions[i]; var cell = new Vector2I((int)MathF.Floor(p.X / 160), (int)MathF.Floor(p.Z / 160));
            if (!chunks.TryGetValue(cell, out var chunk)) chunks.Add(cell, chunk = new());
            chunk.Triangle(p, data.Positions[i + 1], data.Positions[i + 2], data.Normals[i], data.Colors[i], data.Owners[i], data.Parts[i], (int)data.Attributes[i].X);
        }
        foreach (var chunk in chunks.Values)
        {
            var node = AddMesh("FacadeDetails", chunk, filter: p => p != "roof_detail"); node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; _detailNodes.Add(node);
            node = AddMesh("RoofDetails", chunk, filter: p => p == "roof_detail"); node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; _roofDetailNodes.Add(node);
        }
        Walls = FortificationPreviewBuilder.Build(layout, map, Settings.WallHeight,context.Terrain); _lowWalls = FortificationPreviewBuilder.LowWall(Walls, map,context.Terrain);
        _wallNode = AddMesh("Fortification", Walls); _lowWallNode = AddMesh("LowFortification", _lowWalls); _wallPick = new(Walls); _lowWallPick = new(_lowWalls);
        _props = context.Placement != null ? CityGeometry.Props(context.Placement) : new(); _propsNode = AddMesh("Props", _props); _propPick = new(_props);
        ApplyState(); RebuildLabels();
        GD.Print($"CITY_VISUAL_READY source={context.Source.GetType().Name} ground_triangles={Ground.Triangles} building_triangles={Bodies.Triangles} detail_triangles={DetailData.Geometry.Triangles} wall_triangles={Walls.Triangles} elapsed_ms={timer.Elapsed.TotalMilliseconds:F1}");
    }
    public void SetState(CityViewMode mode, CityAnalysisTheme theme, bool roofs, int walls, bool details, bool labels)
    { Mode = mode; Theme = theme; Roofs = roofs; WallDisplay = walls; Details = details; Labels = labels; ApplyState(); }
    private bool HideMasses => Mode == CityViewMode.Analysis && Theme is CityAnalysisTheme.Parcels or CityAnalysisTheme.Access or CityAnalysisTheme.Remainders;
    private void ApplyState()
    {
        if (_material == null) return;
        _material.SetShaderParameter("view_mode", (int)Mode); _material.SetShaderParameter("stale", Context?.Stale ?? false);
        _groundMaterial.SetShaderParameter("view_mode", (int)Mode); _groundMaterial.SetShaderParameter("stale", Context?.Stale ?? false);
        _material.SetShaderParameter("gray_study", Context?.Placement?.Options.Scheme == SceneScheme.GrayStudy);
        _groundMaterial.SetShaderParameter("gray_study", Context?.Placement?.Options.Scheme == SceneScheme.GrayStudy);
        _material.SetShaderParameter("analysis_uses", Theme == CityAnalysisTheme.Uses);
        _groundMaterial.SetShaderParameter("analysis_uses", Theme == CityAnalysisTheme.Uses);
        if (_bodyNode != null) _bodyNode.Visible = !HideMasses;
        if (_roofNode != null) _roofNode.Visible = !HideMasses && Roofs;
        if (_floorNode != null) _floorNode.Visible = !HideMasses && !Roofs;
        if (_wallNode != null) _wallNode.Visible = WallDisplay == 0;
        if (_lowWallNode != null) _lowWallNode.Visible = WallDisplay == 1;
        if (_propsNode != null) _propsNode.Visible = !HideMasses;
    }
    public void Select(string? id) { if (Selected == id) return; Selected = id; _overlayKey = ""; RebuildLabels(); }
    public override void _Process(double delta)
    {
        if (!Visible || Context == null) return; var camera = GetViewport().GetCamera3D(); if (camera == null) return;
        float threshold = Settings.DetailViewSize * (_detailVisible ? 1.1f : .9f);
        _detailVisible = Details && !HideMasses && Mode != CityViewMode.Issues && camera.Size < threshold;
        foreach (var node in _detailNodes) node.Visible = _detailVisible;
        foreach (var node in _roofDetailNodes) node.Visible = _detailVisible && Roofs;
        UpdateOverlays(camera); UpdateLabels(camera);
    }
    public string? Pick(Vector3 origin, Vector3 ray, Vector2? ground)
    {
        if (Context == null) return null;
        if (HideMasses && ground.HasValue)
        {
            var prop = Context.Placement?.Props.FirstOrDefault(p => p.Footprint.Contains(ground.Value)); if (prop != null) return prop.Id;
            var building = Context.Buildings?.Buildings.FirstOrDefault(b => b.Footprint.Contains(ground.Value)); return building != null ? $"B{building.Id}" : null;
        }
        float nearest = float.PositiveInfinity; string? id = null; LastPickTriangles = 0;
        foreach (var index in new[] { _bodyPick, _propsNode?.Visible == true ? _propPick : null, _detailVisible ? _detailPick : null, WallDisplay == 0 ? _wallPick : WallDisplay == 1 ? _lowWallPick : null })
        {
            if (index == null) continue; var hit = index.Pick(origin, ray, nearest, Roofs); LastPickTriangles += index.LastTriangleTests;
            if (hit.Id != null) { nearest = hit.Distance; id = hit.Id; }
        }
        if (id != null) { var p = origin + ray * nearest; if (Context.Map.Contains(new(p.X, p.Z)) && p.Y < Context.Terrain.HeightAt(new(p.X, p.Z)) - .04f) return null; return id; }
        if (ground.HasValue) {
            var gate=Context.Layout.Town?.Gates.FirstOrDefault(g=>g.Passage.Contains(ground.Value));if(gate!=null)return gate.Id;
            if(Context.Grading?.InPlaza(ground.Value)==true)return "plaza";
            var road=Context.Grading?.SurfaceAt(ground.Value);if(road!=null)return $"R{road.RoadId}";
        }
        return null;
    }
    public override void _ExitTree()
    { foreach (var mesh in _ownedMeshes) mesh.Dispose(); _material?.Dispose(); _groundMaterial?.Dispose(); _overlayMesh?.Dispose(); }
}
