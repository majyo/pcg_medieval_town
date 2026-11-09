using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PcgCitySandbox.City;

namespace PcgCitySandbox.Rendering;

/// <summary>What a legend shows for a painted field: a ramp with its two ends, or a list of colored categories.</summary>
public sealed record FieldLegend(string Caption, Func<float, Color>? Ramp, string Low, string High,
    IReadOnlyList<(Color Color, string Name)>? Categories);

/// <summary>
/// How one field on the site grid is painted on the ground, explained in the legend and read in the probe. The ground
/// view menu, the legend and the probe are all built from <see cref="FieldStyles.All"/>, whatever the field's type.
/// </summary>
public abstract class GroundStyle
{
    /// <summary>Color of cells without a value (e.g. the aspect of flat ground).</summary>
    protected static readonly Color NoData = new("d9d6cb");

    public abstract IKey Key { get; }

    /// <summary>The stage the field belongs to, as a menu prefix: "采样", "评价".</summary>
    public abstract string Group { get; }

    public string Name => Key.Label;

    /// <summary>The field's current value in <paramref name="graph"/>, or null while it has none.</summary>
    public abstract object? Value(IKeyReader graph);

    /// <summary>Ground color at any point of the map.</summary>
    public abstract Func<Vector2, Color> Painter(object value);

    public abstract FieldLegend Legend(object value);

    /// <summary>The field's name and value at <paramref name="p"/>, for the probe.</summary>
    public abstract string Describe(object value, Vector2 p);
}

/// <summary>A scalar field: the value range its color ramp spans, the ramp, and how a value reads.</summary>
public sealed class FieldStyle : GroundStyle
{
    private readonly Key<ScalarField> _key;

    public FieldStyle(string group, Key<ScalarField> key, Func<ScalarField, (float Low, float High)> range, Func<float, Color> ramp)
    {
        Group = group;
        _key = key;
        Range = range;
        Ramp = ramp;
    }

    public override IKey Key => _key;
    public override string Group { get; }

    /// <summary>The values the ramp's ends stand for; values outside are clamped.</summary>
    public Func<ScalarField, (float Low, float High)> Range { get; }

    /// <summary>Color at a position along the ramp, 0 to 1.</summary>
    public Func<float, Color> Ramp { get; }

    /// <summary>How a value reads in the probe; defaults to the field's own formatting.</summary>
    public Func<ScalarField, float, string>? Format { get; init; }

    public override object? Value(IKeyReader graph) => graph.Get(_key);

    public override Func<Vector2, Color> Painter(object value)
    {
        var field = (ScalarField)value;
        return p => ColorOf(field, field.Sample(p));
    }

    public override FieldLegend Legend(object value)
    {
        var field = (ScalarField)value;
        var (low, high) = Range(field);
        return new FieldLegend(field.Label + (field.Unit == "" ? "" : $"（{field.Unit}）"), Ramp, Read(field, low), Read(field, high),
            field.NoData.HasValue ? new[] { (NoData, NoDataName) } : null);
    }

    public override string Describe(object value, Vector2 p)
    {
        var field = (ScalarField)value;
        return $"{field.Label} {Read(field, field.Sample(p))}";
    }

    /// <summary>What a cell without a value means for this field, in the legend.</summary>
    public string NoDataName { get; init; } = "无值";

    private Color ColorOf(ScalarField field, float value)
    {
        if (field.IsNoData(value))
            return NoData;
        var (low, high) = Range(field);
        return Ramp(high > low ? Mathf.Clamp((value - low) / (high - low), 0, 1) : 0);
    }

    private string Read(ScalarField field, float value) =>
        Format != null && !field.IsNoData(value) ? Format(field, value) : field.Format(value);
}

/// <summary>A category field: one color per category, cell by cell (no blending across category borders).</summary>
public sealed class CategoryStyle : GroundStyle
{
    private readonly Key<CategoryField> _key;

    public CategoryStyle(string group, Key<CategoryField> key, IReadOnlyList<Color> colors)
    {
        Group = group;
        _key = key;
        Colors = colors;
    }

    public override IKey Key => _key;
    public override string Group { get; }

    /// <summary>Color of each category, by its index.</summary>
    public IReadOnlyList<Color> Colors { get; }

    public override object? Value(IKeyReader graph) => graph.Get(_key);

    public override Func<Vector2, Color> Painter(object value)
    {
        var field = (CategoryField)value;
        return p => Colors[field.Nearest(p)];
    }

    public override FieldLegend Legend(object value)
    {
        var field = (CategoryField)value;
        return new FieldLegend(field.Label, null, "", "",
            Enumerable.Range(0, field.Names.Count).Select(c => (Colors[c], $"{field.Names[c]} {field.Fraction(c):P0}")).ToArray());
    }

    public override string Describe(object value, Vector2 p)
    {
        var field = (CategoryField)value;
        return field.Names[field.Nearest(p)];
    }
}

/// <summary>The registered ground styles, in menu order. Showing a new field takes one entry here.</summary>
public static class FieldStyles
{
    private static readonly string[] Compass = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };

    /// <summary>Colors of the buildability reasons, indexed by <see cref="SiteBlock"/>; also the site overlay's.</summary>
    public static readonly IReadOnlyList<Color> Buildability = new Color[]
    {
        new("d8e3ae"), new("2f6f9a"), new("7fb6c9"), new("a9cfd8"), new("c8553d"), new("2f6b3a"),
    };

    public static readonly IReadOnlyList<GroundStyle> All = new GroundStyle[]
    {
        new FieldStyle("采样", Keys.Height, f => (f.Min, f.Max), Stops((0, "315e70"), (0.5f, "92b78e"), (1, "e6d3a0"))),
        new FieldStyle("采样", Keys.Slope, _ => (0, 20), Stops((0, "7db39e"), (0.4f, "e8c36f"), (1, "be6251"))),
        new FieldStyle("采样", Keys.Aspect, _ => (0, 360),
            Stops((0, "5b7fb5"), (0.25f, "7fb069"), (0.5f, "e3b85a"), (0.75f, "c9705a"), (1, "5b7fb5")))
        {
            Format = (_, v) => $"{Compass[(int)MathF.Round(v / 45) % 8]} {v:0}°",
            NoDataName = "平地",
        },
        // Water blues below zero, then from the near bank (green) out to dry, distant land (tan).
        new FieldStyle("采样", Keys.Shore, _ => (-25, 250),
            Stops((0, "2f5f86"), (0.09f, "9cc3d4"), (0.0915f, "cfe3b0"), (0.4f, "e3cf8f"), (1, "b5774f")))
        {
            Format = (_, v) => v < 0 ? $"水中 {-v:0} m" : $"{v:0} m",
        },
        // Hard breaks at the water level, 2 m (low floodplain) and 6 m (terraces above it).
        new FieldStyle("采样", Keys.AboveWater, _ => (-3, 12),
            Stops((0, "2f5f86"), (0.2f, "7fa9c4"), (0.2005f, "bfe0d6"), (0.333f, "9fcdbd"), (0.3335f, "d6e391"),
                (0.6f, "b4cf6c"), (0.6005f, "e6d3a0"), (1, "c4986a")))
        {
            Format = (_, v) => $"{v:+0.0;-0.0} m",
        },
        // Hollows blue, rises brown, level ground pale.
        new FieldStyle("采样", Keys.Relief, _ => (-3, 3), Stops((0, "4f7fa6"), (0.5f, "eeeadb"), (1, "a5653f")))
        {
            Format = (_, v) => $"{v:+0.0;-0.0} m",
        },
        new FieldStyle("采样", Keys.Forest, _ => (0, 1), Stops((0, "efe8c8"), (0.3f, "b9cf8a"), (1, "2f5e34")))
        {
            Format = (_, v) => $"{v:P0}",
        },
        new CategoryStyle("评价", Keys.Buildable, Buildability),
        // Unbuildable land a muted gray-brown, then from poor (ochre) to good (deep green) places to live.
        new FieldStyle("评价", Keys.Habitability, _ => (0, 1),
            Stops((0, "b9ad9b"), (0.0001f, "e2c27a"), (0.5f, "c5d38a"), (0.8f, "7fb069"), (1, "2f7a4a")))
        {
            Format = (_, v) => v <= 0 ? "0（不可建设）" : $"{v:0.00}",
        },
        // Level open ground pale, costlier ground darker and redder.
        new FieldStyle("评价", Keys.TravelCost, _ => (1, 3), Stops((0, "eee6c8"), (0.4f, "d9a65f"), (1, "8c3b2e")))
        {
            Format = (_, v) => $"{v:0.00}×",
            NoDataName = "不可通行",
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
