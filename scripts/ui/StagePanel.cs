using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Ui;

/// <summary>
/// Right-hand panel. One section per stage with status, summary, notices and controls generated from the
/// options' [Param] properties. Editing a value invalidates that stage and everything after it.
/// </summary>
public partial class StagePanel : PanelContainer
{
    private readonly List<(Label Header, Label Summary, Label Notices)> _sections = new();
    private Pipeline _pipeline = null!;
    private CheckBox _auto = null!;
    private double _runAt = -1;

    public void Bind(Pipeline pipeline)
    {
        _pipeline = pipeline;
        _pipeline.Changed += Refresh;
        AddThemeStyleboxOverride("panel", UiStyle.Box(UiStyle.Panel));

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);

        var actions = new HBoxContainer();
        var run = UiStyle.Button("生成");
        run.Pressed += () => _ = _pipeline.Run();
        var cancel = UiStyle.Button("取消");
        cancel.Pressed += _pipeline.Cancel;
        _auto = new CheckBox { Text = "改参数自动生成", ButtonPressed = true, FocusMode = FocusModeEnum.None };
        actions.AddChild(run);
        actions.AddChild(cancel);
        actions.AddChild(_auto);
        list.AddChild(actions);

        for (int i = 0; i < pipeline.Stages.Count; i++)
            list.AddChild(Section(i));
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (_runAt < 0 || Time.GetTicksMsec() / 1000.0 < _runAt)
            return;
        _runAt = -1;
        _ = _pipeline.Run();
    }

    private Control Section(int index)
    {
        var stage = _pipeline.Stages[index];
        var box = new VBoxContainer();
        box.AddChild(new HSeparator());
        var header = UiStyle.Text("", 15, UiStyle.Accent);
        var summary = UiStyle.Text("", 12, UiStyle.Ink);
        var notices = UiStyle.Text("", 12, new Color("e3b25f"));
        box.AddChild(header);
        box.AddChild(summary);
        box.AddChild(notices);
        _sections.Add((header, summary, notices));

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        foreach (var property in stage.Options.GetType().GetProperties())
        {
            var param = property.GetCustomAttribute<ParamAttribute>();
            if (param == null)
                continue;
            grid.AddChild(UiStyle.Text(param.Label, 12, UiStyle.Muted, wrap: false));
            grid.AddChild(Control(stage.Options, property, param, index));
        }

        box.AddChild(grid);
        return box;
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
        _pipeline.Invalidate(index);
        if (_auto.ButtonPressed)
            _runAt = Time.GetTicksMsec() / 1000.0 + 0.35;
    }

    private void Refresh()
    {
        for (int i = 0; i < _sections.Count; i++)
        {
            var (header, summary, notices) = _sections[i];
            var status = _pipeline.Status(i);
            string state = status switch
            {
                StageStatus.Valid => $"有效 · {_pipeline.Seconds(i):0.00} s",
                StageStatus.Running => "运行中…",
                StageStatus.Stale => "已过期",
                StageStatus.Failed => "失败",
                _ => "未运行",
            };
            header.Text = $"{_pipeline.Stages[i].Title}    {state}";
            var result = _pipeline.Result(i) as IStageResult;
            summary.Text = status == StageStatus.Failed ? _pipeline.Error(i) : result?.Summary ?? "";
            notices.Text = string.Join("\n", result?.Notices ?? Array.Empty<string>());
            notices.Visible = notices.Text.Length > 0;
        }
    }
}
