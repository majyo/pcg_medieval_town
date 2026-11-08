using System;
using System.Collections.Generic;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Rendering;

/// <summary>
/// How one site field is shown: the value range its color ramp spans, the ramp, and how a value reads in the probe.
/// The ground view menu, the legend and the probe are all built from <see cref="FieldStyles.All"/>.
/// </summary>
public sealed class FieldStyle
{
    public FieldStyle(Key<ScalarField> key, Func<ScalarField, (float Low, float High)> range, Func<float, Color> ramp)
    {
        Key = key;
        Range = range;
        Ramp = ramp;
    }

    public Key<ScalarField> Key { get; }

    /// <summary>The values the ramp's ends stand for; values outside are clamped.</summary>
    public Func<ScalarField, (float Low, float High)> Range { get; }

    /// <summary>Color at a position along the ramp, 0 to 1.</summary>
    public Func<float, Color> Ramp { get; }

    public Color NoData { get; init; } = new("d9d6cb");

    /// <summary>How a value reads in the probe; defaults to the field's own formatting.</summary>
    public Func<ScalarField, float, string>? Format { get; init; }

    public string Name => Key.Label;

    public Color ColorOf(ScalarField field, float value)
    {
        if (field.IsNoData(value))
            return NoData;
        var (low, high) = Range(field);
        return Ramp(high > low ? Mathf.Clamp((value - low) / (high - low), 0, 1) : 0);
    }

    public string Describe(ScalarField field, float value) =>
        Format != null && !field.IsNoData(value) ? Format(field, value) : field.Format(value);
}

/// <summary>The registered field styles, in menu order. Showing a new field takes one entry here.</summary>
public static class FieldStyles
{
    private static readonly string[] Compass = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };

    public static readonly IReadOnlyList<FieldStyle> All = new[]
    {
        new FieldStyle(Keys.Height, f => (f.Min, f.Max), Stops((0, "315e70"), (0.5f, "92b78e"), (1, "e6d3a0"))),
        new FieldStyle(Keys.Slope, _ => (0, 20), Stops((0, "7db39e"), (0.4f, "e8c36f"), (1, "be6251"))),
        new FieldStyle(Keys.Aspect, _ => (0, 360),
            Stops((0, "5b7fb5"), (0.25f, "7fb069"), (0.5f, "e3b85a"), (0.75f, "c9705a"), (1, "5b7fb5")))
        {
            Format = (_, v) => $"{Compass[(int)MathF.Round(v / 45) % 8]} {v:0}°",
        },
    };

    /// <summary>A piecewise-linear ramp through (position, hex color) stops given in increasing position.</summary>
    public static Func<float, Color> Stops(params (float At, string Hex)[] stops)
    {
        var colors = Array.ConvertAll(stops, s => new Color(s.Hex));
        return t =>
        {
            for (int i = 1; i < stops.Length; i++)
            {
                if (t <= stops[i].At)
                    return colors[i - 1].Lerp(colors[i], Mathf.InverseLerp(stops[i - 1].At, stops[i].At, t));
            }

            return colors[^1];
        };
    }
}
