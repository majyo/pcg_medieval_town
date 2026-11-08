using Godot;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// Sky, sun and air: a soft procedural sky that also lights the scene, an afternoon sun with soft shadows,
/// distance haze toward the sky color, ambient occlusion, AgX tone mapping and a little glow.
/// </summary>
public partial class Atmosphere : Node3D
{
    private DirectionalLight3D _sun = null!;

    public override void _Ready()
    {
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color("5d8fc4"),
            SkyHorizonColor = new Color("c5d8de"),
            SkyCurve = 0.12f,
            // Below the horizon only the area around the map shows: keep it a pale haze, not a dark floor.
            GroundHorizonColor = new Color("c5d5d6"),
            GroundBottomColor = new Color("b4c3c2"),
            SunAngleMax = 25,
            SunCurve = 0.12f,
        };
        AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Environment = new Environment
            {
                BackgroundMode = Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.55f,
                TonemapMode = Environment.ToneMapper.Agx,
                TonemapExposure = 1.1f,
                // AgX is neutral and slightly flat; restore some color.
                AdjustmentEnabled = true,
                AdjustmentSaturation = 1.18f,
                AdjustmentContrast = 1.04f,
                SsaoEnabled = true,
                SsaoRadius = 1.6f,
                SsaoIntensity = 1.4f,
                GlowEnabled = true,
                GlowIntensity = 0.35f,
                GlowBloom = 0.03f,
                FogEnabled = true,
                FogLightColor = new Color("c2d4dc"),
                FogDensity = 0.00012f,
                FogAerialPerspective = 0.3f,
                FogSkyAffect = 0,
            },
        });

        _sun = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-36, -52, 0),
            LightColor = new Color("fff0da"),
            LightEnergy = 1.25f,
            LightAngularDistance = 1.2f,
            ShadowEnabled = true,
            ShadowBlur = 1.2f,
        };
        AddChild(_sun);
    }

    /// <summary>Spends the shadow map on what is in view: tight up close, wide when zoomed out.</summary>
    public void FitShadows(float cameraDistance) =>
        _sun.DirectionalShadowMaxDistance = Mathf.Clamp(cameraDistance * 3, 60, 8000);
}
