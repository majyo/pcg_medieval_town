using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.Ui;

/// <summary>One legend block: a color ramp between two end labels, or a list of colored categories.</summary>
public sealed record LegendEntry(string Caption, Func<float, Color>? Ramp, string Low = "", string High = "",
    IReadOnlyList<(Color Color, string Name)>? Categories = null)
{
    public string Signature => $"{Caption}|{Low}|{High}|{string.Join(",", Categories?.Select(c => c.Name) ?? Array.Empty<string>())}";
}

/// <summary>A floating card explaining the colors currently on screen; hidden when there is nothing to explain.</summary>
public partial class Legend : PanelContainer
{
    private readonly VBoxContainer _box = new();
    private string _signature = "";

    public Legend()
    {
        Name = "Legend";
        CustomMinimumSize = new Vector2(220, 0);
        AddThemeStyleboxOverride("panel", UiStyle.Box(UiStyle.Panel, 8, 10, 8));
        _box.AddThemeConstantOverride("separation", 4);
        AddChild(_box);
        Visible = false;
    }

    public void Show(IReadOnlyList<LegendEntry> entries)
    {
        Visible = entries.Count > 0;
        string signature = string.Join("\n", entries.Select(e => e.Signature));
        if (signature == _signature)
            return;
        _signature = signature;
        foreach (var child in _box.GetChildren())
            child.QueueFree();
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0)
                _box.AddChild(new HSeparator());
            Build(entries[i]);
        }
    }

    private void Build(LegendEntry entry)
    {
        _box.AddChild(UiStyle.Text(entry.Caption, 12, UiStyle.Ink));
        if (entry.Ramp != null)
        {
            var gradient = new Gradient();
            const int stops = 33;
            var offsets = new float[stops];
            var colors = new Color[stops];
            for (int i = 0; i < stops; i++)
            {
                offsets[i] = i / (float)(stops - 1);
                colors[i] = entry.Ramp(offsets[i]);
            }

            gradient.Offsets = offsets;
            gradient.Colors = colors;
            _box.AddChild(new TextureRect
            {
                Texture = new GradientTexture1D { Gradient = gradient, Width = 128 },
                CustomMinimumSize = new Vector2(0, 10),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
            });
            var ends = new HBoxContainer();
            var low = UiStyle.Text(entry.Low, 11, UiStyle.Muted, wrap: false);
            var high = UiStyle.Text(entry.High, 11, UiStyle.Muted, wrap: false);
            high.HorizontalAlignment = HorizontalAlignment.Right;
            ends.AddChild(low);
            ends.AddChild(high);
            _box.AddChild(ends);
        }

        if (entry.Categories != null)
        {
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 12);
            foreach (var (color, name) in entry.Categories)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                row.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = SizeFlags.ShrinkCenter });
                row.AddChild(UiStyle.Text(name, 11, UiStyle.Muted, wrap: false));
                grid.AddChild(row);
            }

            _box.AddChild(grid);
        }
    }
}
