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
        new BuildableLand(),
        new Habitability(),
        new TravelCost(),
        new LegacySite(),
        new RiverCrossings(),
        new RegionalRoutes(),
        new ChooseOrigin(),
        new Village(),
        new MarketPlace(),
        new ChurchSite(),
        new MarketGrowth(),
        new EnclosingWall(),
        new RouteGates(),
        new WalledInfill(),
        new Suburbs(),
        new TownReach(),
        new TownQuarters(),
        new Workshops(),
        new Countryside(),
        new RoadZoning(),
        new SkeletonLinks(),
        new StreetMesh(),
        new ExtractRoads(),
        new StreetBlocks(),
        new FrontageParcels(),
        new GableHouses());
}
