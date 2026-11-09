using System.Collections.Generic;
using System.Threading.Tasks;

namespace PcgCitySandbox.City;

/// <summary>Forest cover (0 open, 1 closed woodland) at each cell center, as the terrain reports it.</summary>
public sealed class SampleForest : CityNode<NoOptions>
{
    public override string Group => Sampling.Group;
    public override string Title => "林地";
    public override IReadOnlyList<IKey> Reads => [Keys.Grid];
    public override IReadOnlyList<IKey> Writes => [Keys.Forest];

    protected override void Run(NodeContext c, NoOptions o)
    {
        var grid = c.Get(Keys.Grid);
        var terrain = c.Terrain;
        var forest = new float[grid.Count];
        var options = new ParallelOptions { CancellationToken = c.Cancellation };
        Parallel.For(0, grid.Count, options, k => forest[k] = terrain.Sample(grid.Center(k)).Forest);
        c.Set(Keys.Forest, new ScalarField(grid, forest, "林地", ""));
    }
}
