using PcgCitySandbox.Terrain;

namespace PcgCitySandbox.City;

/// <summary>
/// The town generation graph. List order is run order; wiring follows from the keys each node reads, writes and
/// edits. To swap an algorithm, replace its line with another node over the same keys.
/// </summary>
public static class TownGraph
{
    public static CityGraph Create(ITerrain terrain) => new(
        terrain,
        new SampleGrid(),
        new SampleHeight(),
        new SlopeAspect(),
        new SurfaceWater(),
        new LandformPosition(),
        new SampleForest(),
        new SiteAnalysis(),
        new ChooseCore(),
        new SquarePlaza(),
        new StarWall(),
        new PlaceGates(),
        new RoadZoning(),
        new MainStreets(),
        new OuterRoads(),
        new StreetMesh(),
        new ExtractRoads(),
        new StreetBlocks(),
        new FrontageParcels(),
        new GableHouses());
}
