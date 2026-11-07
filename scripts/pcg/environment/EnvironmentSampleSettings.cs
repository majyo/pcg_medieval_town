using Godot;
using System;

namespace PcgCitySandbox.Pcg;

[GlobalClass]
public partial class EnvironmentSampleSettings : Resource
{
    [Export] public Rect2 Bounds { get; set; } = new(-1000, -1000, 2000, 2000);
    [Export] public float TargetSpacing { get; set; } = 10;
    [Export] public float MaxGroundSlope { get; set; } = 12;
    [Export] public bool ReadVegetation { get; set; } = true;
    public EnvironmentSampleOptions Snapshot() => new(Bounds, TargetSpacing, MaxGroundSlope, ReadVegetation);
}

public readonly record struct EnvironmentSampleOptions(Rect2 Bounds, float TargetSpacing = 10,
    float MaxGroundSlope = 12, bool ReadVegetation = true)
{
    public const long MaxQueries = 1_000_000;
    public (int X, int Z) ValidateGrid()
    {
        if (!Bounds.Position.IsFinite() || !Bounds.Size.IsFinite() || !Bounds.End.IsFinite() ||
            Bounds.Size.X <= 0 || Bounds.Size.Y <= 0 || !float.IsFinite(TargetSpacing) || TargetSpacing <= 0 ||
            !float.IsFinite(MaxGroundSlope) || MaxGroundSlope < 0 || MaxGroundSlope > 90)
            throw new ArgumentException("采样范围、间距或坡度参数无效。");
        double x = Math.Ceiling((double)Bounds.Size.X / TargetSpacing);
        double z = Math.Ceiling((double)Bounds.Size.Y / TargetSpacing);
        if (x < 1 || z < 1 || x > MaxQueries || z > MaxQueries || (x + 1) * (z + 1) + x * z > MaxQueries)
            throw new ArgumentException($"采样超过 {MaxQueries:N0} 次查询预算，请缩小范围或增大间距。");
        // Reject ranges too small to represent a distinct grid in world-space float coordinates.
        float dx = Bounds.Size.X / (int)x, dz = Bounds.Size.Y / (int)z;
        if (Bounds.Position.X + dx == Bounds.Position.X || Bounds.End.X - dx == Bounds.End.X ||
            Bounds.Position.Y + dz == Bounds.Position.Y || Bounds.End.Y - dz == Bounds.End.Y)
            throw new ArgumentException("采样间距小于当前世界坐标的数值精度。");
        return ((int)x, (int)z);
    }
}
