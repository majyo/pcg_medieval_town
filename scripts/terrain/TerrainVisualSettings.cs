using Godot;
using System;

namespace PcgCitySandbox;

public enum TerrainVisualQuality { Simple, Standard, Detailed }

[GlobalClass]
public partial class TerrainVisualSettings : Resource
{
    [Export] public TerrainVisualQuality Quality { get; set; } = TerrainVisualQuality.Standard;
    [Export] public Color Meadow { get; set; } = new("738363");
    [Export] public Color DryGrass { get; set; } = new("898767");
    [Export] public Color ForestFloor { get; set; } = new("555e45");
    [Export] public Color Soil { get; set; } = new("897965");
    [Export] public Color WetMud { get; set; } = new("75705d");
    [Export] public Color Gravel { get; set; } = new("928b73");
    [Export(PropertyHint.Range, "0,0.4,0.01")] public float MacroVariation { get; set; } = .08f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float DetailStrength { get; set; } = .65f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float NormalStrength { get; set; } = .3f;
    [Export(PropertyHint.Range, "0.5,2,0.1")] public float BankWidth { get; set; } = 1;
    [Export(PropertyHint.Range, "0,2,0.05")] public float FlowSpeed { get; set; } = .7f;
    public void Validate()
    {
        if (!Enum.IsDefined(Quality) || !float.IsFinite(MacroVariation) || MacroVariation is < 0 or > .4f ||
            !float.IsFinite(DetailStrength) || DetailStrength is < 0 or > 1 || !float.IsFinite(NormalStrength) || NormalStrength is < 0 or > 1 ||
            !float.IsFinite(BankWidth) || BankWidth is < .5f or > 2 || !float.IsFinite(FlowSpeed) || FlowSpeed is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(TerrainVisualSettings));
    }
}
