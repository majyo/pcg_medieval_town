using Godot;
using System;
using System.Collections.Generic;

namespace PcgCitySandbox.Pcg;

// Only adapts an already prepared map. This class never creates or edits it.
public sealed class TemperateEnvironmentSource : IEnvironmentSource
{
    private readonly TemperateMapData _map;
    public string SourceId { get; }
    public long Revision { get; }
    public Rect2 Bounds { get; }
    public float? NativeSpacing => _map.Spacing;
    public EnvironmentLayers Layers { get; }
    public IReadOnlyList<ConstraintPolygon> NoBuildZones { get; }
    public IReadOnlyList<ConstraintPolygon> Obstacles { get; }

    // Null means not supplied. An explicit empty collection means known empty.
    public TemperateEnvironmentSource(TemperateMapData map, string sourceId, long revision,
        IReadOnlyList<ConstraintPolygon>? noBuildZones, IReadOnlyList<ConstraintPolygon>? obstacles)
    {
        _map = map; SourceId = sourceId; Revision = revision;
        Bounds = new Rect2(-map.Half, -map.Half, map.Half * 2, map.Half * 2);
        NoBuildZones = Copy(noBuildZones); Obstacles = Copy(obstacles);
        Layers = new(LayerStatus.Provided, LayerStatus.Provided, LayerStatus.Provided, LayerStatus.Provided,
            Status(noBuildZones), Status(obstacles));
    }

    public bool Contains(Vector2 p) => p.IsFinite() && _map.Contains(p);
    public EnvironmentPoint Query(Vector2 p)
    {
        var s = _map.Sample(p);
        return new(s.Height, s.SlopeDegrees, s.IsWater, s.Forest);
    }
    private static LayerStatus Status(IReadOnlyList<ConstraintPolygon>? p) => p == null ? LayerStatus.Unavailable : p.Count == 0 ? LayerStatus.KnownEmpty : LayerStatus.Provided;
    private static IReadOnlyList<ConstraintPolygon> Copy(IReadOnlyList<ConstraintPolygon>? p)
    {
        var copy = new ConstraintPolygon[p?.Count ?? 0];
        for (int i = 0; i < copy.Length; i++) copy[i] = new ConstraintPolygon(p![i].Id, p[i].Points);
        return Array.AsReadOnly(copy);
    }
}
