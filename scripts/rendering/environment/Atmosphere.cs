using System;
using System.Collections.Generic;
using Godot;
using Environment = Godot.Environment;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Rendering;

/// <summary>The sun follows the time of day: east at 6 h, south at noon, west at 18 h.</summary>
public sealed class SunSettings
{
    [Param("时刻 h", 5, 20, 0.05)] public float Hour { get; set; } = 15.2f;
    [Param("方位偏移 °", -180, 180, 1)] public float AzimuthOffset { get; set; }
    [Param("正午高度角 °", 10, 85, 1)] public float NoonElevation { get; set; } = 52;
    [Param("强度", 0, 3, 0.05)] public float Energy { get; set; } = 1.25f;
    [Param("颜色")] public Color Color { get; set; } = new("fff0da");
    [Param("低角度暖色", 0, 1, 0.05)] public float Warmth { get; set; } = 0.6f;
    [Param("阴影")] public bool Shadows { get; set; } = true;
    [Param("阴影柔和", 0, 4, 0.1)] public float ShadowBlur { get; set; } = 1.2f;
}

public sealed class SkySettings
{
    [Param("天顶色")] public Color TopColor { get; set; } = new("5d8fc4");
    [Param("地平线色")] public Color HorizonColor { get; set; } = new("c5d8de");
    [Param("天空亮度", 0, 3, 0.05)] public float Energy { get; set; } = 1;
    [Param("环境光", 0, 2, 0.05)] public float AmbientEnergy { get; set; } = 0.55f;
}

public sealed class FogSettings
{
    [Param("启用")] public bool Enabled { get; set; } = true;
    [Param("浓度 ×10⁻⁴", 0, 30, 0.1)] public float Density { get; set; } = 1.2f;
    [Param("颜色")] public Color Color { get; set; } = new("c2d4dc");
    [Param("空气透视", 0, 1, 0.05)] public float AerialPerspective { get; set; } = 0.3f;
    [Param("阳光散射", 0, 1, 0.05)] public float SunScatter { get; set; }
    [Param("影响天空", 0, 1, 0.05)] public float SkyAffect { get; set; }
    [Param("低处雾浓度", 0, 0.5, 0.005)] public float HeightDensity { get; set; }
    [Param("低处雾高度 m", -20, 120, 1)] public float Height { get; set; } = 20;
}

public sealed class PostSettings
{
    [Param("曝光", 0.3, 3, 0.05)] public float Exposure { get; set; } = 1.1f;
    [Param("饱和度", 0, 2, 0.02)] public float Saturation { get; set; } = 1.18f;
    [Param("对比度", 0.5, 1.5, 0.02)] public float Contrast { get; set; } = 1.04f;
    [Param("环境光遮蔽")] public bool Ssao { get; set; } = true;
    [Param("遮蔽强度", 0, 4, 0.1)] public float SsaoIntensity { get; set; } = 1.4f;
    [Param("遮蔽半径 m", 0.2, 6, 0.1)] public float SsaoRadius { get; set; } = 1.6f;
    [Param("泛光")] public bool Glow { get; set; } = true;
    [Param("泛光强度", 0, 2, 0.05)] public float GlowIntensity { get; set; } = 0.35f;
    [Param("泛光外溢", 0, 0.5, 0.01)] public float GlowBloom { get; set; } = 0.03f;
}

/// <summary>The painterly light shared by the ground and every environment shader, and the wind.</summary>
public sealed class StyleSettings
{
    [Param("卡通分阶", 0, 1, 0.05)] public float Toon { get; set; } = 0.3f;
    [Param("光照包裹", 0, 1, 0.05)] public float Wrap { get; set; } = 0.35f;
    [Param("暗部补光", 0, 1, 0.02)] public float ShadowFill { get; set; } = 0.22f;
    [Param("暗部色调")] public Color ShadowTint { get; set; } = new(0.55f, 0.60f, 0.85f);
    [Param("风力", 0, 3, 0.05)] public float WindStrength { get; set; } = 1;
    [Param("风吹向 °", 0, 360, 1)] public float WindBearing { get; set; } = 127;
}

/// <summary>Everything <see cref="Atmosphere"/> can be told; one object per panel section.</summary>
public sealed class AtmosphereSettings
{
    public SunSettings Sun { get; } = new();
    public SkySettings Sky { get; } = new();
    public FogSettings Fog { get; } = new();
    public PostSettings Post { get; } = new();
    public StyleSettings Style { get; } = new();

    public IEnumerable<object> Sections => new object[] { Sun, Sky, Fog, Post, Style };

    /// <summary>Copies every value of <paramref name="other"/> into these objects (the panel stays bound to them).</summary>
    public void CopyFrom(AtmosphereSettings other)
    {
        using var mine = Sections.GetEnumerator();
        foreach (var section in other.Sections)
        {
            mine.MoveNext();
            foreach (var property in section.GetType().GetProperties())
            {
                if (property.CanWrite)
                    property.SetValue(mine.Current, property.GetValue(section));
            }
        }
    }

    /// <summary>Named looks, the first being the defaults.</summary>
    public static readonly IReadOnlyList<(string Name, Func<AtmosphereSettings> Create)> Presets = new (string, Func<AtmosphereSettings>)[]
    {
        ("午后", () => new AtmosphereSettings()),
        ("清晨", () => With(s =>
        {
            (s.Sun.Hour, s.Sun.Energy, s.Sun.Color) = (7.3f, 1.05f, new Color("ffd9b3"));
            (s.Sky.TopColor, s.Sky.HorizonColor, s.Sky.AmbientEnergy) = (new Color("7f9fc8"), new Color("f0d9c4"), 0.5f);
            (s.Fog.Density, s.Fog.Color, s.Fog.SunScatter, s.Fog.HeightDensity, s.Fog.Height) = (3, new Color("e6d8ca"), 0.35f, 0.06f, 14);
            s.Post.Exposure = 1.15f;
        })),
        ("正午", () => With(s =>
        {
            (s.Sun.Hour, s.Sun.Energy, s.Sun.Color) = (12, 1.4f, new Color("fff8ec"));
            (s.Fog.Density, s.Post.Saturation) = (0.8f, 1.12f);
        })),
        ("黄昏", () => With(s =>
        {
            (s.Sun.Hour, s.Sun.Energy, s.Sun.Color, s.Sun.Warmth) = (17.4f, 1.15f, new Color("ffc48f"), 0.8f);
            (s.Sky.TopColor, s.Sky.HorizonColor, s.Sky.AmbientEnergy) = (new Color("4f6fa6"), new Color("f2bc93"), 0.45f);
            (s.Fog.Density, s.Fog.Color, s.Fog.SunScatter) = (2.5f, new Color("e8b997"), 0.5f);
            (s.Post.Exposure, s.Post.GlowIntensity) = (1.25f, 0.55f);
        })),
        ("阴天", () => With(s =>
        {
            (s.Sun.Energy, s.Sun.Color, s.Sun.ShadowBlur) = (0.35f, new Color("e8ecf0"), 4);
            (s.Sky.TopColor, s.Sky.HorizonColor, s.Sky.AmbientEnergy) = (new Color("9aa6b0"), new Color("cdd2d6"), 1.0f);
            (s.Fog.Density, s.Fog.Color) = (5, new Color("c4cacd"));
            (s.Post.Saturation, s.Post.Contrast) = (0.9f, 0.98f);
            s.Style.ShadowFill = 0.35f;
        })),
        ("薄雾", () => With(s =>
        {
            (s.Sun.Hour, s.Sun.Energy) = (9, 0.85f);
            (s.Sky.HorizonColor, s.Sky.AmbientEnergy) = (new Color("dae2e4"), 0.75f);
            (s.Fog.Density, s.Fog.Color, s.Fog.AerialPerspective) = (6, new Color("d8e0e2"), 0.6f);
            (s.Fog.HeightDensity, s.Fog.Height, s.Fog.SunScatter) = (0.12f, 16, 0.2f);
            s.Post.Saturation = 1.0f;
        })),
    };

    private static AtmosphereSettings With(Action<AtmosphereSettings> change)
    {
        var settings = new AtmosphereSettings();
        change(settings);
        return settings;
    }
}

/// <summary>
/// Sky, sun and air: a soft procedural sky that also lights the scene, a sun placed by the time of day with soft
/// shadows, distance haze toward the sky color, ambient occlusion, AgX tone mapping and a little glow. Edit
/// <see cref="Settings"/> and call <see cref="Apply"/>.
/// </summary>
public partial class Atmosphere : Node3D
{
    private static readonly Color Warm = new(1, 0.6f, 0.36f);

    private DirectionalLight3D _sun = null!;
    private ProceduralSkyMaterial _sky = null!;
    private Environment _environment = null!;

    public AtmosphereSettings Settings { get; } = new();

    public override void _Ready()
    {
        _sky = new ProceduralSkyMaterial { SkyCurve = 0.12f, SunAngleMax = 25, SunCurve = 0.12f };
        _environment = new Environment
        {
            BackgroundMode = Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky },
            AmbientLightSource = Environment.AmbientSource.Sky,
            TonemapMode = Environment.ToneMapper.Agx,
            // AgX is neutral and slightly flat; saturation and contrast restore some color.
            AdjustmentEnabled = true,
            FogMode = Environment.FogModeEnum.Exponential,
        };
        AddChild(new WorldEnvironment { Name = "Environment", Environment = _environment });
        _sun = new DirectionalLight3D { Name = "Sun", LightAngularDistance = 1.2f };
        AddChild(_sun);
        Apply();
    }

    /// <summary>Pushes <see cref="Settings"/> to the sun, sky, environment and the shared shader globals.</summary>
    public void Apply()
    {
        if (_sun == null)
            return;
        var (sun, sky, fog, post, style) = (Settings.Sun, Settings.Sky, Settings.Fog, Settings.Post, Settings.Style);

        float raw = sun.NoonElevation * Mathf.Sin(Mathf.Pi * (sun.Hour - 6) / 12);
        float elevation = Mathf.Max(raw, 1.5f);
        float azimuth = 90 + (sun.Hour - 6) * 15 + sun.AzimuthOffset;
        // A light shines along its -Z; facing away from the sun's compass bearing (north is -Z) means yaw 180° - bearing.
        _sun.RotationDegrees = new Vector3(-elevation, 180 - azimuth, 0);
        float low = 1 - Mathf.SmoothStep(2, 25, elevation);
        _sun.LightColor = sun.Color * Colors.White.Lerp(Warm, low * sun.Warmth);
        _sun.LightEnergy = sun.Energy * Mathf.Clamp((raw + 4) / 12, 0.15f, 1);
        _sun.ShadowEnabled = sun.Shadows;
        _sun.ShadowBlur = sun.ShadowBlur;

        _sky.SkyTopColor = sky.TopColor;
        _sky.SkyHorizonColor = sky.HorizonColor;
        _sky.SkyEnergyMultiplier = sky.Energy;
        // Below the horizon only the area around the map shows: keep it a pale haze, not a dark floor.
        _sky.GroundHorizonColor = sky.HorizonColor;
        _sky.GroundBottomColor = sky.HorizonColor.Darkened(0.08f);
        _sky.GroundEnergyMultiplier = sky.Energy;
        _environment.AmbientLightEnergy = sky.AmbientEnergy;

        _environment.FogEnabled = fog.Enabled;
        _environment.FogDensity = fog.Density * 1e-4f;
        _environment.FogLightColor = fog.Color;
        _environment.FogAerialPerspective = fog.AerialPerspective;
        _environment.FogSunScatter = fog.SunScatter;
        _environment.FogSkyAffect = fog.SkyAffect;
        _environment.FogHeight = fog.Height;
        _environment.FogHeightDensity = fog.HeightDensity;

        _environment.TonemapExposure = post.Exposure;
        _environment.AdjustmentSaturation = post.Saturation;
        _environment.AdjustmentContrast = post.Contrast;
        _environment.SsaoEnabled = post.Ssao;
        _environment.SsaoIntensity = post.SsaoIntensity;
        _environment.SsaoRadius = post.SsaoRadius;
        _environment.GlowEnabled = post.Glow;
        _environment.GlowIntensity = post.GlowIntensity;
        _environment.GlowBloom = post.GlowBloom;

        // Registered in project.godot [shader_globals]; read by shaders/ground.gdshader and painterly.gdshaderinc.
        RenderingServer.GlobalShaderParameterSet("style_toon", style.Toon);
        RenderingServer.GlobalShaderParameterSet("style_light_wrap", style.Wrap);
        RenderingServer.GlobalShaderParameterSet("style_shadow_fill", style.ShadowFill);
        var tint = style.ShadowTint.SrgbToLinear();
        RenderingServer.GlobalShaderParameterSet("style_shadow_tint", new Vector3(tint.R, tint.G, tint.B));
        RenderingServer.GlobalShaderParameterSet("wind_strength", style.WindStrength);
        float bearing = Mathf.DegToRad(style.WindBearing);
        RenderingServer.GlobalShaderParameterSet("wind_direction", new Vector2(Mathf.Sin(bearing), -Mathf.Cos(bearing)));
    }

    /// <summary>Spends the shadow map on what is in view: tight up close, wide when zoomed out.</summary>
    public void FitShadows(float cameraDistance) =>
        _sun.DirectionalShadowMaxDistance = Mathf.Clamp(cameraDistance * 3, 60, 8000);
}
