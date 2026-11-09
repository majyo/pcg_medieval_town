using System.Collections.Generic;

namespace PcgCitySandbox.City;

/// <summary>
/// Transitional adapter: packs the evaluation into the <see cref="Site"/> the layout, road and block nodes still
/// read, on the shared sampling grid (suitability is the habitability score). Goes away once they read the
/// evaluation keys directly.
/// </summary>
public sealed class LegacySite : CityNode<NoOptions>
{
    public override string Group => Evaluation.Group;
    public override string Title => "场地汇总（过渡）";
    public override IReadOnlyList<IKey> Reads => [Keys.Buildable, Keys.Height, Keys.Slope, Keys.Habitability];
    public override IReadOnlyList<IKey> Writes => [Keys.Site];

    protected override void Run(NodeContext c, NoOptions o)
    {
        var buildable = c.Get(Keys.Buildable);
        var height = c.Get(Keys.Height).Values;
        var slope = c.Get(Keys.Slope).Values;
        var habitability = c.Get(Keys.Habitability).Values;
        var site = new Site(buildable.Grid);
        height.CopyTo(site.Height, 0);
        slope.CopyTo(site.Slope, 0);
        habitability.CopyTo(site.Suitability, 0);
        for (int k = 0; k < site.Count; k++)
            site.Block[k] = buildable.Block(k);
        site.BuildableFraction = buildable.Fraction((int)SiteBlock.None);
        c.Set(Keys.Site, site);
    }
}
