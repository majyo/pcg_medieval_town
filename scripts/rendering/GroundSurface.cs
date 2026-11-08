using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.Terrain;
using TokisanGames;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// The ground surface, drawn by the Terrain3D clipmap terrain (addons/terrain_3d). The map's height field is
/// resampled onto a grid of about 1 m that exactly spans the map; ground colors go into Terrain3D's color map
/// over a plain white texture. The finest LOD follows the point passed to <see cref="Follow"/>.
/// </summary>
public partial class GroundSurface : Node3D
{
    /// <summary>Vertices per side. Vertex indices run -Half..Half, which fits two 1024-vertex regions.</summary>
    private const int Side = 2047;

    private const int HalfIndex = Side / 2;

    private readonly Node3D _target = new() { Name = "ClipmapTarget" };
    private Terrain3D _terrain = null!;
    private TemperateMapData _map = null!;
    private float _spacing;

    public void Build(TemperateMapData map, Func<int, Color> gridColor)
    {
        _map = map;
        _spacing = map.Half / HalfIndex;
        AddChild(_target);
        _terrain = Terrain3D.Instantiate();
        _terrain.Name = "Terrain3D";
        _terrain.RegionSize = Terrain3D.RegionSizeEnum.Size1024;
        _terrain.VertexSpacing = _spacing;
        // Assets are created in code and never saved, so the editor copies must stay.
        _terrain.FreeEditorTextures = false;
        AddChild(_terrain);
        _terrain.ClipmapTarget = _target;
        _terrain.SetCamera(GetViewport().GetCamera3D());

        var material = _terrain.Material;
        material.WorldBackground = Terrain3DMaterial.WorldBackgroundEnum.None;
        material.AutoShaderEnabled = false;
        material.MacroVariationEnabled = false;
        _terrain.Assets.SetTextureAsset(0, PlainTexture());

        Import(HeightImage(), new Variant(), ColorImage(gridColor));
    }

    /// <summary>Recolors the ground from per-grid-vertex colors of the map (e.g. for an analysis view).</summary>
    public void SetColors(Func<int, Color> gridColor) => Import(new Variant(), new Variant(), ColorImage(gridColor));

    /// <summary>Centers the finest clipmap level on <paramref name="focus"/>.</summary>
    public void Follow(Vector3 focus) => _target.GlobalPosition = focus;

    private void Import(Variant height, Variant control, Variant color)
    {
        var corner = -HalfIndex * _spacing;
        _terrain.Data.ImportImages(new Godot.Collections.Array { height, control, color }, new Vector3(corner, 0, corner));
    }

    private Vector2 GridPoint(int x, int z) => new((x - HalfIndex) * _spacing, (z - HalfIndex) * _spacing);

    private Image HeightImage()
    {
        var heights = new float[Side * Side];
        Parallel.For(0, Side, z =>
        {
            for (int x = 0; x < Side; x++)
                heights[z * Side + x] = _map.HeightAt(GridPoint(x, z));
        });
        return Image.CreateFromData(Side, Side, false, Image.Format.Rf, MemoryMarshal.AsBytes(heights.AsSpan()).ToArray());
    }

    /// <summary>Grid colors bilinearly resampled onto the terrain grid; alpha 0.5 leaves roughness unchanged.</summary>
    private Image ColorImage(Func<int, Color> gridColor)
    {
        var grid = new Color[_map.Heights.Length];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = gridColor(i);
        var pixels = new byte[Side * Side * 4];
        Parallel.For(0, Side, z =>
        {
            for (int x = 0; x < Side; x++)
            {
                var p = GridPoint(x, z);
                float gx = Mathf.Clamp((p.X + _map.Half) / _map.Spacing, 0, _map.Side - 1.001f);
                float gz = Mathf.Clamp((p.Y + _map.Half) / _map.Spacing, 0, _map.Side - 1.001f);
                int ix = (int)gx, iz = (int)gz;
                float u = gx - ix, v = gz - iz;
                var top = grid[_map.Index(ix, iz)].Lerp(grid[_map.Index(ix + 1, iz)], u);
                var bottom = grid[_map.Index(ix, iz + 1)].Lerp(grid[_map.Index(ix + 1, iz + 1)], u);
                var c = top.Lerp(bottom, v);
                int k = (z * Side + x) * 4;
                pixels[k] = (byte)c.R8;
                pixels[k + 1] = (byte)c.G8;
                pixels[k + 2] = (byte)c.B8;
                pixels[k + 3] = 128;
            }
        });
        return Image.CreateFromData(Side, Side, false, Image.Format.Rgba8, pixels);
    }

    /// <summary>A white albedo and a flat normal, so the color map alone decides the ground color.</summary>
    private static Terrain3DTextureAsset PlainTexture()
    {
        static ImageTexture Fill(Color color)
        {
            var image = Image.CreateEmpty(256, 256, false, Image.Format.Rgba8);
            image.Fill(color);
            image.GenerateMipmaps();
            return ImageTexture.CreateFromImage(image);
        }

        var asset = Terrain3DTextureAsset.Instantiate();
        asset.Name = "Plain";
        asset.AlbedoTexture = Fill(new Color(1, 1, 1, 0.5f));
        asset.NormalTexture = Fill(new Color(0.5f, 0.5f, 1, 0.8f));
        return asset;
    }
}
