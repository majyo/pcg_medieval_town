using Godot;

namespace PcgCitySandbox.Ui;

/// <summary>Shared look for the few hand-built controls.</summary>
public static class UiStyle
{
    public static readonly Color Ink = new("e6ede0"), Muted = new("a8b6a8"), Accent = new("cbdca0");
    public static readonly Color Panel = new(0.075f, 0.12f, 0.10f, 0.94f);

    public static Label Text(string text, int size, Color color, bool wrap = true)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    public static Button Button(string text) =>
        new() { Text = text, CustomMinimumSize = new Vector2(0, 32), FocusMode = Control.FocusModeEnum.None };

    public static CheckBox Toggle(string text, bool on) =>
        new() { Text = text, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None };

    /// <summary>Docks <paramref name="panel"/> along the right edge of its parent with a fixed width.</summary>
    public static void DockRight(Control panel, float width)
    {
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);
        panel.OffsetLeft = -16 - width;
        panel.OffsetRight = -16;
        panel.OffsetTop = 16;
        panel.OffsetBottom = -16;
    }

    public static StyleBoxFlat Box(Color color) => new()
    {
        BgColor = color,
        CornerRadiusTopLeft = 10,
        CornerRadiusTopRight = 10,
        CornerRadiusBottomLeft = 10,
        CornerRadiusBottomRight = 10,
        ContentMarginLeft = 14,
        ContentMarginRight = 14,
        ContentMarginTop = 12,
        ContentMarginBottom = 12,
    };
}
