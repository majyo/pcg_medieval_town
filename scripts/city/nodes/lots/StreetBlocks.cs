using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class StreetBlocksOptions
{
    [Param("城墙内侧退让 m", 0, 30, 1)] public float WallSetback { get; set; } = 8;
    [Param("最小街区面积 m²", 30, 400, 5)] public float MinArea { get; set; } = 80;
}

/// <summary>Developable land = wall interior minus a setback, the paved area and unbuildable cells; blocks are its parts.</summary>
public sealed class StreetBlocks : CityNode<StreetBlocksOptions>
{
    public override string Group => "街区与地块";
    public override string Title => "街区";
    public override IReadOnlyList<IKey> Reads => [Keys.Site, Keys.Wall, Keys.Roads];
    public override IReadOnlyList<IKey> Writes => [Keys.Blocks];

    protected override void Run(NodeContext c, StreetBlocksOptions o)
    {
        var site = c.Get(Keys.Site);
        var wall = c.Get(Keys.Wall);
        var roads = c.Get(Keys.Roads);
        var developable = wall.Interior.Offset(-o.WallSetback)
            .Subtract(roads.Footprint)
            .Subtract(Unbuildable(site, wall.Interior.Bounds));
        var blocks = developable.Parts().Where(b => b.Area >= o.MinArea).ToList();
        c.Set(Keys.Blocks, new BlockSet(developable, blocks));
    }

    private static Region Unbuildable(Site site, Rect2 bounds)
    {
        var squares = new List<Vector2[]>();
        float s = site.CellSize;
        for (int k = 0; k < site.Count; k++)
        {
            var p = site.Center(k);
            if (site.Buildable(k) || !bounds.Grow(s).HasPoint(p))
                continue;
            squares.Add(new[]
            {
                p + new Vector2(-s, -s) / 2, p + new Vector2(s, -s) / 2,
                p + new Vector2(s, s) / 2, p + new Vector2(-s, s) / 2,
            });
        }

        return squares.Count == 0 ? Region.Empty : Region.Polygons(squares);
    }
}
