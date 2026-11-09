using System;
using Godot;

namespace PcgCitySandbox.Ui;

/// <summary>
/// Left-hand panel: map seed and rebuild, view choices and toggles, a status line and a probe line.
/// It only raises events and runs the callbacks it is given; what they do is decided by whoever builds it.
/// </summary>
public partial class MapPanel : PanelContainer
{
    private readonly Label _title = UiStyle.Text("", 18, UiStyle.Accent);
    private readonly Label _stats = UiStyle.Text("", 12, UiStyle.Muted);
    private readonly Label _probe = UiStyle.Text("", 12, UiStyle.Muted);
    private readonly SpinBox _seed = new() { MinValue = 0, MaxValue = 999999, SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly VBoxContainer _controls = new();
    private readonly VBoxContainer _legend = new() { Visible = false };
    private readonly Label _legendCaption = UiStyle.Text("", 11, UiStyle.Muted);
    private readonly TextureRect _legendRamp = new()
    {
        CustomMinimumSize = new Vector2(0, 10),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.Scale,
    };
    private readonly Label _legendLow = UiStyle.Text("", 11, UiStyle.Muted, wrap: false);
    private readonly Label _legendHigh = UiStyle.Text("", 11, UiStyle.Muted, wrap: false);

    public MapPanel()
    {
        Name = "Map";
        CustomMinimumSize = new Vector2(250, 0);
        AddThemeStyleboxOverride("panel", UiStyle.Box(UiStyle.Panel));

        var box = new VBoxContainer();
        AddChild(box);
        box.AddChild(_title);
        box.AddChild(_stats);

        var seedRow = new HBoxContainer();
        var rebuild = UiStyle.Button("重建地图");
        rebuild.Pressed += () => RebuildRequested?.Invoke((int)_seed.Value);
        seedRow.AddChild(_seed);
        seedRow.AddChild(rebuild);
        box.AddChild(seedRow);

        box.AddChild(_controls);
        var reset = UiStyle.Button("复位视角 (Home)");
        reset.Pressed += () => ResetViewRequested?.Invoke();
        box.AddChild(reset);
        box.AddChild(_probe);
        _legend.AddChild(_legendCaption);
        _legend.AddChild(_legendRamp);
        var ends = new HBoxContainer();
        _legendHigh.HorizontalAlignment = HorizontalAlignment.Right;
        ends.AddChild(_legendLow);
        ends.AddChild(_legendHigh);
        _legend.AddChild(ends);

        box.AddChild(UiStyle.Text("滚轮缩放 · WASD/中键平移 · 右键旋转", 11, UiStyle.Muted));
    }

    public event Action<int>? RebuildRequested;
    public event Action? ResetViewRequested;

    public string Title { set => _title.Text = value; }
    public int Seed { get => (int)_seed.Value; set => _seed.Value = value; }
    public string Stats { set => _stats.Text = value; }
    public string Probe { set => _probe.Text = value; }

    /// <summary>Adds a drop-down; the returned control lets the caller change the selection.</summary>
    public OptionButton AddChoice(string[] names, int selected, Action<int> changed)
    {
        var choice = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var name in names)
            choice.AddItem(name);
        choice.Selected = selected;
        choice.ItemSelected += i => changed((int)i);
        _controls.AddChild(choice);
        return choice;
    }

    /// <summary>Places the legend (hidden until <see cref="ShowLegend"/>) at this point in the controls.</summary>
    public void AddLegend() => _controls.AddChild(_legend);

    /// <summary>A color ramp from <paramref name="low"/> to <paramref name="high"/> under a caption.</summary>
    public void ShowLegend(string caption, Func<float, Color> ramp, string low, string high)
    {
        var gradient = new Gradient();
        const int stops = 33;
        var offsets = new float[stops];
        var colors = new Color[stops];
        for (int i = 0; i < stops; i++)
        {
            offsets[i] = i / (float)(stops - 1);
            colors[i] = ramp(offsets[i]);
        }

        gradient.Offsets = offsets;
        gradient.Colors = colors;
        _legendRamp.Texture = new GradientTexture1D { Gradient = gradient, Width = 128 };
        _legendCaption.Text = caption;
        _legendLow.Text = low;
        _legendHigh.Text = high;
        _legend.Visible = true;
    }

    public void HideLegend() => _legend.Visible = false;

    /// <summary>Adds a check box; the returned control lets the caller change it without raising the event.</summary>
    public CheckBox AddToggle(string text, bool on, Action<bool> changed)
    {
        var check = UiStyle.Toggle(text, on);
        check.Toggled += value => changed(value);
        _controls.AddChild(check);
        return check;
    }
}
