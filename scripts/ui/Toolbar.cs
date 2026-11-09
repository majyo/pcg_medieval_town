using System;
using System.Globalization;
using System.Linq;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Ui;

/// <summary>
/// The strip across the top: a title and whatever buttons, fields and switches the composition adds, left to right.
/// It only runs the callbacks it is given.
/// </summary>
public partial class Toolbar : PanelContainer
{
    private readonly HBoxContainer _row = new();

    public Toolbar()
    {
        Name = "Toolbar";
        AddThemeStyleboxOverride("panel", UiStyle.Box(UiStyle.Panel, 8, 10, 5));
        _row.AddThemeConstantOverride("separation", 6);
        AddChild(_row);
    }

    public void AddTitle(string text)
    {
        var label = UiStyle.Text(text, 16, UiStyle.Accent, wrap: false);
        label.SizeFlagsHorizontal = SizeFlags.Fill;
        _row.AddChild(label);
    }

    public Button AddButton(string text, string tooltip, Action pressed, bool accent = false, bool toggle = false)
    {
        var button = UiStyle.Button(text, tooltip, accent);
        button.ToggleMode = toggle;
        button.Pressed += pressed;
        _row.AddChild(button);
        return button;
    }

    /// <summary>A button that shows and changes a shared on/off setting.</summary>
    public Button AddToggle(string text, string tooltip, Setting<bool> setting)
    {
        var button = UiStyle.Button(text, tooltip);
        button.ToggleMode = true;
        button.ButtonPressed = setting.Value;
        button.Toggled += on => setting.Value = on;
        setting.Changed += on => button.SetPressedNoSignal(on);
        _row.AddChild(button);
        return button;
    }

    public void AddNumber(string label, Setting<int> setting, int min, int max, string tooltip)
    {
        var caption = UiStyle.Text(label, 12, UiStyle.Muted, wrap: false);
        caption.SizeFlagsHorizontal = SizeFlags.Fill;
        _row.AddChild(caption);
        var field = new LineEdit { CustomMinimumSize = new Vector2(76, 0), SelectAllOnFocus = true, TooltipText = tooltip };
        void Show(int value) => field.Text = value.ToString(CultureInfo.InvariantCulture);
        void Parse()
        {
            if (int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                setting.Value = Math.Clamp(value, min, max);
            Show(setting.Value);
        }

        field.TextSubmitted += _ =>
        {
            Parse();
            field.ReleaseFocus();
        };
        field.FocusExited += Parse;
        setting.Changed += Show;
        Show(setting.Value);
        _row.AddChild(field);
    }

    public void AddSeparator() => _row.AddChild(new VSeparator());

    public void AddSpacer() => _row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

    public void Add(Control control) => _row.AddChild(control);
}

/// <summary>How far the town graph is: a bar of valid nodes and a one-line state.</summary>
public partial class GraphProgress : HBoxContainer
{
    private readonly ProgressBar _bar = new()
    {
        ShowPercentage = false,
        CustomMinimumSize = new Vector2(110, 8),
        SizeFlagsVertical = SizeFlags.ShrinkCenter,
    };
    private readonly Label _label = UiStyle.Text("", 12, UiStyle.Muted, wrap: false);
    private CityGraph _graph = null!;

    public void Bind(CityGraph graph)
    {
        _graph = graph;
        AddThemeConstantOverride("separation", 8);
        _label.CustomMinimumSize = new Vector2(150, 0);
        AddChild(_bar);
        AddChild(_label);
        graph.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        int count = _graph.Nodes.Count;
        var statuses = Enumerable.Range(0, count).Select(_graph.Status).ToList();
        int valid = statuses.Count(s => s == NodeStatus.Valid);
        _bar.MaxValue = count;
        _bar.Value = valid;
        int running = statuses.IndexOf(NodeStatus.Running);
        int failed = statuses.Count(s => s == NodeStatus.Failed);
        (_label.Text, var color) = running >= 0
            ? ($"生成中 · {_graph.Nodes[running].Title}", UiStyle.StatusColor(NodeStatus.Running))
            : failed > 0
                ? ($"{failed} 个节点失败", UiStyle.Error)
                : valid == count
                    ? ($"全部有效 · {Enumerable.Range(0, count).Sum(_graph.Seconds):0.00} s", UiStyle.Muted)
                    : ($"{count - valid} 个节点待生成", UiStyle.Warning);
        _label.AddThemeColorOverride("font_color", color);
    }
}

/// <summary>The strip across the bottom: what is under the mouse, and a key hint.</summary>
public partial class StatusBar : PanelContainer
{
    private readonly Label _probe = UiStyle.Text("", 12, UiStyle.Ink, wrap: false);
    private readonly Label _hint = UiStyle.Text("", 11, UiStyle.Faint, wrap: false);
    private readonly Label _fps = UiStyle.Text("", 11, UiStyle.Faint, wrap: false);

    public StatusBar()
    {
        Name = "StatusBar";
        AddThemeStyleboxOverride("panel", UiStyle.Box(UiStyle.Panel, 6, 10, 3));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        AddChild(row);
        _probe.ClipText = true;
        _probe.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _hint.SizeFlagsHorizontal = SizeFlags.Fill;
        _fps.SizeFlagsHorizontal = SizeFlags.Fill;
        _fps.CustomMinimumSize = new Vector2(52, 0);
        _fps.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(_probe);
        row.AddChild(_hint);
        row.AddChild(_fps);
    }

    public string Probe { set => _probe.Text = value; }
    public string Hint { set => _hint.Text = value; }

    public override void _Process(double delta) => _fps.Text = $"{Engine.GetFramesPerSecond():0} fps";
}

/// <summary>Runs an action once, a short while after the last request (e.g. regenerate after a slider settles).</summary>
public partial class Debounce : Node
{
    private double _at = -1;
    private Action? _action;

    public void Schedule(Action action, double seconds)
    {
        _action = action;
        _at = Time.GetTicksMsec() / 1000.0 + seconds;
    }

    public override void _Process(double delta)
    {
        if (_at < 0 || Time.GetTicksMsec() / 1000.0 < _at)
            return;
        _at = -1;
        _action?.Invoke();
    }
}

/// <summary>Single-key shortcuts that apply when no text field has the keyboard.</summary>
public partial class Shortcuts : Node
{
    private readonly System.Collections.Generic.Dictionary<Key, Action> _keys = new();

    public void Map(Key key, Action action) => _keys[key] = action;

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } key && !key.CtrlPressed && !key.AltPressed &&
            _keys.TryGetValue(key.Keycode, out var action))
        {
            action();
            GetViewport().SetInputAsHandled();
        }
    }
}
