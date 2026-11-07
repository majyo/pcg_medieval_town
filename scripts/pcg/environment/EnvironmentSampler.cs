using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PcgCitySandbox.Pcg;

public readonly record struct PcgProgress(float Fraction, string Message);
public sealed record PcgContext(CancellationToken Cancellation = default, Func<PcgProgress, Task>? Yield = null);

public static class EnvironmentSampler
{
    public static CityEnvironmentData Sample(IEnvironmentSource source, EnvironmentSampleOptions settings) =>
        SampleAsync(source, settings, new PcgContext()).GetAwaiter().GetResult();

    public static async Task<CityEnvironmentData> SampleAsync(IEnvironmentSource source,
        EnvironmentSampleOptions settings, PcgContext context)
    {
        var timer = Stopwatch.StartNew();
        var (nx, nz) = settings.ValidateGrid();
        string id = source.SourceId; long revision = source.Revision;
        var layers = source.Layers;
        if (string.IsNullOrWhiteSpace(id) || layers.Height != LayerStatus.Provided || layers.Slope != LayerStatus.Provided ||
            layers.Water == LayerStatus.Unavailable)
            throw new ArgumentException("环境来源必须提供标识、地面高度、坡度和已知水域层。");
        if (!source.Bounds.Position.IsFinite() || !source.Bounds.Size.IsFinite() || !source.Bounds.End.IsFinite() ||
            source.Bounds.Size.X <= 0 || source.Bounds.Size.Y <= 0)
            throw new ArgumentException("环境覆盖范围无效。");
        var sourceBounds = source.Bounds;
        float? nativeSpacing = source.NativeSpacing;
        if (nativeSpacing.HasValue && (!float.IsFinite(nativeSpacing.Value) || nativeSpacing <= 0))
            throw new ArgumentException("环境原生分辨率无效。");
        foreach (var status in new[] { layers.Water, layers.Vegetation, layers.NoBuild, layers.Obstacles })
            if (!Enum.IsDefined(status)) throw new ArgumentException("环境层状态无效。");
        var zones = CopyPolygons(source.NoBuildZones, layers.NoBuild);
        var obstacles = CopyPolygons(source.Obstacles, layers.Obstacles);
        int side = nx + 1, queries = 0, probes = 0, completePoints = 0;
        var vertices = new EnvironmentPoint[side * (nz + 1)];
        var heights = new float?[vertices.Length];
        var cells = new EnvironmentCell[nx * nz];
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        float dx = settings.Bounds.Size.X / nx, dz = settings.Bounds.Size.Y / nz;

        Vector2 Position(int x, int z) => new(x == nx ? settings.Bounds.End.X : settings.Bounds.Position.X + x * dx,
            z == nz ? settings.Bounds.End.Y : settings.Bounds.Position.Y + z * dz);
        void CheckRun()
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (source.SourceId != id || source.Revision != revision)
                throw new InvalidOperationException("采样期间环境版本改变，请重新运行。");
        }
        async Task Checkpoint(int row, float fraction, string message)
        {
            CheckRun();
            if (context.Yield != null && (row % 8 == 0 || fraction >= 1))
                await context.Yield(new PcgProgress(fraction, message));
            CheckRun();
        }
        EnvironmentPoint Read(Vector2 p)
        {
            probes++;
            if (!ConstraintPolygon.Inside(sourceBounds, p) || !source.Contains(p)) return default;
            queries++;
            EnvironmentPoint s = source.Query(p);
            Validate(s.Height, "高程", p);
            Validate(s.Slope, "坡度", p, 0, 90);
            if (settings.ReadVegetation && layers.Vegetation == LayerStatus.Provided) Validate(s.Vegetation, "植被密度", p, 0, 1);
            bool? water = layers.Water == LayerStatus.KnownEmpty ? false : s.Water;
            float? vegetation = !settings.ReadVegetation || layers.Vegetation == LayerStatus.Unavailable ? null :
                layers.Vegetation == LayerStatus.KnownEmpty ? 0 : s.Vegetation;
            if (s.Height.HasValue) { min = Math.Min(min, s.Height.Value); max = Math.Max(max, s.Height.Value); }
            if (s.Height.HasValue && s.Slope.HasValue && water.HasValue) completePoints++;
            return new(s.Height, s.Slope, water, vegetation);
        }

        for (int z = 0; z <= nz; z++)
        {
            await Checkpoint(z, z / (float)(nz + 1) * 0.5f, "读取地面顶点");
            for (int x = 0; x <= nx; x++)
            {
                int k = z * side + x;
                vertices[k] = Read(Position(x, z)); heights[k] = vertices[k].Height;
            }
        }
        int candidates = 0, blocked = 0, unknown = 0, completeCells = 0;
        var reasonCounts = new Dictionary<EnvironmentRestriction, int>();
        var reasonFlags = Enum.GetValues<EnvironmentRestriction>().Where(r => r != EnvironmentRestriction.None).ToArray();
        foreach (var reason in reasonFlags) reasonCounts[reason] = 0;
        var points = new EnvironmentPoint[5];
        for (int z = 0; z < nz; z++)
        {
            await Checkpoint(z, 0.5f + z / (float)nz * 0.5f, "汇总环境与建设限制");
            for (int x = 0; x < nx; x++)
            {
                Vector2 start = Position(x, z), end = Position(x + 1, z + 1);
                points[0] = vertices[z * side + x]; points[1] = vertices[z * side + x + 1];
                points[2] = vertices[(z + 1) * side + x]; points[3] = vertices[(z + 1) * side + x + 1];
                points[4] = Read((start + end) * 0.5f);
                bool groundKnown = true, slopeKnown = true, waterKnown = true, wet = false;
                float? slope = null;
                foreach (var p in points)
                {
                    groundKnown &= p.Height.HasValue; slopeKnown &= p.Slope.HasValue; waterKnown &= p.Water.HasValue;
                    wet |= p.Water == true;
                    if (p.Slope.HasValue) slope = Math.Max(slope ?? 0, p.Slope.Value);
                }
                var reasons = EnvironmentRestriction.None;
                if (!groundKnown) reasons |= EnvironmentRestriction.NoGroundData;
                if (!slopeKnown || !waterKnown || layers.NoBuild == LayerStatus.Unavailable || layers.Obstacles == LayerStatus.Unavailable)
                    reasons |= EnvironmentRestriction.UnknownLayer;
                if (wet) reasons |= EnvironmentRestriction.Water;
                if (slope > settings.MaxGroundSlope) reasons |= EnvironmentRestriction.TooSteep;
                var rect = new Rect2(start, end - start);
                if (zones.Any(p => p.Intersects(rect))) reasons |= EnvironmentRestriction.NoBuildZone;
                if (obstacles.Any(p => p.Intersects(rect))) reasons |= EnvironmentRestriction.Occupied;
                const EnvironmentRestriction hard = EnvironmentRestriction.Water | EnvironmentRestriction.TooSteep | EnvironmentRestriction.NoBuildZone | EnvironmentRestriction.Occupied;
                var state = (reasons & hard) != 0 ? BuildCandidateState.Blocked : reasons != EnvironmentRestriction.None ? BuildCandidateState.Unknown : BuildCandidateState.Candidate;
                bool complete = groundKnown && slopeKnown && waterKnown;
                cells[z * nx + x] = new EnvironmentCell(slope, wet ? true : waterKnown ? false : null,
                    points[4].Vegetation, reasons, state, complete);
                if (complete) completeCells++;
                switch (state) { case BuildCandidateState.Candidate: candidates++; break; case BuildCandidateState.Blocked: blocked++; break; default: unknown++; break; }
                foreach (var reason in reasonFlags) if ((reasons & reason) != 0) reasonCounts[reason]++;
            }
        }
        if (completePoints == 0) throw new InvalidOperationException("采样范围内没有完整有效的基础环境样本。");
        await Checkpoint(nz, 1, "采样完成");
        if (candidates + blocked + unknown != cells.Length) throw new InvalidOperationException("单元统计不一致。");
        return new CityEnvironmentData(id, revision, nativeSpacing, layers, settings, nx, nz, heights, cells, zones, obstacles,
            new EnvironmentSummary(candidates, blocked, unknown, completeCells, min, max, probes, queries,
                timer.Elapsed.TotalSeconds, new ReadOnlyDictionary<EnvironmentRestriction, int>(reasonCounts)));
    }

    private static ConstraintPolygon[] CopyPolygons(IReadOnlyList<ConstraintPolygon> polygons, LayerStatus status)
    {
        if (status != LayerStatus.Provided && polygons.Count != 0) throw new ArgumentException("未提供或已确认为空的约束层不能包含轮廓。");
        if (polygons.Select(p => p.Id).Distinct().Count() != polygons.Count) throw new ArgumentException("约束层中存在重复 ID。");
        return polygons.Select(p => new ConstraintPolygon(p.Id, p.Points)).ToArray();
    }

    private static void Validate(float? value, string field, Vector2 p, float min = float.NegativeInfinity, float max = float.PositiveInfinity)
    {
        if (value.HasValue && (!float.IsFinite(value.Value) || value < min || value > max))
            throw new InvalidOperationException($"环境来源返回无效{field}，位置 ({p.X}, {p.Y})。");
    }
}
