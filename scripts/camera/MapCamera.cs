using System;
using Godot;

namespace PcgCitySandbox.CameraControl;

/// <summary>
/// Perspective camera orbiting a focus point on the ground. Mouse: wheel zoom, right-drag rotate, middle-drag pan.
/// Keys: WASD pan, Q/E rotate, Home reset. Knows only its bounds and an optional ground-height callback;
/// nothing about terrain, town or UI.
/// </summary>
public partial class MapCamera : Camera3D
{
    private const float MinDistance = 6, MinPitch = 0.06f, MaxPitch = 1.55f, GroundClearance = 1.5f;
    private bool _rotating, _panning;

    public Vector3 Focus { get; set; } = new(0, 15, 0);
    public float Yaw { get; set; } = -0.38f;
    public float Pitch { get; set; } = 0.83f;

    /// <summary>Distance from the camera to the focus point, in meters.</summary>
    public float Distance { get; set; } = 3200;

    /// <summary>Side length of the square area the focus may move over, centered on the origin.</summary>
    public float MapSize { get; set; } = 2000;

    /// <summary>
    /// Ground height under a horizontal position. When set, the focus sits on the ground and the camera never
    /// dips below it.
    /// </summary>
    public Func<Vector2, float>? GroundHeight { get; set; }

    private float MaxDistance => MapSize * 2.2f;

    public override void _Ready()
    {
        Projection = ProjectionType.Perspective;
        Fov = 40;
        Current = true;
        ResetView();
    }

    public void ResetView() => Frame(new Vector3(0, Focus.Y, 0), 0.83f, -0.38f, MapSize * 1.6f);

    public void TopView() => Frame(new Vector3(0, Focus.Y, 0), MaxPitch, 0, MapSize * 1.5f);

    /// <summary>Looks at <paramref name="focus"/> from the given angles and distance.</summary>
    public void Frame(Vector3 focus, float pitch, float yaw, float distance)
    {
        Focus = focus;
        Pitch = pitch;
        Yaw = yaw;
        Distance = distance;
        UpdateTransform();
    }

    /// <summary>Size of one screen pixel at the focus point, in meters.</summary>
    public float MetersPerPixel() =>
        2 * Distance * Mathf.Tan(Mathf.DegToRad(Fov) / 2) / GetViewport().GetVisibleRect().Size.Y;

    public override void _Input(InputEvent e)
    {
        // Release drags even if the pointer moved over a GUI control.
        if (e is InputEventMouseButton { Pressed: false } b)
        {
            if (b.ButtonIndex == MouseButton.Right)
                _rotating = false;
            if (b.ButtonIndex == MouseButton.Middle)
                _panning = false;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Home })
            ResetView();
        if (e is InputEventMouseButton b)
        {
            if (b.ButtonIndex == MouseButton.Right)
                _rotating = b.Pressed;
            if (b.ButtonIndex == MouseButton.Middle)
                _panning = b.Pressed;
            if (b.Pressed && b.ButtonIndex == MouseButton.WheelUp)
                Distance = Mathf.Max(MinDistance, Distance * 0.88f);
            if (b.Pressed && b.ButtonIndex == MouseButton.WheelDown)
                Distance = Mathf.Min(MaxDistance, Distance / 0.88f);
        }

        if (e is InputEventMouseMotion m)
        {
            if (_rotating)
            {
                Yaw -= m.Relative.X * 0.005f;
                Pitch = Mathf.Clamp(Pitch + m.Relative.Y * 0.005f, MinPitch, MaxPitch);
            }

            if (_panning)
            {
                float pixel = MetersPerPixel();
                Focus += (-Right() * m.Relative.X - Forward() * m.Relative.Y / Mathf.Max(Mathf.Sin(Pitch), 0.3f)) * pixel;
            }
        }

        UpdateTransform();
    }

    public override void _Process(double delta)
    {
        // Typing into any text field must not move the camera.
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)
            return;
        Vector2 movement = new((Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0));
        if (movement.LengthSquared() > 0)
            Focus += (Right() * movement.X + Forward() * movement.Y).Normalized() * Distance * (float)delta * 0.6f;
        if (Input.IsPhysicalKeyPressed(Key.Q))
            Yaw += (float)delta * 0.65f;
        if (Input.IsPhysicalKeyPressed(Key.E))
            Yaw -= (float)delta * 0.65f;
        UpdateTransform();
    }

    public void UpdateTransform()
    {
        float limit = MapSize * 0.7f;
        Distance = Mathf.Clamp(Distance, MinDistance, MaxDistance);
        Pitch = Mathf.Clamp(Pitch, MinPitch, MaxPitch);
        var focus = new Vector2(Mathf.Clamp(Focus.X, -limit, limit), Mathf.Clamp(Focus.Z, -limit, limit));
        Focus = new Vector3(focus.X, GroundHeight?.Invoke(focus) ?? Focus.Y, focus.Y);

        Vector3 direction = new(Mathf.Sin(Yaw) * Mathf.Cos(Pitch), Mathf.Sin(Pitch), Mathf.Cos(Yaw) * Mathf.Cos(Pitch));
        var position = Focus + direction * Distance;
        if (GroundHeight != null)
            position.Y = Mathf.Max(position.Y, GroundHeight(new Vector2(position.X, position.Z)) + GroundClearance);
        Position = position;

        // Keep depth precision proportional to how close we are; the far plane always reaches past the map.
        Near = Mathf.Clamp(Distance * 0.01f, 0.05f, 5);
        Far = Distance + MapSize * 2.5f;
        LookAt(Focus, Vector3.Up);
    }

    private Vector3 Right() => new(Mathf.Cos(Yaw), 0, -Mathf.Sin(Yaw));

    private Vector3 Forward() => new(Mathf.Sin(Yaw), 0, Mathf.Cos(Yaw));
}
