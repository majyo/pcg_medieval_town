using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Ui;

/// <summary>
/// A column of labeled rows (slider with a typed value, switch, drop-down, color, number, buttons) under foldable
/// section headers. Rows read their value through a getter, so <see cref="Refresh"/> re-reads everything after the
/// values changed elsewhere (a preset, a reset). A row whose value differs from its default shows a reset button.
/// </summary>
public partial class Inspector : VBoxContainer
{
    private const float LabelWidth = 112;
    private readonly List<Action> _refreshers = new();
    private readonly List<Inspector> _sections = new();

    public Inspector()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 3);
    }

    /// <summary>Re-reads every row's value, in this inspector and its sections.</summary>
    public void Refresh()
    {
        foreach (var refresh in _refreshers)
            refresh();
        foreach (var section in _sections)
            section.Refresh();
    }

    /// <summary>A foldable group of rows under a header.</summary>
    public Inspector Section(string title, bool open = true, string? tooltip = null)
    {
        var header = new Button
        {
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            FocusMode = FocusModeEnum.None,
            TooltipText = tooltip ?? "",
        };
        header.AddThemeFontSizeOverride("font_size", 13);
        header.AddThemeColorOverride("font_color", UiStyle.Accent);
        header.AddThemeColorOverride("font_hover_color", Colors.White);
        header.AddThemeStyleboxOverride("normal", UiStyle.Box(new Color(1, 1, 1, 0.03f), 4, 6, 3));
        header.AddThemeStyleboxOverride("hover", UiStyle.Box(new Color(1, 1, 1, 0.07f), 4, 6, 3));
        header.AddThemeStyleboxOverride("pressed", UiStyle.Box(new Color(1, 1, 1, 0.07f), 4, 6, 3));
        var body = new Inspector { Visible = open };
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        margin.AddChild(body);
        void Label() => header.Text = (body.Visible ? "▾  " : "▸  ") + title;
        header.Pressed += () =>
        {
            body.Visible = !body.Visible;
            Label();
        };
        Label();
        AddChild(header);
        AddChild(margin);
        _sections.Add(body);
        return body;
    }

    public Label Note(string text, Color? color = null, int size = 12)
    {
        var label = UiStyle.Text(text, size, color ?? UiStyle.Muted);
        AddChild(label);
        return label;
    }

    /// <summary>A slider with an editable value; values typed outside the slider range are clamped to it.</summary>
    public void Slider(string label, double min, double max, double step, Func<double> get, Action<double> set,
        double? reset = null, string? tooltip = null)
    {
        var row = Row(label, tooltip, out var resetButton);
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Scrollable = false,
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        var field = new LineEdit
        {
            CustomMinimumSize = new Vector2(62, 0),
            Alignment = HorizontalAlignment.Right,
            SelectAllOnFocus = true,
        };
        string format = "F" + Decimals(step);
        bool updating = false;
        void Show(double value)
        {
            updating = true;
            slider.Value = value;
            field.Text = value.ToString(format, CultureInfo.InvariantCulture);
            resetButton.Visible = reset.HasValue && Math.Abs(value - reset.Value) > step * 0.5;
            updating = false;
        }

        void Commit(double value)
        {
            value = Math.Clamp(Math.Round(value / step) * step, min, max);
            set(value);
            Show(value);
        }

        slider.ValueChanged += value =>
        {
            if (!updating)
                Commit(value);
        };
        field.TextSubmitted += text =>
        {
            Parse(text);
            field.ReleaseFocus();
        };
        field.FocusExited += () => Parse(field.Text);
        void Parse(string text)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                Commit(value);
            else
                Show(get());
        }

        if (reset.HasValue)
            resetButton.Pressed += () => Commit(reset.Value);
        row.AddChild(slider);
        row.AddChild(field);
        _refreshers.Add(() => Show(get()));
        Show(get());
    }

    /// <summary>A whole number typed into a field, optionally with a button that picks a random one.</summary>
    public void Number(string label, int min, int max, Func<int> get, Action<int> set, int? reset = null, bool dice = false,
        string? tooltip = null)
    {
        var row = Row(label, tooltip, out var resetButton);
        var field = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true };
        void Show(int value)
        {
            field.Text = value.ToString(CultureInfo.InvariantCulture);
            resetButton.Visible = reset.HasValue && value != reset.Value;
        }

        void Commit(int value)
        {
            value = Math.Clamp(value, min, max);
            set(value);
            Show(value);
        }

        field.TextSubmitted += text =>
        {
            Parse(text);
            field.ReleaseFocus();
        };
        field.FocusExited += () => Parse(field.Text);
        void Parse(string text)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                Commit(value);
            else
                Show(get());
        }

        row.AddChild(field);
        if (dice)
        {
            var roll = UiStyle.Mini("随机", "随机取一个值");
            roll.Pressed += () => Commit((int)GD.RandRange(min, max));
            row.AddChild(roll);
        }

        if (reset.HasValue)
            resetButton.Pressed += () => Commit(reset.Value);
        _refreshers.Add(() => Show(get()));
        Show(get());
    }

    public void Toggle(string label, Func<bool> get, Action<bool> set, bool? reset = null, string? tooltip = null)
    {
        var row = Row(label, tooltip, out var resetButton);
        var check = new CheckButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        check.Alignment = HorizontalAlignment.Right;
        void Show(bool value)
        {
            check.SetPressedNoSignal(value);
            resetButton.Visible = reset.HasValue && value != reset.Value;
        }

        check.Toggled += on =>
        {
            set(on);
            Show(on);
        };
        if (reset.HasValue)
            resetButton.Pressed += () =>
            {
                set(reset.Value);
                Show(reset.Value);
            };
        row.AddChild(check);
        _refreshers.Add(() => Show(get()));
        Show(get());
    }

    /// <summary>A switch bound to a shared setting.</summary>
    public void Toggle(string label, Setting<bool> setting, string? tooltip = null)
    {
        Toggle(label, () => setting.Value, on => setting.Value = on, tooltip: tooltip);
        var refresh = _refreshers[^1];
        setting.Changed += _ => refresh();
    }

    public void Choice(string label, IReadOnlyList<string> items, Func<int> get, Action<int> set, string? tooltip = null)
    {
        var row = Row(label, tooltip, out _);
        var choice = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
        foreach (var item in items)
            choice.AddItem(item);
        choice.ItemSelected += i => set((int)i);
        row.AddChild(choice);
        _refreshers.Add(() => choice.Selected = get());
        choice.Selected = get();
    }

    public void Choice(string label, IReadOnlyList<string> items, Setting<int> setting, string? tooltip = null)
    {
        Choice(label, items, () => setting.Value, i => setting.Value = i, tooltip);
        var refresh = _refreshers[^1];
        setting.Changed += _ => refresh();
    }

    public void ColorRow(string label, Func<Color> get, Action<Color> set, Color? reset = null, string? tooltip = null)
    {
        var row = Row(label, tooltip, out var resetButton);
        var picker = new ColorPickerButton
        {
            EditAlpha = false,
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 24),
        };
        void Show(Color value)
        {
            picker.Color = value;
            resetButton.Visible = reset.HasValue && !value.IsEqualApprox(reset.Value);
        }

        picker.ColorChanged += color =>
        {
            set(color);
            Show(color);
        };
        if (reset.HasValue)
            resetButton.Pressed += () =>
            {
                set(reset.Value);
                Show(reset.Value);
            };
        row.AddChild(picker);
        _refreshers.Add(() => Show(get()));
        Show(get());
    }

    /// <summary>A row of equally wide buttons.</summary>
    public HBoxContainer Buttons(params (string Text, string Tooltip, Action Pressed)[] buttons)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        foreach (var (text, tooltip, pressed) in buttons)
        {
            var button = UiStyle.Button(text, tooltip);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Pressed += pressed;
            row.AddChild(button);
        }

        AddChild(row);
        return row;
    }

    /// <summary>
    /// Rows for every [Param] property of <paramref name="target"/>: bool → switch, Color → color, large int ranges →
    /// number field (with a random button for seeds), other numbers → slider. Defaults are the values at build time.
    /// </summary>
    public void Params(object target, Action<PropertyInfo> changed)
    {
        foreach (var property in target.GetType().GetProperties())
        {
            var param = property.GetCustomAttribute<ParamAttribute>();
            if (param == null)
                continue;
            object initial = property.GetValue(target)!;
            void Set(object value)
            {
                if (Equals(property.GetValue(target), value))
                    return;
                property.SetValue(target, value);
                changed(property);
            }

            var type = property.PropertyType;
            if (type == typeof(bool))
                Toggle(param.Label, () => (bool)property.GetValue(target)!, on => Set(on), (bool)initial);
            else if (type == typeof(Color))
                ColorRow(param.Label, () => (Color)property.GetValue(target)!, c => Set(c), (Color)initial);
            else if (type == typeof(int) && param.Max - param.Min > 200)
                Number(param.Label, (int)param.Min, (int)param.Max, () => (int)property.GetValue(target)!, v => Set(v), (int)initial,
                    dice: property.Name.Contains("Seed"));
            else if (type == typeof(int))
                Slider(param.Label, param.Min, param.Max, Math.Max(1, param.Step), () => (int)property.GetValue(target)!,
                    v => Set((int)Math.Round(v)), (int)initial);
            else
                Slider(param.Label, param.Min, param.Max, param.Step, () => Convert.ToDouble(property.GetValue(target)),
                    v => Set(Convert.ChangeType(v, type, CultureInfo.InvariantCulture)), Convert.ToDouble(initial));
        }
    }

    /// <summary>
    /// A row: label, then the returned box for the value controls, then a reset button that keeps its space while
    /// hidden so rows do not shift when it appears.
    /// </summary>
    private HBoxContainer Row(string label, string? tooltip, out Button reset)
    {
        var row = new HBoxContainer { TooltipText = tooltip ?? "" };
        row.AddThemeConstantOverride("separation", 6);
        var name = UiStyle.Text(label, 12, UiStyle.Muted, wrap: false);
        name.CustomMinimumSize = new Vector2(LabelWidth, 0);
        name.SizeFlagsHorizontal = SizeFlags.Fill;
        name.ClipText = true;
        name.TooltipText = tooltip ?? label;
        name.MouseFilter = MouseFilterEnum.Pass;
        row.AddChild(name);
        var content = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 6);
        row.AddChild(content);
        reset = UiStyle.Mini("↺", "恢复默认");
        reset.Visible = false;
        var slot = new Control { CustomMinimumSize = new Vector2(22, 0) };
        slot.AddChild(reset);
        reset.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        row.AddChild(slot);
        AddChild(row);
        return content;
    }

    private static int Decimals(double step)
    {
        int decimals = 0;
        while (decimals < 6 && Math.Abs(step * Math.Pow(10, decimals) - Math.Round(step * Math.Pow(10, decimals))) > 1e-6)
            decimals++;
        return decimals;
    }
}
