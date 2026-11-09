using Godot;

namespace PcgCitySandbox.Ui;

/// <summary>
/// The sandbox look: one dark, slightly translucent theme applied at the interface root, so every control inherits
/// it, plus the few colors and helpers the hand-built widgets use.
/// </summary>
public static class UiStyle
{
    public static readonly Color Ink = new("e8ecea"), Muted = new("9aa8a6"), Faint = new("6c7a78"), Accent = new("9fd0a8");
    public static readonly Color Warning = new("e3b25f"), Error = new("e47a6a");
    public static readonly Color Panel = new(0.075f, 0.095f, 0.105f, 0.93f);
    public static readonly Color Card = new("1b2327"), Field = new("11171a"), Line = new("2c373c");
    public static readonly Color ButtonFill = new("263034"), ButtonHover = new("2f3b40"), ButtonPressed = new("3b6a50");

    public const int FontSize = 13;

    public static Theme CreateTheme()
    {
        var theme = new Theme { DefaultFontSize = FontSize };

        theme.SetStylebox("panel", "PanelContainer", Box(Panel, 8, 10, 8));
        theme.SetStylebox("panel", "Panel", Box(Panel, 8, 10, 8));

        foreach (var type in new[] { "Button", "OptionButton", "MenuButton", "ColorPickerButton" })
        {
            theme.SetStylebox("normal", type, Box(ButtonFill, 5, 10, 4));
            theme.SetStylebox("hover", type, Box(ButtonHover, 5, 10, 4));
            theme.SetStylebox("pressed", type, Box(ButtonPressed, 5, 10, 4));
            theme.SetStylebox("hover_pressed", type, Box(ButtonPressed.Lightened(0.08f), 5, 10, 4));
            theme.SetStylebox("disabled", type, Box(ButtonFill.Darkened(0.3f), 5, 10, 4));
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetColor("font_color", type, Ink);
            theme.SetColor("font_hover_color", type, Colors.White);
            theme.SetColor("font_pressed_color", type, Colors.White);
            theme.SetColor("font_hover_pressed_color", type, Colors.White);
            theme.SetColor("font_focus_color", type, Ink);
            theme.SetColor("font_disabled_color", type, Faint);
        }

        foreach (var type in new[] { "CheckBox", "CheckButton" })
        {
            theme.SetStylebox("normal", type, new StyleBoxEmpty());
            theme.SetStylebox("hover", type, new StyleBoxEmpty());
            theme.SetStylebox("pressed", type, new StyleBoxEmpty());
            theme.SetStylebox("hover_pressed", type, new StyleBoxEmpty());
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetColor("font_color", type, Ink);
            theme.SetColor("font_hover_color", type, Colors.White);
            theme.SetColor("font_pressed_color", type, Ink);
            theme.SetColor("font_hover_pressed_color", type, Colors.White);
        }

        foreach (var (icon, on, dim) in new[]
        {
            ("checked", true, false), ("unchecked", false, false), ("checked_mirrored", true, false),
            ("unchecked_mirrored", false, false), ("checked_disabled", true, true), ("unchecked_disabled", false, true),
        })
        {
            theme.SetIcon(icon, "CheckButton", Switch(on, dim));
        }

        theme.SetStylebox("normal", "LineEdit", Box(Field, 4, 6, 3, Line));
        theme.SetStylebox("focus", "LineEdit", Box(Field, 4, 6, 3, Accent));
        theme.SetStylebox("read_only", "LineEdit", Box(Field, 4, 6, 3));
        theme.SetColor("font_color", "LineEdit", Ink);
        theme.SetColor("caret_color", "LineEdit", Accent);
        theme.SetColor("selection_color", "LineEdit", ButtonPressed);

        var track = Box(Line, 2, 0, 2);
        theme.SetStylebox("slider", "HSlider", track);
        theme.SetStylebox("grabber_area", "HSlider", Box(ButtonPressed.Lightened(0.15f), 2, 0, 2));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Box(ButtonPressed.Lightened(0.3f), 2, 0, 2));

        theme.SetStylebox("panel", "TabContainer", Box(new Color(0, 0, 0, 0), 0, 0, 6));
        theme.SetStylebox("tab_selected", "TabContainer", Tab(ButtonPressed, Accent));
        theme.SetStylebox("tab_unselected", "TabContainer", Tab(new Color(0, 0, 0, 0), null));
        theme.SetStylebox("tab_hovered", "TabContainer", Tab(ButtonHover, null));
        theme.SetStylebox("tabbar_background", "TabContainer", new StyleBoxEmpty());
        theme.SetColor("font_selected_color", "TabContainer", Colors.White);
        theme.SetColor("font_unselected_color", "TabContainer", Muted);
        theme.SetColor("font_hovered_color", "TabContainer", Ink);
        theme.SetFontSize("font_size", "TabContainer", 14);

        theme.SetStylebox("panel", "PopupMenu", Box(Card, 6, 6, 6, Line));
        theme.SetStylebox("hover", "PopupMenu", Box(ButtonPressed, 4, 6, 2));
        theme.SetColor("font_color", "PopupMenu", Ink);
        theme.SetColor("font_hover_color", "PopupMenu", Colors.White);
        theme.SetStylebox("panel", "TooltipPanel", Box(Card, 5, 8, 5, Line));
        theme.SetColor("font_color", "TooltipLabel", Ink);
        theme.SetFontSize("font_size", "TooltipLabel", 12);

        foreach (var type in new[] { "VScrollBar", "HScrollBar" })
        {
            theme.SetStylebox("scroll", type, Box(new Color(0, 0, 0, 0), 3, 3, 3));
            theme.SetStylebox("grabber", type, Box(Line.Lightened(0.1f), 3, 3, 3));
            theme.SetStylebox("grabber_highlight", type, Box(Line.Lightened(0.25f), 3, 3, 3));
            theme.SetStylebox("grabber_pressed", type, Box(Accent.Darkened(0.3f), 3, 3, 3));
        }

        theme.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Line, Thickness = 1 });
        theme.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Line, Thickness = 1, Vertical = true });
        theme.SetConstant("separation", "HSeparator", 10);
        theme.SetConstant("separation", "VSeparator", 12);
        theme.SetStylebox("background", "ProgressBar", Box(Field, 3, 0, 0));
        theme.SetStylebox("fill", "ProgressBar", Box(ButtonPressed.Lightened(0.15f), 3, 0, 0));
        theme.SetColor("font_color", "Label", Ink);
        return theme;
    }

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

    public static Button Button(string text, string? tooltip = null, bool accent = false)
    {
        var button = new Button { Text = text, TooltipText = tooltip ?? "", FocusMode = Control.FocusModeEnum.None };
        if (accent)
        {
            button.AddThemeStyleboxOverride("normal", Box(ButtonPressed, 5, 10, 4));
            button.AddThemeStyleboxOverride("hover", Box(ButtonPressed.Lightened(0.1f), 5, 10, 4));
        }

        return button;
    }

    /// <summary>A small borderless button for row and card actions.</summary>
    public static Button Mini(string text, string tooltip)
    {
        var button = new Button { Text = text, TooltipText = tooltip, Flat = true, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", 12);
        button.AddThemeColorOverride("font_color", Muted);
        button.AddThemeColorOverride("font_hover_color", Accent);
        button.AddThemeStyleboxOverride("normal", Box(new Color(0, 0, 0, 0), 4, 5, 1));
        button.AddThemeStyleboxOverride("hover", Box(ButtonHover, 4, 5, 1));
        button.AddThemeStyleboxOverride("pressed", Box(ButtonPressed, 4, 5, 1));
        return button;
    }

    public static Color StatusColor(City.NodeStatus status) => status switch
    {
        City.NodeStatus.Valid => new Color("7cc68d"),
        City.NodeStatus.Running => new Color("6fb3e8"),
        City.NodeStatus.Stale => Warning,
        City.NodeStatus.Failed => Error,
        City.NodeStatus.Blocked => new Color("a08a62"),
        _ => Faint,
    };

    public static string StatusText(City.NodeStatus status) => status switch
    {
        City.NodeStatus.Valid => "有效",
        City.NodeStatus.Running => "运行中",
        City.NodeStatus.Stale => "已过期",
        City.NodeStatus.Failed => "失败",
        City.NodeStatus.Blocked => "等待上游",
        _ => "未运行",
    };

    public static StyleBoxFlat Box(Color color, int radius, int marginX, int marginY, Color? border = null) => new()
    {
        BgColor = color,
        CornerRadiusTopLeft = radius,
        CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius,
        CornerRadiusBottomRight = radius,
        ContentMarginLeft = marginX,
        ContentMarginRight = marginX,
        ContentMarginTop = marginY,
        ContentMarginBottom = marginY,
        BorderColor = border ?? Colors.Transparent,
        BorderWidthLeft = border == null ? 0 : 1,
        BorderWidthRight = border == null ? 0 : 1,
        BorderWidthTop = border == null ? 0 : 1,
        BorderWidthBottom = border == null ? 0 : 1,
    };

    /// <summary>A pill-shaped switch: the knob on the right and the track lit when on.</summary>
    private static ImageTexture Switch(bool on, bool dim)
    {
        const int width = 32, height = 18;
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        var track = on ? new Color("3f8f62") : new Color("3a464a");
        var knob = on ? Colors.White : new Color("aab6b4");
        float r = height / 2f, knobX = on ? width - r : r;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                // Signed distances to the pill and to the knob; one pixel of falloff for anti-aliasing.
                float pill = new Vector2(Mathf.Max(Mathf.Abs(p.X - width / 2f) - (width / 2f - r), 0), p.Y - r).Length() - r;
                float dot = p.DistanceTo(new Vector2(knobX, r)) - (r - 3);
                var color = track;
                color.A = Mathf.Clamp(0.5f - pill, 0, 1);
                color = color.Lerp(knob, Mathf.Clamp(0.5f - dot, 0, 1));
                if (dim)
                    color.A *= 0.45f;
                image.SetPixel(x, y, color);
            }
        }

        return ImageTexture.CreateFromImage(image);
    }

    private static StyleBoxFlat Tab(Color color, Color? underline)
    {
        var box = Box(color, 5, 12, 5);
        box.CornerRadiusBottomLeft = box.CornerRadiusBottomRight = 0;
        if (underline != null)
        {
            box.BorderColor = underline.Value;
            box.BorderWidthBottom = 2;
        }

        return box;
    }
}
