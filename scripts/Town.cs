using PcgCitySandbox.Core;
using PcgCitySandbox.Stages;

namespace PcgCitySandbox;

/// <summary>The town generation chain, in order.</summary>
public static class Town
{
    public static Pipeline CreatePipeline(TemperateMapData map) => new(
        map,
        new SiteStage(),
        new LayoutStage(),
        new RoadStage(),
        new ParcelStage(),
        new BuildingStage());

    public static TemperateMapData CreateMap(MapSettings settings, int seed)
    {
        var copy = (MapSettings)settings.Duplicate();
        copy.Seed = seed;
        return new TemperateMapData(copy);
    }
}
