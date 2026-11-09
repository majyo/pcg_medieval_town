using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class StreetBlocksOptions
{
    [Param("城墙内侧退让 m", 0, 30, 1,
        Hint = "城墙内侧留出多宽不建设（墙根道路和空地）")]
    public float WallSetback { get; set; } = 8;
    [Param("最小街区面积 m²", 30, 400, 5,
        Hint = "比它小的碎块不算街区")]
    public float MinArea { get; set; } = 80;
}

/// <summary>
/// Developable land = built cells inside the wall, minus a setback from the wall, the paved area, landmark plots and
/// unbuildable cells; blocks are its parts. The unbuilt land inside the wall stays gardens.
/// </summary>
public sealed class StreetBlocks : CityNode<StreetBlocksOptions>
{
    public override string Group => "街区与地块";
    public override string Title => "街区";
    public override string Description =>
        "城内已建成的地退让城墙后，去掉路面、地标用地和不可建设地，剩下的连通部分就是街区；城内没建成的地留作菜园。";
    public override IReadOnlyList<IKey> Reads => [Keys.Site, Keys.Wall, Keys.Roads, Keys.Landmarks, Keys.Growth];
    public override IReadOnlyList<IKey> Writes => [Keys.Blocks];

    protected override void Run(NodeContext c, StreetBlocksOptions o)
    {
        var site = c.Get(Keys.Site);
        var wall = c.Get(Keys.Wall);
        var roads = c.Get(Keys.Roads);
        var developable = wall.Interior.Offset(-o.WallSetback)
            .Intersect(c.Get(Keys.Growth).Footprint.Rounded(site.CellSize))
            .Subtract(roads.Footprint)
            .Subtract(c.Get(Keys.Landmarks).Plots)
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
