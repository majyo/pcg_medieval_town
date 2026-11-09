using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PcgCitySandbox.City;

public sealed class StreetBlocksOptions
{
    [Param("城墙内侧退让 m", 0, 30, 1,
        Hint = "城墙内侧留出多宽不建设（墙根道路和空地）")]
    public float WallSetback { get; set; } = 8;
    [Param("城墙外侧退让 m", 0, 60, 1,
        Hint = "城墙外侧留出多宽不建设（墙外防守用的空地），与四期的城外离墙距离一致")]
    public float OutsideSetback { get; set; } = 15;
    [Param("最小街区面积 m²", 30, 400, 5,
        Hint = "比它小的碎块不算街区")]
    public float MinArea { get; set; } = 80;
}

/// <summary>
/// Developable land = the built cells, kept clear of the wall on both sides, minus the paved area, landmark plots and
/// unbuildable cells; blocks are its parts. Unbuilt land (gardens, steep ground) stays open.
/// </summary>
public sealed class StreetBlocks : CityNode<StreetBlocksOptions>
{
    // Blocks run along the road edges for kilometers; a hair of clearance keeps rounding slivers off the paving.
    private const float PavingClearance = 0.05f;

    public override string Group => "街区与地块";
    public override string Title => "街区";
    public override string Description =>
        "城内外已建成的地在城墙两侧各留出空地，去掉路面、地标用地和不可建设地，剩下的连通部分就是街区；没建成的地（陡坡、菜园）不建房。";
    public override IReadOnlyList<IKey> Reads => [Keys.Site, Keys.Wall, Keys.Roads, Keys.Landmarks, Keys.Growth];
    public override IReadOnlyList<IKey> Writes => [Keys.Blocks];

    protected override void Run(NodeContext c, StreetBlocksOptions o)
    {
        var site = c.Get(Keys.Site);
        var wall = c.Get(Keys.Wall);
        var roads = c.Get(Keys.Roads);
        var growth = c.Get(Keys.Growth);
        var built = growth.Footprint.Rounded(site.CellSize);
        var developable = wall.Interior.Offset(-o.WallSetback).Intersect(built)
            .Union(built.Subtract(wall.Interior.Offset(o.OutsideSetback)))
            .Subtract(roads.Footprint.Offset(PavingClearance))
            .Subtract(c.Get(Keys.Landmarks).Plots)
            .Subtract(Unbuildable(site, growth));
        var blocks = developable.Parts().Where(b => b.Area >= o.MinArea).ToList();
        c.Set(Keys.Blocks, new BlockSet(developable, blocks));
    }

    /// <summary>
    /// Unbuildable cells next to a built cell: the only ones the rounded built area can reach. The site shares the
    /// growth grid, so cell indices agree.
    /// </summary>
    private static Region Unbuildable(Site site, GrowthHistory growth)
    {
        var squares = new List<Vector2[]>();
        float s = site.CellSize;
        for (int k = 0; k < site.Count; k++)
        {
            if (site.Buildable(k) || !NextToBuiltCell(growth, k))
                continue;
            var p = site.Center(k);
            squares.Add(new[]
            {
                p + new Vector2(-s, -s) / 2, p + new Vector2(s, -s) / 2,
                p + new Vector2(s, s) / 2, p + new Vector2(-s, s) / 2,
            });
        }

        return squares.Count == 0 ? Region.Empty : Region.Polygons(squares);
    }

    private static bool NextToBuiltCell(GrowthHistory growth, int k)
    {
        var grid = growth.Grid;
        int x = k % grid.Nx, z = k / grid.Nx;
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (grid.InGrid(x + dx, z + dz) && growth.Built(grid.Index(x + dx, z + dz)))
                    return true;
            }
        }

        return false;
    }
}
