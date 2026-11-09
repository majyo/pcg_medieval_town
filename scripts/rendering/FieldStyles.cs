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

    /// <summary>Color of cells without a value (e.g. the aspect of flat ground).</summary>
    private static readonly Color NoData = new("d9d6cb");

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
        // Water blues below zero, then from the near bank (green) out to dry, distant land (tan).
        new FieldStyle(Keys.Shore, _ => (-25, 250),
            Stops((0, "2f5f86"), (0.09f, "9cc3d4"), (0.0915f, "cfe3b0"), (0.4f, "e3cf8f"), (1, "b5774f")))
        {
            Format = (_, v) => v < 0 ? $"水中 {-v:0} m" : $"{v:0} m",
        },
        // Hard breaks at the water level, 2 m (low floodplain) and 6 m (terraces above it).
        new FieldStyle(Keys.AboveWater, _ => (-3, 12),
            Stops((0, "2f5f86"), (0.2f, "7fa9c4"), (0.2005f, "bfe0d6"), (0.333f, "9fcdbd"), (0.3335f, "d6e391"),
                (0.6f, "b4cf6c"), (0.6005f, "e6d3a0"), (1, "c4986a")))
        {
            Format = (_, v) => $"{v:+0.0;-0.0} m",
        },
        // Hollows blue, rises brown, level ground pale.
        new FieldStyle(Keys.Relief, _ => (-3, 3), Stops((0, "4f7fa6"), (0.5f, "eeeadb"), (1, "a5653f")))
        {
            Format = (_, v) => $"{v:+0.0;-0.0} m",
        },
        new FieldStyle(Keys.Forest, _ => (0, 1), Stops((0, "efe8c8"), (0.3f, "b9cf8a"), (1, "2f5e34")))
        {
            Format = (_, v) => $"{v:P0}",
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
