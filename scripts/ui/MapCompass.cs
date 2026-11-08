using Godot;

namespace PcgCitySandbox.Ui;

/// <summary>North arrow and scale bar for any orthographic camera (north is -Z).</summary>
public partial class MapCompass : Control
{
    public Camera3D? Camera { get; set; }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (Camera == null)
            return;
        Color ink = new("344e46");
        Vector2 c = new(55, 48);
        DrawArc(c, 25, 0, Mathf.Tau, 48, new Color(0.2f, 0.3f, 0.26f, 0.25f), 1.3f, true);
        // Any point in front of the camera works: the projection is parallel.
        var ahead = Camera.GlobalPosition - Camera.GlobalBasis.Z * 100;
        Vector2 n = (Camera.UnprojectPosition(ahead + Vector3.Forward * 100) - Camera.UnprojectPosition(ahead)).Normalized();
        Vector2 side = new(-n.Y, n.X);
        DrawColoredPolygon(new[] { c + n * 25, c - n * 10 + side * 6, c - n * 4, c - n * 10 - side * 6 }, ink);
        DrawString(ThemeDB.FallbackFont, c + n * 38 + new Vector2(-6, 5), "N", fontSize: 16, modulate: ink);

        float target = Camera.Size / GetViewportRect().Size.Y * 80;
        float span = 5;
        foreach (float step in new[] { 10f, 20, 50, 100, 200, 500 })
        {
            if (step <= target)
                span = step;
        }

        float width = span / Camera.Size * GetViewportRect().Size.Y;
        Vector2 a = new(55 - width * 0.5f, 102), b = a + new Vector2(width, 0);
        DrawLine(a, b, ink, 2);
        DrawLine(a - new Vector2(0, 4), a + new Vector2(0, 4), ink, 2);
        DrawLine(b - new Vector2(0, 4), b + new Vector2(0, 4), ink, 2);
        DrawString(ThemeDB.FallbackFont, new Vector2(33, 125), $"{span:0} m", fontSize: 13, modulate: ink);
    }
}
