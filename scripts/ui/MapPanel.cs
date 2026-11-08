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
        box.AddChild(UiStyle.Text("滚轮缩放 · WASD/中键平移 · 右键旋转", 11, UiStyle.Muted));
    }

    public event Action<int>? RebuildRequested;
    public event Action? ResetViewRequested;

    public string Title { set => _title.Text = value; }
    public int Seed { get => (int)_seed.Value; set => _seed.Value = value; }
    public string Stats { set => _stats.Text = value; }
    public string Probe { set => _probe.Text = value; }

    public void AddChoice(string[] names, int selected, Action<int> changed)
    {
        var choice = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var name in names)
            choice.AddItem(name);
        choice.Selected = selected;
        choice.ItemSelected += i => changed((int)i);
        _controls.AddChild(choice);
    }

    public void AddToggle(string text, bool on, Action<bool> changed)
    {
        var check = UiStyle.Toggle(text, on);
        check.Toggled += value => changed(value);
        _controls.AddChild(check);
    }
}
