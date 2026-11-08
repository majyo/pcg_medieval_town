namespace PcgCitySandbox.City;

/// <summary>
/// Every piece of data the town graph passes between nodes. The view, the checks and Main read these keys, so
/// they keep working whichever node produces a key.
/// </summary>
public static class Keys
{
    public static readonly Key<Site> Site = new("site", "场地");
    public static readonly Key<TownCore> Core = new("town.core", "城址");
    public static readonly Key<Plaza> Plaza = new("town.plaza", "广场");
    public static readonly Key<WallRing> Wall = new("town.wall", "城墙");
    public static readonly Key<GateSet> Gates = new("town.gates", "城门");
    public static readonly Key<RoadZones> RoadZones = new("road.zones", "通行区");
    public static readonly Key<RoadLinks> RoadLinks = new("road.links", "道路连线");
    public static readonly Key<RoadGraph> Roads = new("roads", "路网");
    public static readonly Key<BlockSet> Blocks = new("blocks", "街区");
    public static readonly Key<ParcelSet> Parcels = new("parcels", "地块");
    public static readonly Key<BuildingSet> Buildings = new("buildings", "建筑");
}
