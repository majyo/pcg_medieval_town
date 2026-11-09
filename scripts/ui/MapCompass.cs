using System;
using Godot;

namespace PcgCitySandbox.Ui;

/// <summary>North arrow for any camera (north is -Z) and a scale bar for the meters-per-pixel it is given.</summary>
public partial class MapCompass : PanelContainer
{
    private readonly Control _canvas = new() { CustomMinimumSize = new Vector2(96, 112), MouseFilter = MouseFilterEnum.Ignore };

    public MapCompass()
    {
        Name = "Compass";
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeStyleboxOverride("panel", UiStyle.Box(UiStyle.Panel, 8, 4, 4));
        AddChild(_canvas);
        _canvas.Draw += Paint;
    }

    public Camera3D? Camera { get; set; }

    /// <summary>Ground size of one screen pixel where the scale applies (e.g. at the camera focus).</summary>
    public Func<float>? MetersPerPixel { get; set; }

    public override void _Process(double delta) => _canvas.QueueRedraw();

    private void Paint()
    {
        if (Camera == null)
            return;
        var ink = UiStyle.Ink;
        Vector2 c = new(48, 42);
        _canvas.DrawArc(c, 26, 0, Mathf.Tau, 48, UiStyle.Line.Lightened(0.15f), 1.3f, true);
        // A short step north from a point straight ahead shows which way north points on screen.
        var ahead = Camera.GlobalPosition - Camera.GlobalBasis.Z * 100;
        Vector2 n = (Camera.UnprojectPosition(ahead + Vector3.Forward) - Camera.UnprojectPosition(ahead)).Normalized();
        Vector2 side = new(-n.Y, n.X);
        _canvas.DrawColoredPolygon(new[] { c + n * 24, c - n * 4 + side * 7, c - n * 1, c - n * 4 - side * 7 }, UiStyle.Error);
        _canvas.DrawColoredPolygon(new[] { c - n * 22, c - n * 4 + side * 7, c - n * 1, c - n * 4 - side * 7 }, UiStyle.Muted);
        _canvas.DrawString(ThemeDB.FallbackFont, c + n * 36 + new Vector2(-5, 5), "N", fontSize: 14, modulate: ink);

        if (MetersPerPixel == null)
            return;
        float pixel = MetersPerPixel();
        float target = pixel * 70;
        float span = 1;
        foreach (float step in new[] { 2f, 5, 10, 20, 50, 100, 200, 500, 1000 })
        {
            if (step <= target)
                span = step;
        }

        float width = span / pixel;
        Vector2 a = new(48 - width * 0.5f, 88), b = a + new Vector2(width, 0);
        _canvas.DrawLine(a, b, ink, 2);
        _canvas.DrawLine(a - new Vector2(0, 4), a + new Vector2(0, 4), ink, 2);
        _canvas.DrawLine(b - new Vector2(0, 4), b + new Vector2(0, 4), ink, 2);
        string text = $"{span:0} m";
        float textWidth = ThemeDB.FallbackFont.GetStringSize(text, fontSize: 12).X;
        _canvas.DrawString(ThemeDB.FallbackFont, new Vector2(48 - textWidth / 2, 106), text, fontSize: 12, modulate: ink);
    }
}
