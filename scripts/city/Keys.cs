using System.Collections.Generic;

namespace PcgCitySandbox.City;

/// <summary>
/// Every piece of data the town graph passes between nodes. The view, the checks and Main read these keys, so
/// they keep working whichever node produces a key.
/// </summary>
public static class Keys
{
    public static readonly Key<SiteGrid> Grid = new("site.grid", "采样网格",
        "所有采样场共用的方格网：覆盖全图，以原点为中心");
    public static readonly Key<ScalarField> Height = new("site.height", "高程",
        "每格的地面高程（格内多点平均，滤掉米级起伏）");
    public static readonly Key<ScalarField> Slope = new("site.slope", "坡度",
        "每格的坡度，0° 为平地");
    public static readonly Key<ScalarField> Aspect = new("site.aspect", "坡向",
        "每格朝向的方位（下坡方向，北为 0°，顺时针）；平地无值");
    public static readonly Key<ScalarField> Shore = new("site.shore", "离水距离",
        "到最近河道边的距离；在水里为负");
    public static readonly Key<ScalarField> AboveWater = new("site.above_water", "高出水面",
        "地面比最近河段水位高出多少；低处易淹");
    public static readonly Key<ScalarField> Relief = new("site.relief", "地形位置",
        "比周围平均高程高出多少：正为岗地、台地边缘，负为谷地");
    public static readonly Key<ScalarField> Forest = new("site.forest", "林地",
        "林地覆盖，0 为空地，1 为密林");
    public static readonly Key<CategoryField> Buildable = new("eval.buildable", "可建设判定",
        "每格能否建设；不能时给出第一个原因：水域、易淹、河岸退让、坡度过大、森林");
    public static readonly Key<ScalarField> Habitability = new("eval.habitability", "宜居度",
        "每格适合居住的程度，0–1；不可建设的格为 0");
    public static readonly Key<ScalarField> TravelCost = new("eval.travel_cost", "通行代价",
        "走 1 m 相当于在平地上走几米；水面和陡崖不可通行（无值）");
    public static readonly Key<CrossingSet> Crossings = new("region.crossings", "渡口",
        "适合过河的地方，每处有两岸的桥头");
    public static readonly Key<RouteNetwork> Routes = new("region.routes", "过境道路",
        "城镇出现之前就有的道路：图边入口之间的干线和支线，只在渡口过河");
    public static readonly Key<TownOrigin> Origin = new("town.origin", "起源点",
        "城镇最早出现的地点（桥头、路口或路边）");
    public static readonly Key<GrowthHistory> Growth = new("town.growth", "生长分期",
        "每格建成于第几期：一期村落、二期市镇、三期城内填充……；后一期在前一期上累加");
    public static readonly Key<Plaza> Plaza = new("town.plaza", "市场",
        "城镇的市场广场：路口处的三角形，或道路加宽成的纺锤形");
    public static readonly Key<LandmarkSet> Landmarks = new("town.landmarks", "地标",
        "公共建筑的用地：教堂、城堡、市政厅、修道院，城外的麻风院和刑场");
    public static readonly Key<WallRing> Wall = new("town.wall", "城墙",
        "城墙的环线和它围住的城内范围");
    public static readonly Key<GateSet> Gates = new("town.gates", "城门",
        "过境道路穿过城墙处的城门，每座通向一个图边入口");
    public static readonly Key<ScalarField> Reach = new("land.reach", "离城路程",
        "从各城门出发、按过境道路的通行代价走到每一格的路程（只有使用中的渡口能过河）；城内为 0，走不到的格无值");
    public static readonly Key<CategoryField> LandUse = new("land.use", "用地",
        "全图每一格的用途：城镇分区写出城镇和水域，作坊、农业带依次把其余的格分完");
    public static readonly Key<RoadZones> RoadZones = new("road.zones", "通行区",
        "每格能否修路，在城内还是城外；只能在城门处穿墙");
    public static readonly Key<RoadLinks> RoadLinks = new("road.links", "道路连线",
        "相邻格之间的道路连线（带等级）：骨架道路抄自过境道路（城内为主街、城外为城外道路），街巷网再累加");
    public static readonly Key<StreetPlan> StreetPlan = new("road.plan", "规划街巷",
        "三期、四期成片的地上的规划区：每片的引导线（最近的过境道路）、平行街和横街；有机街巷不进这些格");
    public static readonly Key<SquareSet> Squares = new("road.squares", "小广场",
        "城门内侧的门内广场、教堂西门前的前庭、城堡门前的空地；提取路网时并进铺装区");
    public static readonly Key<RoadGraph> Roads = new("roads", "路网",
        "由连线提取出的路网：路口、路段折线、路宽和铺装范围");
    public static readonly Key<BlockSet> Blocks = new("blocks", "街区",
        "可建区（城内外的建成格，城墙两侧退让，去掉道路和不可建设地）分成的街区");
    public static readonly Key<ParcelSet> Parcels = new("parcels", "地块",
        "沿街切出的临街地块");
    public static readonly Key<FixtureSet> Fixtures = new("fixtures", "公共设施",
        "立在铺装上的公共设施：水井（每个建成格离井或水不超过 80 m），以后还有市场十字");
    public static readonly Key<BridgeSet> Bridges = new("bridges", "桥",
        "道路过河处的桥：水平的桥面、两岸的桥台和水里的桥墩");
    public static readonly Key<TerraceSet> Terraces = new("terraces", "台地",
        "街区沿街分成的台地：每块临街一段是水平的，后面是它的地块可以延伸到的后院；地标用地各一块。地块在台地里切，房屋地坪取台地的高度");
    public static readonly Key<GradePlan> Grading = new("grading", "地面修整",
        "修整后的地面：道路走廊、平台、挡土墙，以及由它们算出的地面高度；规划仍读原地形，画出来和建起来的东西读它");
    public static readonly Key<BuildingSet> Buildings = new("buildings", "建筑",
        "每个地块上的房屋");
    public static readonly Key<YardSet> Yards = new("yards", "院落",
        "地块之间的院墙（城内）和篱笆（城外）、深后院尾端的后屋、房屋和后屋之外的菜园");

    /// <summary>Every sampled field, in sampling order.</summary>
    public static readonly IReadOnlyList<Key<ScalarField>> Fields = [Height, Slope, Aspect, Shore, AboveWater, Relief, Forest];

    /// <summary>Every scalar evaluation of the site.</summary>
    public static readonly IReadOnlyList<Key<ScalarField>> Scores = [Habitability, TravelCost];
}
