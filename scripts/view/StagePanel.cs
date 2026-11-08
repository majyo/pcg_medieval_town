using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using PcgCitySandbox.Core;
using PcgCitySandbox.Stages;

namespace PcgCitySandbox.View;

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
        AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.075f, 0.12f, 0.10f, 0.94f)));

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);

        var actions = new HBoxContainer();
        var run = Ui.Button("生成");
        run.Pressed += () => _ = _pipeline.Run();
        var cancel = Ui.Button("取消");
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
        var header = Ui.Text("", 15, Ui.Accent);
        var summary = Ui.Text("", 12, Ui.Ink);
        var notices = Ui.Text("", 12, new Color("e3b25f"));
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
            grid.AddChild(Ui.Text(param.Label, 12, Ui.Muted, wrap: false));
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

/// <summary>Shared look for the few hand-built controls.</summary>
public static class Ui
{
    public static readonly Color Ink = new("e6ede0"), Muted = new("a8b6a8"), Accent = new("cbdca0");

    public static Label Text(string text, int size, Color color, bool wrap = true)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    public static Button Button(string text) =>
        new() { Text = text, CustomMinimumSize = new Vector2(0, 32), FocusMode = Control.FocusModeEnum.None };

    public static StyleBoxFlat Box(Color color) => new()
    {
        BgColor = color,
        CornerRadiusTopLeft = 10,
        CornerRadiusTopRight = 10,
        CornerRadiusBottomLeft = 10,
        CornerRadiusBottomRight = 10,
        ContentMarginLeft = 14,
        ContentMarginRight = 14,
        ContentMarginTop = 12,
        ContentMarginBottom = 12,
    };
}
