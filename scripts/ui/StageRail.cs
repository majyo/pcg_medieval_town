using System;
using System.Collections.Generic;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Ui;

/// <summary>
/// Left-hand overview of the workflow: every stage with its overall status, the nodes under it with theirs.
/// Clicking a stage selects it; clicking a node selects its stage and raises <see cref="NodeClicked"/>.
/// </summary>
public partial class StageRail : PanelContainer
{
    private readonly List<(Button Button, StatusDot Dot, Label Count)> _stages = new();
    private readonly List<(StatusDot Dot, Label Time)> _nodes = new();
    private CityGraph _graph = null!;
    private IReadOnlyList<Stage> _list = null!;
    private Setting<int> _selected = null!;

    public event Action<int>? NodeClicked;

    public void Bind(CityGraph graph, IReadOnlyList<Stage> stages, Setting<int> selected)
    {
        _graph = graph;
        _list = stages;
        _selected = selected;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        var head = new HBoxContainer();
        head.AddChild(UiStyle.Text("生成阶段", 14, UiStyle.Ink, wrap: false));
        var hint = UiStyle.Text("按 1–" + Math.Min(stages.Count, 9) + " 切换", 11, UiStyle.Faint, wrap: false);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        head.AddChild(hint);
        box.AddChild(head);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        box.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 1);
        scroll.AddChild(list);

        var group = new ButtonGroup();
        for (int s = 0; s < stages.Count; s++)
        {
            int index = s;
            list.AddChild(StageButton(index, stages[index], group));
            foreach (int node in stages[index].Nodes)
                list.AddChild(NodeButton(index, node));
            list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        }

        selected.Changed += _ => Refresh();
        graph.Changed += Refresh;
        Refresh();
    }

    private Button StageButton(int index, Stage stage, ButtonGroup group)
    {
        var button = new Button
        {
            ToggleMode = true,
            ButtonGroup = group,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 32),
        };
        button.AddThemeStyleboxOverride("normal", UiStyle.Box(new Color(1, 1, 1, 0.04f), 5, 8, 4));
        button.AddThemeStyleboxOverride("hover", UiStyle.Box(UiStyle.ButtonHover, 5, 8, 4));
        var selectedBox = UiStyle.Box(UiStyle.ButtonPressed.Darkened(0.2f), 5, 8, 4, UiStyle.Accent.Darkened(0.2f));
        button.AddThemeStyleboxOverride("pressed", selectedBox);
        button.AddThemeStyleboxOverride("hover_pressed", selectedBox);
        button.Pressed += () => _selected.Value = index;

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        button.AddChild(row);
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        row.OffsetLeft = 8;
        row.OffsetRight = -8;
        var number = UiStyle.Text(index < 9 ? (index + 1).ToString() : "", 11, UiStyle.Faint, wrap: false);
        number.SizeFlagsHorizontal = SizeFlags.Fill;
        number.CustomMinimumSize = new Vector2(10, 0);
        var title = UiStyle.Text(stage.Title, 13, UiStyle.Ink, wrap: false);
        var count = UiStyle.Text("", 11, UiStyle.Muted, wrap: false);
        count.SizeFlagsHorizontal = SizeFlags.Fill;
        var dot = new StatusDot(9);
        foreach (var control in new Control[] { number, title, count, dot })
        {
            control.MouseFilter = MouseFilterEnum.Ignore;
            control.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(control);
        }

        _stages.Add((button, dot, count));
        return button;
    }

    private Button NodeButton(int stage, int node)
    {
        var button = new Button { Flat = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 22) };
        button.AddThemeStyleboxOverride("hover", UiStyle.Box(new Color(1, 1, 1, 0.05f), 4, 4, 1));
        var info = _graph.Nodes[node];
        button.TooltipText = UiStyle.Tip(info.Title, info.Description) + "\n\n点击查看参数";
        button.Pressed += () =>
        {
            _selected.Value = stage;
            NodeClicked?.Invoke(node);
        };
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 7);
        button.AddChild(row);
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        row.OffsetLeft = 26;
        row.OffsetRight = -8;
        var dot = new StatusDot(6);
        var title = UiStyle.Text(_graph.Nodes[node].Title, 12, UiStyle.Muted, wrap: false);
        var time = UiStyle.Text("", 11, UiStyle.Faint, wrap: false);
        time.SizeFlagsHorizontal = SizeFlags.Fill;
        foreach (var control in new Control[] { dot, title, time })
        {
            control.MouseFilter = MouseFilterEnum.Ignore;
            control.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(control);
        }

        _nodes.Add((dot, time));
        return button;
    }

    private void Refresh()
    {
        int n = 0;
        for (int s = 0; s < _stages.Count; s++)
        {
            var (button, dot, count) = _stages[s];
            var stage = _list[s];
            button.SetPressedNoSignal(s == _selected.Value);
            var (status, valid, _) = stage.Summarize(_graph);
            dot.Status = status;
            count.Text = stage.Nodes.Count == 0 ? stage.Caption?.Invoke() ?? "" : $"{valid}/{stage.Nodes.Count}";
            foreach (int node in stage.Nodes)
            {
                var (nodeDot, time) = _nodes[n++];
                nodeDot.Status = _graph.Status(node);
                time.Text = _graph.Status(node) switch
                {
                    NodeStatus.Valid => _graph.Seconds(node) < 0.005 ? "<0.01s" : $"{_graph.Seconds(node):0.00}s",
                    var other => UiStyle.StatusText(other),
                };
            }
        }
    }
}
