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

    /// <summary>
    /// Ground color for a node that edits the value: by default the same as <see cref="Painter(object)"/>; a style
    /// may gray out what <paramref name="previous"/> (the version the node started from) already held.
    /// </summary>
    public virtual Func<Vector2, Color> Painter(object value, object? previous) => Painter(value);

    public abstract FieldLegend Legend(object value);

    /// <summary>The legend for a node that edits the value; by default the same as <see cref="Legend(object)"/>.</summary>
    public virtual FieldLegend Legend(object value, object? previous) => Legend(value);

    /// <summary>The field's name and value at <paramref name="p"/>, for the probe.</summary>
    public abstract string Describe(object value, Vector2 p);

    /// <summary>Whether the probe lists this field (measurements and evaluations do; intermediate grids do not).</summary>
    public virtual bool Probe => true;
}

/// <summary>A per-cell value that is not a field but reads best painted on the ground (the grid, zonings).</summary>
public sealed class CellStyle<T> : GroundStyle where T : class
{
    private readonly Key<T> _key;
    private readonly Func<T, Vector2, Color> _color;
    private readonly Func<T, FieldLegend> _legend;
    private readonly Func<T, Vector2, string> _describe;

    public CellStyle(string group, Key<T> key, Func<T, Vector2, Color> color, Func<T, FieldLegend> legend,
        Func<T, Vector2, string> describe)
    {
        Group = group;
        _key = key;
        _color = color;
        _legend = legend;
        _describe = describe;
    }

    public override IKey Key => _key;
    public override string Group { get; }

    /// <summary>Whether the probe lists this value (off for intermediate grids).</summary>
    public bool Listed { get; init; }

    public override bool Probe => Listed;
    public override object? Value(IKeyReader graph) => graph.Get(_key);

    public override Func<Vector2, Color> Painter(object value)
    {
        var typed = (T)value;
        return p => _color(typed, p);
    }

    public override FieldLegend Legend(object value) => _legend((T)value);
    public override string Describe(object value, Vector2 p) => _describe((T)value, p);
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
        return new FieldLegend(field.Label, null, "", "", Enumerable.Range(0, field.Names.Count).Where(c => field.Counts[c] > 0)
            .Select(c => (Colors[c], $"{field.Names[c]} {Share(field.Fraction(c))}")).ToArray());
    }

    /// <summary>A share as a percentage, with one decimal below 10% so small categories do not read as 0%.</summary>
    private static string Share(float fraction) => fraction >= 0.1f ? $"{fraction:P0}" : $"{fraction:P1}";

    /// <summary>Cells the editing node changed in their colors, the rest grayed.</summary>
    public override Func<Vector2, Color> Painter(object value, object? previous)
    {
        if (previous is not CategoryField before)
            return Painter(value);
        var field = (CategoryField)value;
        return p =>
        {
            int k = field.Grid.CellAt(p);
            var color = Colors[field.Nearest(p)];
            return k >= 0 && before.Values[k] == field.Values[k] ? Inherited(color) : color;
        };
    }

    /// <summary>Only the categories the editing node wrote, with the share each has now; the rest as one gray entry.</summary>
    public override FieldLegend Legend(object value, object? previous)
    {
        if (previous is not CategoryField before)
            return Legend(value);
        var field = (CategoryField)value;
        var written = Enumerable.Range(0, field.Names.Count).Where(c => field.Counts[c] > before.Counts[c]);
        return new FieldLegend(field.Label + " · 本节点写入的类别", null, "", "",
            written.Select(c => (Colors[c], $"{field.Names[c]} {Share(field.Fraction(c))}"))
                .Append((Inherited(new Color("b0aa9a")), "上游已有的用途")).ToArray());
    }

    private static Color Inherited(Color color)
    {
        float gray = color.R * 0.3f + color.G * 0.59f + color.B * 0.11f;
        return new Color(gray, gray, gray).Lerp(new Color("d9d6cb"), 0.55f);
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
        new("d8e3ae"), new("2f6f9a"), new("4fb39c"), new("a9cfd8"), new("c8553d"), new("2f6b3a"),
    };

    /// <summary>Colors of the growth phases from the first on, oldest darkest; unbuilt land is pale.</summary>
    public static readonly IReadOnlyList<Color> Phases = new Color[]
    {
        new("7a2e22"), new("c8642c"), new("e3a94a"), new("9db06a"), new("6f8fa8"),
    };

    private static readonly Color Unbuilt = new("e9e5d6");

    /// <summary>
    /// Colors of the land uses, indexed by <see cref="LandUse"/>: the town in warm tones, the wall strip pale gray,
    /// the trades dark, the countryside light (woodland dark green, water as in the buildability map).
    /// </summary>
    public static readonly IReadOnlyList<Color> LandColors = new Color[]
    {
        new("d9d6cb"), new("2f6f9a"), new("9c3f2c"), new("c8642c"), new("e3a94a"), new("d38a55"), new("c4bdac"),
        new("a9c26a"), new("8c7a64"), new("3f5577"), new("5a3a34"), new("2f8a8f"), new("2f5e34"), new("6fbfa0"),
        new("cfd85a"), new("8a5a9a"), new("ecd27a"), new("9fc58a"), new("a8a39a"),
    };

    private static readonly Func<float, Color> CutFill = Stops((0, "2f5f9a"), (0.5f, "eeeadb"), (1, "b5452f"));

    private const int GridBlock = 25;
    private static readonly string[] BlockTones = { "e6e0cf", "cdd6c3" };
    private static readonly string[] ZoneNames = { "不能修路（水或太陡）", "城内可修路", "城外可修路", "墙边（不能沿墙修路）", "城门（唯一可穿墙处）" };
    private static readonly Color[] ZoneColors = { new("8c8f94"), new("e3c77a"), new("a9cf8f"), new("c46b4f"), new("b5523b") };

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
        new CellStyle<GrowthHistory>("城镇", Keys.Growth, PhaseColor,
            g => new FieldLegend("生长分期", null, "", "", Enumerable.Range(1, g.Latest)
                .Select(n => (PhaseOf(n), $"{g.Label(n)} +{g.Phases[n - 1].Area / 10000:0.0} ha")).Append((Unbuilt, "未建成")).ToArray()),
            (g, p) => g.Grid.CellAt(p) is int k and >= 0 && g.Built(k) ? g.Label(g.Phase[k]) : "未建成")
        {
            Listed = true,
        },
        // Near the gates pale, far out darker; the ramp ends at the default farming radius and a half.
        new FieldStyle("用地", Keys.Reach, _ => (0, 1800), Stops((0, "f3ecd2"), (0.66f, "c9a86a"), (1, "6e5a44")))
        {
            Format = (_, v) => $"{v:0} m",
            NoDataName = "走不到",
        },
        new CategoryStyle("用地", Keys.LandUse, LandColors),
        // Cut blue, fill red, untouched ground pale.
        new CellStyle<GradePlan>("修整", Keys.Grading, (g, p) => CutFill(Mathf.Clamp(g.Patch.Sample(p) / 4 + 0.5f, 0, 1)),
            g => new FieldLegend("挖填（m）· " + g.Summary, CutFill, "挖 2 m", "填 2 m", null),
            (g, p) => g.Patch.Sample(p) is var d && MathF.Abs(d) < 0.05f ? "未挖填" : $"{(d < 0 ? "挖" : "填")} {MathF.Abs(d):0.0} m")
        {
            Listed = true,
        },
        new CellStyle<SiteGrid>("过程", Keys.Grid, GridColor,
            g => new FieldLegend($"采样网格 · {g.Nx}×{g.Nz} 格，每格 {g.CellSize:0.#} m", null, "", "",
                new[] { (new Color(BlockTones[0]), $"色块边长 {GridBlock * g.CellSize:0} m（{GridBlock} 格）") }),
            (g, p) => $"格 ({(int)MathF.Floor((p.X - g.Origin.X) / g.CellSize)}, {(int)MathF.Floor((p.Y - g.Origin.Y) / g.CellSize)})"),
        new CellStyle<RoadZones>("过程", Keys.RoadZones, (zones, p) => ZoneColors[(int)Zone(zones, p)],
            _ => new FieldLegend("通行区", null, "", "",
                Enum.GetValues<RoadCell>().Select(z => (ZoneColors[(int)z], ZoneNames[(int)z])).ToArray()),
            (zones, p) => ZoneNames[(int)Zone(zones, p)]),
    };

    /// <summary>The style that paints <paramref name="key"/>, or null when it is not painted on the ground.</summary>
    public static GroundStyle? For(IKey key) => All.FirstOrDefault(s => s.Key == key);

    /// <summary>A checkerboard of cells inside a checkerboard of 25-cell blocks, so both the cell size and the extent read.</summary>
    private static Color GridColor(SiteGrid grid, Vector2 p)
    {
        int x = (int)MathF.Floor((p.X - grid.Origin.X) / grid.CellSize);
        int z = (int)MathF.Floor((p.Y - grid.Origin.Y) / grid.CellSize);
        if (!grid.InGrid(x, z))
            return new Color("9a968c");
        var color = new Color(BlockTones[(x / GridBlock + z / GridBlock) & 1]);
        return ((x + z) & 1) == 0 ? color : color.Darkened(0.07f);
    }

    private static Color PhaseOf(int n) => Phases[Math.Min(n, Phases.Count) - 1];

    private static Color PhaseColor(GrowthHistory growth, Vector2 p) =>
        growth.Grid.CellAt(p) is int k and >= 0 && growth.Built(k) ? PhaseOf(growth.Phase[k]) : Unbuilt;

    private enum RoadCell { Blocked, Inside, Outside, NearWall, Gate }

    private static RoadCell Zone(RoadZones zones, Vector2 p)
    {
        int k = zones.Grid.CellAt(p);
        if (k < 0 || !zones.Passable(k))
            return RoadCell.Blocked;
        if (zones.IsGate(k))
            return RoadCell.Gate;
        if (zones.Allowed(k, RoadZone.Inside))
            return RoadCell.Inside;
        return zones.Allowed(k, RoadZone.Outside) ? RoadCell.Outside : RoadCell.NearWall;
    }

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
