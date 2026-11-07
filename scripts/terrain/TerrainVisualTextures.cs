using Godot;
using System;

namespace PcgCitySandbox;

// Seamless, authored-in-code surface textures; shared across every map rebuild.
// RGB pattern is a linear reflectance modulation. Packed data: normal RG, roughness B, pattern A.
public sealed class TerrainVisualTextures
{
    public const int Size = 1024;
    public Texture2DArray Patterns { get; } = new();
    public Texture2DArray NormalsRoughness { get; } = new();
    public long Bytes { get; private set; }
    public static TerrainVisualTextures Shared => _shared ??= new();
    private static TerrainVisualTextures? _shared;
    private TerrainVisualTextures()
    {
        var patterns = new Godot.Collections.Array<Image>(); var normals = new Godot.Collections.Array<Image>();
        for (int layer = 0; layer < 6; layer++)
        {
            var heights = new float[Size * Size]; var colors = new byte[Size * Size * 4]; var packed = new byte[colors.Length];
            for (int z = 0; z < Size; z++) for (int x = 0; x < Size; x++)
            {
                float u = x / (float)Size, v = z / (float)Size;
                float coarse = Noise(u, v, 8, layer + 1), medium = Noise(u, v, 40, layer + 7), fine = Noise(u, v, 180, layer + 17);
                float cell = Cells(u, v, layer < 2 ? 40 : layer == 2 ? 24 : 18, layer + 41, layer < 2 ? .23f : layer == 2 ? .65f : 1);
                float h = layer switch
                {
                    0 => .2f * coarse + .3f * medium + .4f * cell + .1f * fine,
                    1 => .15f * coarse + .2f * medium + .55f * cell + .1f * fine,
                    2 => .2f * coarse + .2f * medium + .5f * cell + .1f * fine,
                    3 => .35f * coarse + .4f * medium + .25f * fine,
                    4 => .65f * coarse + .25f * medium + .1f * fine,
                    _ => .12f * coarse + .12f * medium + .7f * cell + .06f * fine
                };
                int k = z * Size + x; heights[k] = h;
                float shade = .22f + h * .58f;
                colors[k * 4] = TerrainVisualData.Byte(shade + (coarse - .5f) * .035f);
                colors[k * 4 + 1] = TerrainVisualData.Byte(shade);
                colors[k * 4 + 2] = TerrainVisualData.Byte(shade - (coarse - .5f) * .035f);
                colors[k * 4 + 3] = 255;
            }
            float amplitude = layer switch { 0 => .008f, 1 => .007f, 2 => .012f, 3 => .006f, 4 => .003f, _ => .035f };
            float roughness = layer switch { 4 => .58f, 5 => .87f, _ => .95f };
            for (int z = 0; z < Size; z++) for (int x = 0; x < Size; x++)
            {
                int k = z * Size + x;
                float dx = (heights[z * Size + (x + 1) % Size] - heights[z * Size + (x + Size - 1) % Size]) * amplitude * Size / 16;
                float dz = (heights[((z + 1) % Size) * Size + x] - heights[((z + Size - 1) % Size) * Size + x]) * amplitude * Size / 16;
                var n = new Vector3(-dx, 1, -dz).Normalized();
                packed[k * 4] = TerrainVisualData.Byte(n.X * .5f + .5f); packed[k * 4 + 1] = TerrainVisualData.Byte(n.Z * .5f + .5f);
                packed[k * 4 + 2] = TerrainVisualData.Byte(roughness + (heights[k] - .5f) * .1f);
                packed[k * 4 + 3] = colors[k * 4 + 1];
            }
            var pattern = Image.CreateFromData(Size, Size, false, Image.Format.Rgba8, colors); pattern.GenerateMipmaps();
            var normal = Image.CreateFromData(Size, Size, false, Image.Format.Rgba8, packed); normal.GenerateMipmaps();
            Bytes += pattern.GetData().LongLength + normal.GetData().LongLength;
            patterns.Add(pattern); normals.Add(normal);
        }
        if (Patterns.CreateFromImages(patterns) != Error.Ok || NormalsRoughness.CreateFromImages(normals) != Error.Ok) throw new InvalidOperationException("Terrain texture array creation failed");
        foreach (var image in patterns) image.Dispose(); foreach (var image in normals) image.Dispose();
    }
    private static float Hash(int x, int y, int seed)
    {
        uint h = unchecked((uint)x * 374761393u + (uint)y * 668265263u + (uint)seed * 2246822519u);
        h = (h ^ (h >> 13)) * 1274126177u; return ((h ^ (h >> 16)) & 0xffffff) / 16777215f;
    }
    private static int Wrap(int n, int period) => (n % period + period) % period;
    private static float Noise(float u, float v, int period, int seed)
    {
        float px = u * period, pz = v * period; int x = (int)px, z = (int)pz;
        float tx = px - x, tz = pz - z; tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
        return Mathf.Lerp(Mathf.Lerp(Hash(Wrap(x, period), Wrap(z, period), seed), Hash(Wrap(x + 1, period), Wrap(z, period), seed), tx),
            Mathf.Lerp(Hash(Wrap(x, period), Wrap(z + 1, period), seed), Hash(Wrap(x + 1, period), Wrap(z + 1, period), seed), tx), tz);
    }
    private static float Cells(float u, float v, int period, int seed, float width)
    {
        float px = u * period, pz = v * period; int x = (int)px, z = (int)pz; float best = 4;
        for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
        {
            int xx = Wrap(x + dx, period), zz = Wrap(z + dz, period);
            float a = (px - (x + dx + Hash(xx, zz, seed))) / width;
            float b = pz - (z + dz + Hash(xx, zz, seed + 193));
            best = Math.Min(best, a * a + b * b);
        }
        return 1 - TerrainVisualData.Smooth(.015f, .5f, best);
    }
}
