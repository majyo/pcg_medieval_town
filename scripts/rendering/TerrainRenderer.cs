using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// Everything drawn from the terrain: ground (Terrain3D via <see cref="GroundSurface"/>), map skirt, river surface,
/// trees, rocks and ground cover (grass, flowers).
/// </summary>
public partial class TerrainRenderer : Node3D
{
    private TemperateMapData _data = null!;
    private GroundSurface _ground = null!;
    private WaterSurface _water = null!;
    private Forest _forest = null!;
    private Rocks _rocks = null!;
    private GroundCover _cover = null!;

    private enum GroundView { Landscape, Suitability, Colors }

    private GroundView _view;

    public void Build(TemperateMapData data)
    {
        _data = data;
        _ground = new GroundSurface { Name = "Ground" };
        AddChild(_ground);
        _ground.Build(data, GroundMasks);
        BuildSkirt();
        _water = new WaterSurface { Name = "River" };
        AddChild(_water);
        _water.Build(data);
        _forest = new Forest { Name = "Forest" };
        AddChild(_forest);
        _forest.Build(data.Trees);
        _rocks = new Rocks { Name = "Rocks" };
        AddChild(_rocks);
        _rocks.Build(data);
        _cover = new GroundCover { Name = "GroundCover" };
        AddChild(_cover);
        _cover.Build(data, _ground.Masks);
    }

    /// <summary>The painterly ground (the default).</summary>
    public void ShowLandscape()
    {
        if (_view == GroundView.Landscape)
            return;
        _view = GroundView.Landscape;
        _ground.SetMasks(GroundMasks);
    }

    /// <summary>The terrain's own suitability estimate.</summary>
    public void ShowSuitability()
    {
        if (_view == GroundView.Suitability)
            return;
        _view = GroundView.Suitability;
        _ground.SetColors(SuitabilityColor);
    }

    /// <summary>Colors the ground with <paramref name="color"/> at every height-grid point; always repaints.</summary>
    public void ShowColors(Func<Vector2, Color> color)
    {
        _view = GroundView.Colors;
        _ground.Paint(color);
    }

    /// <summary>
    /// Keeps view-dependent detail where the camera looks: the ground's finest LOD on the focus, ground cover
    /// between the camera and the focus.
    /// </summary>
    public void Follow(Vector3 camera, Vector3 focus)
    {
        _ground.Follow(focus);
        _cover.Follow(camera, focus);
    }

    public void ShowForest(bool visible) => _forest.Visible = visible;
    public void ShowWater(bool visible) => _water.Visible = visible;

    public void ShowCover(bool visible)
    {
        _cover.Visible = visible;
        _rocks.Visible = visible;
    }

    /// <summary>Keeps trees, rocks and ground cover out of <paramref name="exclusion"/> (e.g. the town and its roads).</summary>
    public void SetExclusion(Exclusion? exclusion)
    {
        Func<Vector2, bool>? hidden = exclusion == null ? null : exclusion.Contains;
        _forest.SetMask(hidden);
        _rocks.SetMask(hidden);
        _cover.SetExclusion(exclusion?.Rasterize(_data));
    }

    /// <summary>
    /// What the painterly ground shader needs at a point: R = forest cover, G = height above the river surface
    /// (0 at the water, 1 from 3 m up), B = elevation relative to the map's range.
    /// </summary>
    private Color GroundMasks(Vector2 p)
    {
        var d = _data;
        var sample = d.Sample(p);
        var river = d.NearestRiver(p, 120);
        float aboveWater = float.IsPositiveInfinity(river.Distance) ? 1 : Mathf.Clamp((sample.Height - river.WaterHeight) / 3, 0, 1);
        return new Color(sample.Forest, aboveWater, Mathf.InverseLerp(d.MinHeight, d.MaxHeight, sample.Height));
    }

    /// <summary>Suitability color at analysis grid vertex <paramref name="i"/>.</summary>
    private Color SuitabilityColor(int i) =>
        _data.Water[i] ? new Color("315f74") : new Color("ad7860").Lerp(new Color("90bc84"), _data.Suitability[i]);

    private void BuildSkirt()
    {
        // Follow the height grid's edge vertex by vertex so the skirt meets the ground exactly.
        var kit = new MeshKit();
        int n = _data.HeightSide - 1;
        Vector3 Edge(int x, int z)
        {
            var p = _data.HeightPosition(x, z);
            return new Vector3(p.X, _data.HeightGrid[_data.HeightIndex(x, z)], p.Y);
        }

        var edge = new List<Vector3>();
        for (int i = 0; i < n; i++)
            edge.Add(Edge(i, 0));
        for (int i = 0; i < n; i++)
            edge.Add(Edge(n, i));
        for (int i = n; i > 0; i--)
            edge.Add(Edge(i, n));
        for (int i = n; i > 0; i--)
            edge.Add(Edge(0, i));
        for (int i = 0; i < edge.Count; i++)
        {
            var a = edge[i];
            var b = edge[(i + 1) % edge.Count];
            kit.Quad(a, b, new Vector3(b.X, -22, b.Z), new Vector3(a.X, -22, a.Z), new Color("6b6a55"),
                new Vector3(a.X, 0, a.Z));
        }

        // One-sided, seen only from outside: the ground leaves a thin gap at the map edge that would otherwise
        // show the skirt's inner face.
        var material = MeshKit.Material(cull: BaseMaterial3D.CullModeEnum.Front);
        AddChild(new MeshInstance3D { Name = "Skirt", Mesh = kit.Commit(), MaterialOverride = material });
    }
}
