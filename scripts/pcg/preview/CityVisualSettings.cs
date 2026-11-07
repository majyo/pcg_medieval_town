using Godot;
namespace PcgCitySandbox.Pcg;

public enum CityViewMode { Model, Analysis, Issues }
[GlobalClass]
public partial class CityVisualSettings : Resource
{
    [Export(PropertyHint.Range, "2,10,0.5")] public float WallHeight { get; set; } = 6;
    [Export(PropertyHint.Range, "100,600,25")] public float DetailViewSize { get; set; } = 300;
    [Export] public bool Labels { get; set; } = true;
    [Export] public bool Details { get; set; } = true;
    public void Validate()
    {
        if (!float.IsFinite(WallHeight) || WallHeight is < 2 or > 10 || !float.IsFinite(DetailViewSize) || DetailViewSize is < 100 or > 600)
            throw new System.ArgumentOutOfRangeException(nameof(CityVisualSettings));
    }
}
public static class CityPalette
{
    public static readonly Color Residential = new("92aca0"), Commercial = new("c3a16d"), Production = new("a59bb5"), Civic = new("91a9b6");
    public static Color Program(string name) => name switch { "住宅" => Residential, "商住" => Commercial, "作坊住宅" => Production, _ => Civic };
    public static Color Use(ParcelUse use) => use switch { ParcelUse.Commercial => Commercial, ParcelUse.Production => Production, _ => Residential };
    public static int Role(string program) => program switch { "住宅" => 1, "商住" => 2, "作坊住宅" => 3, _ => 4 };
    public static Color Ground(string kind) => kind switch { "路肩" => new("a29479"), "主路" => new("9b896e"), "街巷" => new("8b7c65"), "广场" => new("a7a28c"), "院场" => new("a39c83"), "通院地面" => new("9a9179"), "作坊院地" => new("95836d"), "后园" => new("738363"), "田地" => new("99906a"), "果园" => new("748260"), "草地" => new("7a876b"), _ => new("8e927b") };
}
