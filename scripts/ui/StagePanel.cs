using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Ui;

/// <summary>
/// Right-hand panel, the editor of the town graph. One section per graph node, under its group heading: status,
/// the keys it reads and writes, summary, notices and controls generated from the options' [Param] properties.
/// Editing a value invalidates that node and everything downstream of it.
/// </summary>
public partial class StagePanel : PanelContainer
{
    private readonly List<(Label Header, Label Summary, Label Notices)> _sections = new();
    private CityGraph _graph = null!;
    private CheckBox _auto = null!;
    private double _runAt = -1;

    public void Bind(CityGraph graph)
    {
        _graph = graph;
        _graph.Changed += Refresh;
        AddThemeStyleboxOverride("panel", UiStyle.Box(UiStyle.Panel));

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);

        var actions = new HBoxContainer();
        var run = UiStyle.Button("生成");
        run.Pressed += () => _ = _graph.Run();
        var cancel = UiStyle.Button("取消");
        cancel.Pressed += _graph.Cancel;
        _auto = new CheckBox { Text = "改参数自动生成", ButtonPressed = true, FocusMode = FocusModeEnum.None };
        actions.AddChild(run);
        actions.AddChild(cancel);
        actions.AddChild(_auto);
        list.AddChild(actions);

        string? group = null;
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            if (graph.Nodes[i].Group != group)
            {
                group = graph.Nodes[i].Group;
                list.AddChild(new HSeparator());
                list.AddChild(UiStyle.Text(group, 16, UiStyle.Ink));
            }

            list.AddChild(Section(i));
        }

        Refresh();
    }

    public override void _Process(double delta)
    {
        if (_runAt < 0 || Time.GetTicksMsec() / 1000.0 < _runAt)
            return;
        _runAt = -1;
        _ = _graph.Run();
    }

    private Control Section(int index)
    {
        var node = _graph.Nodes[index];
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        var header = UiStyle.Text("", 14, UiStyle.Accent);
        var summary = UiStyle.Text("", 12, UiStyle.Ink);
        var notices = UiStyle.Text("", 12, new Color("e3b25f"));
        box.AddChild(header);
        box.AddChild(UiStyle.Text(Ports(node), 11, UiStyle.Muted));
        box.AddChild(summary);
        box.AddChild(notices);
        _sections.Add((header, summary, notices));

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        foreach (var property in node.Options.GetType().GetProperties())
        {
            var param = property.GetCustomAttribute<ParamAttribute>();
            if (param == null)
                continue;
            grid.AddChild(UiStyle.Text(param.Label, 12, UiStyle.Muted, wrap: false));
            grid.AddChild(Control(node.Options, property, param, index));
        }

        if (grid.GetChildCount() > 0)
            box.AddChild(grid);
        return box;
    }

    private static string Ports(CityNode node)
    {
        static string Names(IEnumerable<IKey> keys) => string.Join("、", keys.Select(k => k.Label));
        var input = node.Reads.Concat(node.Edits).ToList();
        var output = node.Writes.Concat(node.Edits).ToList();
        return $"{(input.Count == 0 ? "地形" : Names(input))} → {Names(output)}";
    }

    private Control Control(object options, PropertyInfo property, ParamAttribute param, int index)
    {
        if (property.PropertyType == typeof(bool))
        {
            var check = new CheckBox { ButtonPressed = (bool)property.GetValue(options)!, FocusMode = FocusModeEnum.None };
            check.Toggled += on => Changed(options, property, on, index);
            return check;
        }

        bool integer = property.PropertyType == typeof(int);
        var spin = new SpinBox
        {
            MinValue = param.Min,
            MaxValue = param.Max,
            Step = integer ? Math.Max(1, param.Step) : param.Step,
            Value = Convert.ToDouble(property.GetValue(options)),
            CustomMinimumSize = new Vector2(110, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        spin.ValueChanged += value => Changed(options, property, integer ? (int)value : (float)value, index);
        return spin;
    }

    private void Changed(object options, PropertyInfo property, object value, int index)
    {
        property.SetValue(options, value);
        _graph.Invalidate(index);
        if (_auto.ButtonPressed)
            _runAt = Time.GetTicksMsec() / 1000.0 + 0.35;
    }

    private void Refresh()
    {
        for (int i = 0; i < _sections.Count; i++)
        {
            var (header, summary, notices) = _sections[i];
            var status = _graph.Status(i);
            string state = status switch
            {
                NodeStatus.Valid => $"有效 · {_graph.Seconds(i):0.00} s",
                NodeStatus.Running => "运行中…",
                NodeStatus.Stale => "已过期",
                NodeStatus.Failed => "失败",
                NodeStatus.Blocked => "等待上游",
                _ => "未运行",
            };
            header.Text = $"{_graph.Nodes[i].Title}    {state}";
            summary.Text = status == NodeStatus.Failed ? _graph.Error(i) ?? "" : _graph.Summary(i);
            summary.Visible = summary.Text.Length > 0;
            notices.Text = string.Join("\n", _graph.Notices(i));
            notices.Visible = notices.Text.Length > 0;
        }
    }
}
