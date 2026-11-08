using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

/// <summary>The town generation chain, in order. Takes any terrain; knows nothing about scenes or UI.</summary>
public static class TownPipeline
{
    public static Pipeline Create(ITerrain terrain) => new(
        terrain,
        new SiteStage(),
        new LayoutStage(),
        new RoadStage(),
        new ParcelStage(),
        new BuildingStage());
}
