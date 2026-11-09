using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

/// <summary>
/// A gate wherever a through route crosses the wall: at the route cell on the outer side of the crossing, whose
/// center is put into the wall ring (so the wall is edited). Each gate leads to the map-edge entry reached first by
/// following the routes outward from it.
/// </summary>
public sealed class RouteGates : CityNode<NoOptions>
{
    public override string Group => TownGrowth.Group;
    public override string Title => "城门";
    public override string Description =>
        "过境道路每穿过城墙一次就开一座城门：门点是穿墙处城外一侧的道路格，插进墙线；城门通向沿道路往外最先到达的图边入口。";
    public override IReadOnlyList<IKey> Reads => [Keys.Routes, Keys.Shore];
    public override IReadOnlyList<IKey> Edits => [Keys.Wall];
    public override IReadOnlyList<IKey> Writes => [Keys.Gates];

    protected override void Run(NodeContext c, NoOptions o)
    {
        var network = c.Get(Keys.Routes);
        var shore = c.Get(Keys.Shore).Values;
        var wall = c.Get(Keys.Wall);
        var grid = network.Grid;
        var inside = new Dictionary<int, bool>();
        bool Inside(int k) => inside.TryGetValue(k, out bool v) ? v : inside[k] = wall.Interior.Contains(grid.Center(k));

        // Each crossing: the outer cell (or the inner one where the outer is water) and the way out.
        var crossings = new List<(int Cell, Vector2 Outward)>();
        foreach (var route in network.Routes)
        {
            for (int i = 1; i < route.Cells.Length; i++)
            {
                int a = route.Cells[i - 1], b = route.Cells[i];
                if (Inside(a) == Inside(b))
                    continue;
                int inner = Inside(a) ? a : b, outer = Inside(a) ? b : a;
                int cell = shore[outer] < 0 ? inner : outer;
                if (crossings.All(x => Chebyshev(grid, x.Cell, cell) > 1))
                    crossings.Add((cell, (grid.Center(outer) - grid.Center(inner)).Normalized()));
            }
        }

        var ring = wall.Ring.ToList();
        var gates = new List<Gate>();
        foreach (var (cell, outward) in crossings.OrderBy(x => x.Cell))
        {
            var p = grid.Center(cell);
            Insert(ring, p, grid.CellSize);
            gates.Add(new Gate($"gate_{gates.Count + 1}", p, outward, Entry(network, cell, Inside)));
        }

        if (gates.Count == 0)
        {
            // Cannot happen while the market stands on a route; keep the contract of at least one gate anyway.
            c.Notice("没有过境道路穿过城墙，在离过境道路最近的墙点上开一座门");
            int road = network.Cells.MinBy(k => (ring.Min(q => q.DistanceTo(grid.Center(k))), k));
            var p = grid.Center(grid.CellAt(ring.MinBy(q => q.DistanceTo(grid.Center(road)))));
            Insert(ring, p, grid.CellSize);
            gates.Add(new Gate("gate_1", p, (p - wall.Interior.Bounds.GetCenter()).Normalized(), Entry(network, road, Inside)));
        }

        c.Set(Keys.Wall, wall.With(ring.ToArray()));
        c.Set(Keys.Gates, new GateSet(gates));
    }

    private static int Chebyshev(SiteGrid grid, int a, int b) =>
        Math.Max(Math.Abs(a % grid.Nx - b % grid.Nx), Math.Abs(a / grid.Nx - b / grid.Nx));

    /// <summary>Puts <paramref name="p"/> into the ring on its nearest edge, dropping ring points closer than a cell to it.</summary>
    private static void Insert(List<Vector2> ring, Vector2 p, float cell)
    {
        int edge = Enumerable.Range(0, ring.Count)
            .MinBy(i => Geometry.DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Count]));
        ring.Insert(edge + 1, p);
        for (int i = ring.Count - 1; i >= 0; i--)
        {
            if (ring[i] != p && ring[i].DistanceTo(p) < cell && ring.Count > 4)
                ring.RemoveAt(i);
        }
    }

    /// <summary>The map-edge entry reached first by following route links outward from a gate cell, never into the town.</summary>
    private static Vector2 Entry(RouteNetwork network, int start, Func<int, bool> inside)
    {
        var neighbors = new Dictionary<int, List<int>>();
        foreach (var (a, b) in network.Links)
        {
            (neighbors.TryGetValue(a, out var la) ? la : neighbors[a] = new List<int>()).Add(b);
            (neighbors.TryGetValue(b, out var lb) ? lb : neighbors[b] = new List<int>()).Add(a);
        }

        var entries = network.Entries.ToDictionary(e => e.Cell);
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out int k))
        {
            if (entries.TryGetValue(k, out var entry))
                return entry.Position;
            foreach (int j in neighbors.GetValueOrDefault(k, new List<int>()))
            {
                if (!inside(j) && seen.Add(j))
                    queue.Enqueue(j);
            }
        }

        return network.Entries.MinBy(e => e.Position.DistanceTo(network.Grid.Center(start)))!.Position;
    }
}
