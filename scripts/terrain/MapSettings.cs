using Godot;

namespace PcgCitySandbox.Terrain;

[GlobalClass]
public partial class MapSettings : Resource
{
    [Export] public int Seed { get; set; } = 73129;
    [Export(PropertyHint.Range, "1000,4000,100")] public float SizeMeters { get; set; } = 2000;
    [Export(PropertyHint.Range, "128,512,32")] public int GridSegments { get; set; } = 384;
    [Export(PropertyHint.Range, "5,50,1")] public float ReliefMeters { get; set; } = 26;
    [Export(PropertyHint.Range, "20,100,1")] public float RiverWidthMeters { get; set; } = 48;
    [Export(PropertyHint.Range, "0,1,0.05")] public float ForestDensity { get; set; } = 0.76f;
    [Export(PropertyHint.Range, "10,30,1")] public float TreeSpacingMeters { get; set; } = 15;

    public void Validate()
    {
        if (SizeMeters < 1000 || SizeMeters > 4000 || GridSegments < 128 || GridSegments > 512 ||
            ReliefMeters < 5 || ReliefMeters > 50 || RiverWidthMeters < 20 || RiverWidthMeters > 100 ||
            ForestDensity < 0 || ForestDensity > 1 || TreeSpacingMeters < 10 || TreeSpacingMeters > 30)
            throw new System.ArgumentOutOfRangeException(nameof(MapSettings), "地图参数超出支持范围。");
    }
}
