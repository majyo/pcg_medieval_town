using Godot;

namespace PcgCitySandbox;

public partial class MapCamera : Camera3D
{
    public Vector3 Focus { get; set; } = new(0, 12, 0);
    public float Yaw { get; set; } = -0.38f;
    public float Pitch { get; set; } = 0.83f;
    public float MapSize { get; set; } = 2000;
    private bool _rotating, _panning;

    public override void _Ready()
    {
        Projection = ProjectionType.Orthogonal; Near = 1; Far = 10000; Current = true;
        ResetView();
    }

    public void ResetView()
    {
        // Leave the center of the viewport clear between the map and sampling panels.
        Focus = new Vector3(0, 15, 0); Yaw = -0.38f; Pitch = 0.83f; Size = MapSize * 1.65f;
        UpdateTransform();
    }

    public void TopView()
    {
        Focus = new Vector3(0, 15, 0); Yaw = 0; Pitch = 1.55f; Size = MapSize * 1.40f;
        UpdateTransform();
    }

    public override void _Input(InputEvent e)
    {
        // Release drags even if the pointer moved over a GUI control.
        if (e is InputEventMouseButton { Pressed: false } b)
        { if (b.ButtonIndex == MouseButton.Right) _rotating = false; if (b.ButtonIndex == MouseButton.Middle) _panning = false; }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton b)
        {
            if (b.ButtonIndex == MouseButton.Right) _rotating = b.Pressed;
            if (b.ButtonIndex == MouseButton.Middle) _panning = b.Pressed;
            if (b.Pressed && b.ButtonIndex == MouseButton.WheelUp) Size = Mathf.Max(25, Size * 0.88f);
            if (b.Pressed && b.ButtonIndex == MouseButton.WheelDown) Size = Mathf.Min(MapSize * 1.8f, Size / 0.88f);
        }
        if (e is InputEventMouseMotion m)
        {
            if (_rotating) { Yaw -= m.Relative.X * 0.005f; Pitch = Mathf.Clamp(Pitch + m.Relative.Y * 0.005f, 0.22f, 1.55f); }
            if (_panning)
            {
                float pixel = Size / GetViewport().GetVisibleRect().Size.Y;
                Vector3 right = new(Mathf.Cos(Yaw), 0, -Mathf.Sin(Yaw));
                Vector3 forward = new(Mathf.Sin(Yaw), 0, Mathf.Cos(Yaw));
                Focus += (-right * m.Relative.X - forward * m.Relative.Y / Mathf.Max(Mathf.Sin(Pitch), 0.3f)) * pixel;
            }
        }
        UpdateTransform();
    }

    public override void _Process(double delta)
    {
        if (GetViewport().GuiGetFocusOwner() is LineEdit or SpinBox) return;
        Vector2 movement = new((Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0));
        if (movement.LengthSquared() > 0)
        {
            Vector3 right = new(Mathf.Cos(Yaw), 0, -Mathf.Sin(Yaw));
            Vector3 forward = new(Mathf.Sin(Yaw), 0, Mathf.Cos(Yaw));
            Focus += (right * movement.X + forward * movement.Y).Normalized() * Size * (float)delta * 0.45f;
        }
        if (Input.IsPhysicalKeyPressed(Key.Q)) Yaw += (float)delta * 0.65f;
        if (Input.IsPhysicalKeyPressed(Key.E)) Yaw -= (float)delta * 0.65f;
        UpdateTransform();
    }

    public void UpdateTransform()
    {
        Focus = new Vector3(Mathf.Clamp(Focus.X, -MapSize * 0.7f, MapSize * 0.7f), Focus.Y, Mathf.Clamp(Focus.Z, -MapSize * 0.7f, MapSize * 0.7f));
        Vector3 direction = new(Mathf.Sin(Yaw) * Mathf.Cos(Pitch), Mathf.Sin(Pitch), Mathf.Cos(Yaw) * Mathf.Cos(Pitch));
        Position = Focus + direction * 3400;
        LookAt(Focus, Vector3.Up);
    }
}
