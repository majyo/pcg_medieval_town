using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.Terrain;
using TokisanGames;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// The ground surface, drawn by the Terrain3D clipmap terrain (addons/terrain_3d). The map's height grid is
/// imported as is, one Terrain3D vertex per grid point, so the rendered ground and <c>HeightAt</c> share the
/// same data. Ground colors go into Terrain3D's color map over a plain white texture. The finest LOD follows
/// the point passed to <see cref="Follow"/>.
/// </summary>
public partial class GroundSurface : Node3D
{
    private readonly Node3D _target = new() { Name = "ClipmapTarget" };
    private Terrain3D _terrain = null!;
    private TemperateMapData _map = null!;
    private int _side, _halfIndex;
    private float _spacing;

    public void Build(TemperateMapData map, Func<int, Color> gridColor)
    {
        _map = map;
        _side = map.HeightSide;
        // The grid is odd-sized and centered, so vertex indices run -_halfIndex.._halfIndex.
        _halfIndex = (_side - 1) / 2;
        _spacing = map.HeightSpacing;
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
        var corner = -_halfIndex * _spacing;
        _terrain.Data.ImportImages(new Godot.Collections.Array { height, control, color }, new Vector3(corner, 0, corner));
    }

    private Image HeightImage() =>
        Image.CreateFromData(_side, _side, false, Image.Format.Rf, MemoryMarshal.AsBytes(_map.HeightGrid.AsSpan()).ToArray());

    /// <summary>Analysis-grid colors bilinearly resampled onto the height grid; alpha 0.5 leaves roughness unchanged.</summary>
    private Image ColorImage(Func<int, Color> gridColor)
    {
        var grid = new Color[_map.Heights.Length];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = gridColor(i);
        var pixels = new byte[_side * _side * 4];
        Parallel.For(0, _side, z =>
        {
            for (int x = 0; x < _side; x++)
            {
                var p = _map.HeightPosition(x, z);
                float gx = Mathf.Clamp((p.X + _map.Half) / _map.Spacing, 0, _map.Side - 1.001f);
                float gz = Mathf.Clamp((p.Y + _map.Half) / _map.Spacing, 0, _map.Side - 1.001f);
                int ix = (int)gx, iz = (int)gz;
                float u = gx - ix, v = gz - iz;
                var top = grid[_map.Index(ix, iz)].Lerp(grid[_map.Index(ix + 1, iz)], u);
                var bottom = grid[_map.Index(ix, iz + 1)].Lerp(grid[_map.Index(ix + 1, iz + 1)], u);
                var c = top.Lerp(bottom, v);
                int k = (z * _side + x) * 4;
                pixels[k] = (byte)c.R8;
                pixels[k + 1] = (byte)c.G8;
                pixels[k + 2] = (byte)c.B8;
                pixels[k + 3] = 128;
            }
        });
        return Image.CreateFromData(_side, _side, false, Image.Format.Rgba8, pixels);
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
