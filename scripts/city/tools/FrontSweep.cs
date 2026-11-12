using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// Divides blocks among pieces of street front. Each claim takes a strip of a block behind a stretch of front, as deep as
/// asked, minus what earlier claims took; then all claims grow back together, a thin strip each round, so claims facing
/// each other across a block meet near its middle; then the gaps left at bends and corners join the claim they border
/// most. Claim order decides who gets a corner. Plots and terraces are both cut this way.
/// </summary>
public sealed class FrontSweep
{
    private const float Bucket = 40;

    private readonly IReadOnlyList<Region> _blocks;
    private readonly CancellationToken _cancel;
    private readonly List<Region> _shapes = new();
    private readonly List<Region> _within = new();
    private readonly List<int> _blockOf = new();
    private readonly List<(Vector2 A, Vector2 B, Vector2 Inward)> _fronts = new();
    private readonly Dictionary<(int, int), List<int>> _index = new();

    public FrontSweep(IReadOnlyList<Region> blocks, CancellationToken cancel)
    {
        _blocks = blocks;
        _cancel = cancel;
    }

    public IReadOnlyList<Region> Blocks => _blocks;

    /// <summary>What each claim holds so far, in claim order.</summary>
    public IReadOnlyList<Region> Shapes => _shapes;

    /// <summary>The block each claim lies in.</summary>
    public IReadOnlyList<int> BlockOf => _blockOf;

    /// <summary>
    /// Each claim's front: where the claimed land starts, which may lie back from the line it was claimed along (a block
    /// that starts back from the paving), and the direction into the block.
    /// </summary>
    public IReadOnlyList<(Vector2 A, Vector2 B, Vector2 Inward)> Fronts => _fronts;

    /// <summary>The block containing <paramref name="p"/>, or -1.</summary>
    public int BlockAt(Vector2 p)
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i].Bounds.HasPoint(p) && _blocks[i].Contains(p))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Claims the free part of the block behind the line from <paramref name="a"/> to <paramref name="b"/>, out to
    /// <paramref name="depth"/> past the line, that contains the point 2 m in from the line's middle. Gives up (returns
    /// -1) when that point is in no block or already taken, when the part is smaller than <paramref name="minArea"/>, or
    /// when it wraps around an earlier claim. <paramref name="within"/>, when given, bounds the claim and its growth
    /// further.
    /// </summary>
    public int Claim(Vector2 a, Vector2 b, Vector2 inward, float depth, float minArea, Region? within = null)
    {
        var probe = (a + b) / 2 + inward * 2;
        int block = -1;
        for (int i = 0; i < _blocks.Count && block < 0; i++)
        {
            if (_blocks[i].Contains(probe))
                block = i;
        }

        if (block < 0)
            return -1;

        var limit = within == null ? _blocks[block] : _blocks[block].Intersect(within);
        var quad = Region.Polygon(new[] { a, b, b + inward * depth, a + inward * depth });
        var piece = Free(quad.Intersect(limit), quad.Bounds);
        var part = piece.Parts().FirstOrDefault(p => p.Contains(probe));
        // A part wrapped around an earlier claim would keep only its outer ring and overlap that claim.
        if (part == null || part.Area < minArea || part.Rings.Count != 1)
            return -1;

        // The block may start back from the line (a wall lane running on the unbuilt strip by the wall): the claim's
        // front is where its land starts.
        float gap = Math.Max(0, part.Rings[0].Min(p => (p - a).Dot(inward)) - 0.5f);
        int id = _shapes.Count;
        _shapes.Add(part);
        _within.Add(limit);
        _blockOf.Add(block);
        _fronts.Add((a + inward * (0.5f + gap), b + inward * (0.5f + gap), inward));
        Register(id);
        return id;
    }

    /// <summary>
    /// Every claim takes the next <paramref name="step"/> strip behind it in turn, from <paramref name="from"/> to
    /// <paramref name="to"/> past its front, round after round, so claims facing each other across a block meet within a
    /// strip of its middle. A claim stops once nothing behind it is free.
    /// </summary>
    public void GrowBack(float from, float to, float step)
    {
        var growing = Enumerable.Range(0, _shapes.Count).ToList();
        for (float near = from; near < to && growing.Count > 0; near += step)
        {
            _cancel.ThrowIfCancellationRequested();
            float far = Math.Min(near + step, to) + 0.5f;
            var still = new List<int>();
            foreach (int i in growing)
            {
                var (a, b, inward) = _fronts[i];
                var strip = Region.Polygon(new[]
                {
                    a + inward * (near - 1), b + inward * (near - 1), b + inward * far, a + inward * far,
                });
                var free = Free(strip.Intersect(_within[i]), strip.Bounds);
                if (Join(i, free))
                    still.Add(i);
            }

            growing = still;
        }
    }

    /// <summary>
    /// What is left of each block after the claims, up to <paramref name="maxGap"/> m² a piece, joins the claim it
    /// borders most, as far as that claim's limit allows; larger leftovers stay open. <paramref name="eligible"/>, when
    /// given, picks which claims a gap may join (all claims in the block otherwise).
    /// </summary>
    public void FillGaps(float maxGap, Func<int, Region, bool>? eligible = null)
    {
        for (int block = 0; block < _blocks.Count; block++)
        {
            var members = Enumerable.Range(0, _shapes.Count).Where(i => _blockOf[i] == block).ToList();
            if (members.Count == 0)
                continue;

            var taken = Region.Polygons(members.Select(i => _shapes[i].Rings[0]));
            foreach (var gap in _blocks[block].Subtract(taken).Parts())
            {
                if (gap.Area < 1 || gap.Area > maxGap)
                    continue;

                var around = gap.Offset(0.3f);
                var candidates = members.Where(i => eligible == null || eligible(i, gap)).ToList();
                if (candidates.Count == 0)
                    continue;
                var (best, border) = candidates.Select(i => (i, around.Intersect(_shapes[i]).Area)).MaxBy(x => x.Area);
                // Only as much of the gap as the claim may hold (all of it, unless the claim was given a narrower limit).
                if (border > 0.05)
                    Join(best, ReferenceEquals(_within[best], _blocks[block]) ? gap : gap.Intersect(_within[best]));
            }
        }
    }

    /// <summary>Adds the parts of <paramref name="extra"/> touching claim i to it, if it stays one ring; true when it grew.</summary>
    public bool Join(int i, Region extra)
    {
        var touch = _shapes[i].Offset(0.1f);
        var added = extra.Parts().Where(part => part.Intersect(touch).Area > 0.01).ToList();
        if (added.Count == 0)
            return false;

        var merged = added.Aggregate(_shapes[i], (shape, part) => shape.Union(part));
        if (merged.Rings.Count != 1 || merged.Area < _shapes[i].Area + 0.5)
            return false;

        _shapes[i] = merged;
        Register(i);
        return true;
    }

    /// <summary><paramref name="piece"/> minus every claim whose bounds meet <paramref name="bounds"/>.</summary>
    public Region Free(Region piece, Rect2 bounds)
    {
        foreach (int other in Nearby(bounds))
        {
            if (_shapes[other].Bounds.Intersects(bounds))
                piece = piece.Subtract(_shapes[other]);
        }

        return piece;
    }

    private void Register(int id)
    {
        foreach (var cell in Buckets(_shapes[id].Bounds))
        {
            if (!_index.TryGetValue(cell, out var list))
                _index[cell] = list = new List<int>();
            if (!list.Contains(id))
                list.Add(id);
        }
    }

    private IEnumerable<int> Nearby(Rect2 bounds) =>
        Buckets(bounds).SelectMany(k => _index.TryGetValue(k, out var list) ? list : Enumerable.Empty<int>()).Distinct();

    private static IEnumerable<(int, int)> Buckets(Rect2 r)
    {
        for (int z = (int)MathF.Floor(r.Position.Y / Bucket); z <= (int)MathF.Floor(r.End.Y / Bucket); z++)
            for (int x = (int)MathF.Floor(r.Position.X / Bucket); x <= (int)MathF.Floor(r.End.X / Bucket); x++)
                yield return (x, z);
    }
}
