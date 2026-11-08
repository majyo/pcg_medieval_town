using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using PcgCitySandbox.Terrain;
using TokisanGames;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// The ground surface, drawn by the Terrain3D clipmap terrain (addons/terrain_3d) with our own painterly
/// shader (shaders/ground.gdshader). The map's height grid is imported as is, one Terrain3D vertex per
/// grid point, so the rendered ground and <c>HeightAt</c> share the same data. Terrain3D's color map carries
/// either ground masks (painterly view) or finished colors (analysis views). The finest LOD follows the point
/// passed to <see cref="Follow"/>.
/// </summary>
public partial class GroundSurface : Node3D
{
    private const string ShaderPath = "res://shaders/ground.gdshader";

    private readonly Node3D _target = new() { Name = "ClipmapTarget" };
    private Terrain3D _terrain = null!;
    private TemperateMapData _map = null!;
    private int _side, _halfIndex;

    /// <summary>The landscape masks last imported, one RGBA8 pixel per height-grid point (shared with ground cover).</summary>
    public Image Masks { get; private set; } = null!;
    private float _spacing;

    /// <summary>Builds the ground with the painterly look; see <see cref="SetMasks"/>.</summary>
    public void Build(TemperateMapData map, Func<Vector2, Color> masks)
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
        // The placeholder texture below is created in code and never saved, so its editor copy must stay.
        _terrain.FreeEditorTextures = false;
        AddChild(_terrain);
        _terrain.ClipmapTarget = _target;
        _terrain.SetCamera(GetViewport().GetCamera3D());

        var material = _terrain.Material;
        material.WorldBackground = Terrain3DMaterial.WorldBackgroundEnum.None;
        // Assign before enabling: enabling with an empty slot makes Terrain3D generate its default shader there.
        material.ShaderOverride = GD.Load<Shader>(ShaderPath);
        material.ShaderOverrideEnabled = true;
        // Terrain3D appends a checkerboard debug block to the shader while it has no texture assets. Our shader
        // samples no textures, but one placeholder asset keeps that block out.
        _terrain.Assets.SetTextureAsset(0, PlaceholderTexture());

        Masks = MaskImage(masks);
        Import(HeightImage(), new Variant(), Masks);
        CutOutsideMap();
        _terrain.Material.SetShaderParam("view_mode", 0);
    }

    /// <summary>
    /// Painterly view. <paramref name="masks"/> gives, at a world position: R = forest cover, G = height above the
    /// river surface (0 at the water, 1 from 3 m up), B = relative elevation; all in [0, 1].
    /// </summary>
    public void SetMasks(Func<Vector2, Color> masks)
    {
        Masks = MaskImage(masks);
        Import(new Variant(), new Variant(), Masks);
        _terrain.Material.SetShaderParam("view_mode", 0);
    }

    /// <summary>Analysis view: shows per-analysis-grid-vertex sRGB colors as they are.</summary>
    public void SetColors(Func<int, Color> gridColor)
    {
        Import(new Variant(), new Variant(), ColorImage(gridColor));
        _terrain.Material.SetShaderParam("view_mode", 1);
    }

    /// <summary>Centers the finest clipmap level on <paramref name="focus"/>.</summary>
    public void Follow(Vector3 focus) => _target.GlobalPosition = focus;

    private void Import(Variant height, Variant control, Variant color)
    {
        var corner = -_halfIndex * _spacing;
        _terrain.Data.ImportImages(new Godot.Collections.Array { height, control, color }, new Vector3(corner, 0, corner));
    }

    /// <summary>
    /// Terrain3D stores whole 1024-vertex regions, which reach past the map edge. Those extra vertices keep
    /// default data (height 0, white) and would show as a ring around the map, so mark them all as holes.
    /// </summary>
    private void CutOutsideMap()
    {
        const int regionVertices = 1024;
        int regions = (_halfIndex + regionVertices) / regionVertices;
        int first = -regions * regionVertices, size = 2 * regions * regionVertices;
        float hole = BitConverter.Int32BitsToSingle((int)Terrain3DUtil.EncHole(true));
        var control = new float[size * size];
        Parallel.For(0, size, z =>
        {
            int iz = first + z;
            for (int x = 0; x < size; x++)
            {
                int ix = first + x;
                if (Math.Abs(ix) > _halfIndex || Math.Abs(iz) > _halfIndex)
                    control[z * size + x] = hole;
            }
        });
        var image = Image.CreateFromData(size, size, false, Image.Format.Rf, MemoryMarshal.AsBytes(control.AsSpan()).ToArray());
        var corner = first * _spacing;
        _terrain.Data.ImportImages(new Godot.Collections.Array { new Variant(), image, new Variant() }, new Vector3(corner, 0, corner));
    }

    private Image HeightImage() =>
        Image.CreateFromData(_side, _side, false, Image.Format.Rf, MemoryMarshal.AsBytes(_map.HeightGrid.AsSpan()).ToArray());

    /// <summary>Evaluates <paramref name="masks"/> at every height-grid point.</summary>
    private Image MaskImage(Func<Vector2, Color> masks)
    {
        var pixels = new byte[_side * _side * 4];
        Parallel.For(0, _side, z =>
        {
            for (int x = 0; x < _side; x++)
                Write(pixels, z * _side + x, masks(_map.HeightPosition(x, z)));
        });
        return Image.CreateFromData(_side, _side, false, Image.Format.Rgba8, pixels);
    }

    /// <summary>Analysis-grid colors bilinearly resampled onto the height grid.</summary>
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
                Write(pixels, z * _side + x, top.Lerp(bottom, v));
            }
        });
        return Image.CreateFromData(_side, _side, false, Image.Format.Rgba8, pixels);
    }

    private static Terrain3DTextureAsset PlaceholderTexture()
    {
        static ImageTexture Fill(Color color)
        {
            var image = Image.CreateEmpty(64, 64, false, Image.Format.Rgba8);
            image.Fill(color);
            image.GenerateMipmaps();
            return ImageTexture.CreateFromImage(image);
        }

        var asset = Terrain3DTextureAsset.Instantiate();
        asset.Name = "Placeholder";
        asset.AlbedoTexture = Fill(new Color(1, 1, 1, 0.5f));
        asset.NormalTexture = Fill(new Color(0.5f, 0.5f, 1, 1));
        return asset;
    }

    private static void Write(byte[] pixels, int index, Color c)
    {
        int k = index * 4;
        pixels[k] = (byte)Mathf.Clamp(c.R8, 0, 255);
        pixels[k + 1] = (byte)Mathf.Clamp(c.G8, 0, 255);
        pixels[k + 2] = (byte)Mathf.Clamp(c.B8, 0, 255);
        pixels[k + 3] = 255;
    }
}
