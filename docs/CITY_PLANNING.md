# 城市规划阶段 · 方案

> 状态：第 1 步（场地评价）已完成，其余未实施。基于节点图架构（[ARCHITECTURE.md](ARCHITECTURE.md) §2–5）和场地采样第一阶段（[SITE_SAMPLING.md](SITE_SAMPLING.md)）。本方案同时覆盖场地采样 §7 留下的“第二阶段”。

## 1. 目标与范围

城市规划阶段回答三个问题：**这块地哪里能住、城镇为什么在这里、它是怎么一期一期长成现在这样的。**

它替换现有的“场地”和“城镇布局”两个分组，以及“路网”里的主街、城外道路，分成三组：

**场地评价 → 区域格局 → 城镇生长**

| 分组 | 做什么 | 输出的性质 |
| --- | --- | --- |
| 场地评价 | 把采样场变成判定和评分：能不能建、住得好不好、走起来多费劲 | 逐格的判定、0–1 评分、代价倍数 |
| 区域格局 | 城镇出现之前就存在的东西：渡口、过境道路、道路交汇处，并据此选出城镇的起源点 | 点、折线 |
| 城镇生长 | 从起源点按历史分期长出城镇：村落 → 市镇 → 筑墙 → 城外街带，最后划功能分区 | 逐格的“建成于第几期”、广场、地标、城墙、城门、分区 |

城镇的形态由**分期生长**产生，而不是一次画好：先有路和渡口，村落沿路长出来，市场插在路口，城墙围住已经建成的部分并留出余地，城门落在道路穿墙的地方，墙外再沿路长出街带。这样城门、主街、城外道路都是“本来就在那里的路”，不需要单独设计。

范围之内：

- 用采样场重写可建设判定（替换 `SiteAnalysis` 和 `site` 键），统一到 4 m 采样网格。
- 宜居评分、通行代价、渡口、过境道路、起源点。
- 四期生长、市场、教堂、城墙、城门、功能分区。
- 把骨架道路交给路网分组（替换 `MainStreets`、`OuterRoads`）。

明确不做：

- **不动路网细化、地块、建筑的算法**：`StreetMesh`、`ExtractRoads`、`StreetBlocks`、`FrontageParcels`、`GableHouses` 只做必要的适配（读新键、适应 4 m 网格），逻辑不变。分区、地标暂时不被它们读取。
- **不做人口、经济或智能体模拟。** “生长”是几何过程：按目标面积沿道路扩展区域，确定、可复现，每期一组节点。
- **不做复杂水文**：只用离水距离、高出水面两个场（见 SITE_SAMPLING §1）。“易淹”就是高出水面低于阈值。
- 不改地形生成和 `ITerrain`。
- 不做城堡、修道院、多座城镇（见 §9 待定问题）。

## 2. 现状与问题

- `SiteAnalysis` 在自己的 8 m 网格上重新采样地形，按阈值判定可建设；采样场算好了却没人用。
- `ChooseCore` 的打分是硬编码的几项（开阔度、地形适宜性、离河距离、离图心距离），和道路、渡口没有关系——城址“为什么在这里”说不出理由。
- `StarWall` 先定规划半径再画一个带起伏的圆，城门按扇区均分，主街和城外道路再从城门修出去。顺序是反的：真实的中世纪城镇是路先于墙，城门开在路上。
- 城外道路无法过河（没有渡口/桥的概念），城门朝河时道路到不了地图边缘。
- 城内全是同一种街巷和临街地块，没有市场、教堂、手工业区、菜园这些结构。

## 3. 分组与节点

所有新节点只读采样场和彼此的键。网格统一用 `site.grid`（默认 4 m）。

### 3.1 场地评价

| 节点 | 读 → 写 | 内容 | 主要参数 |
| --- | --- | --- | --- |
| `BuildableLand` 可建设判定 | slope、shore、above_water、forest → `eval.buildable` | 逐格判定及原因，按优先级取第一个：水域（离水 < 0）、易淹（高出水面 < 阈值）、河岸退让（离水 < 退让）、坡度过大、森林。替换 `SiteAnalysis` | 易淹高度 m（默认 2.5）、河岸退让 m、坡度上限 °、森林覆盖上限 |
| `Habitability` 宜居度 | buildable、slope、shore、above_water、relief、aspect → `eval.habitability` | 0–1，不可建设格为 0。几项因子相乘：坡度平缓；近水但不贴水（离水在取水距离内最好，太远递减）；高出水面在 2–6 m 的台地最好；地形位置略高于周围（TPI > 0）加分；朝南坡略加分 | 每项因子的理想区间和权重 |
| `TravelCost` 通行代价 | slope、forest、shore → `eval.travel_cost` | 每走 1 m 相当于平地几米（≥ 1）：随坡度上升，林地加价；水面和陡崖（默认 > 20°）不可通行（无值）。与方向无关：河谷两侧的谷坡普遍 9–15°，若按 12° 的道路坡度上限截断，整条谷坡都会变成屏障，路永远到不了河边；所以这里只加价，顺坡斜着爬升由第 2 步的道路搜索按行进方向的坡度计算 | 可通行坡度上限 °、每度坡度加价、林地加价 |

`Habitability` 和 `TravelCost` 都是 `ScalarField`，自动获得着色、图例、探针和摘要。`eval.buildable` 是新的分类场（§4.1）。

**过渡用的适配节点** `LegacySite`（“场地汇总（过渡）”）：读 `eval.buildable`、height、slope、habitability，在 `site.grid` 上写出旧的 `site`（`Site` 本来就基于 `SiteGrid`，直接复用同一个网格实例），让下游在第 1 步不改代码就能跑；第 6 步删除。

### 3.2 区域格局

这张图是更大地域中的一块：南北向的河把它分成东西两岸，道路从图边进来，在某处过河、在某处交汇。城镇长在这些地方。

| 节点 | 读 → 写 | 内容 | 主要参数 |
| --- | --- | --- | --- |
| `RiverCrossings` 渡口 | shore、above_water、slope → `region.crossings` | 沿河道中线（离水场的局部最小值）每 20 m 取一个横断面，评价：河窄（横断面上离水 < 0 的宽度小）、两岸高出水面适中（1–6 m，不是陡岸也不是滩涂）、两岸 30 m 内坡度平缓。取得分最高、彼此相距足够远的几处 | 候选间距 m、最大河宽 m、个数 |
| `RegionalRoutes` 过境道路 | travel_cost、crossings → `region.routes` | ① 在图边选入口：每条边按通行代价和间距打分，河两岸至少各一个，沿河谷的南北两端优先。② 所有入口两两求最小代价路径，水面只能经过渡口（渡口格有额外代价）。③ 按代价做最小生成树，依次修路，已修道路打折（同 `RoadSearch` 的做法），路自然并线，交汇点自然出现 | 入口数（2–5，默认 4）、渡口代价、并线折扣、种子 |
| `ChooseOrigin` 起源点 | routes、crossings、habitability → `town.origin` | 候选：道路交汇点、渡口桥头、道路上的点。得分 = 周围半径 R 内的宜居度总和 + 交汇/桥头加分 − 离图心距离的小惩罚。记录起源类型（桥头、路口、路边），供摘要和后续节点参考。替换 `ChooseCore` | 评价半径 m、交汇加分、桥头加分、种子 |

### 3.3 城镇生长

每一期是一组节点，后一期读前一期的结果。生长记录是累加型数据 `town.growth`（与 `road.links` 同样的模式）：每期在副本上标出新建成的格并写回。

| 期 | 节点 | 读 → 写 | 内容 | 主要参数 |
| --- | --- | --- | --- | --- |
| 一期 村落 | `Village` 村落 | origin、routes、habitability、buildable → `town.growth` | 从起源点沿道路长出约 2 ha 的带状村落（§3.4 的生长工具） | 目标面积 ha、临路进深 m |
| 二期 市镇 | `MarketPlace` 市场 | origin、routes、growth、buildable → `town.plaza` | 在村落范围内、最靠近道路交汇处的平地上开出市场：道路加宽成的纺锤形或三角形。现有 `Plaza` 类型不变；以后可换方形、街市等变体 | 面积 m²、形状 |
| | `ChurchSite` 教堂 | plaza、growth、relief、buildable → `town.landmarks` | 市场附近地势最高的可建设处，离主路一小段，矩形用地东西朝向（祭坛朝东） | 搜索半径 m、用地尺寸 |
| | `MarketGrowth` 市镇扩展 | plaza、routes、landmarks、habitability；编辑 `town.growth` | 以市场为中心沿道路扩展到约 6 ha | 目标面积 ha |
| 三期 筑墙 | `EnclosingWall` 城墙 | growth、buildable、relief、shore → `town.wall` | 围住一、二期建成区并留出余地（默认面积再加 60%）：建成区外扩后取平滑外轮廓，靠近河岸时贴着河岸退让线走（河当护城河），遇到岗地边缘（TPI 高）时外推到边缘上。替换 `StarWall` | 余地比例、平滑度、最少顶点间距 m |
| | `RouteGates` 城门 | routes；编辑 `town.wall` → `town.gates` | 每条道路穿过城墙的地方开一座门，门点对齐到格心；同一条路多次穿墙时城墙局部外凸把它包进来，相距太近的门合并。`Gate.Entry` 是这条路的图边入口。替换 `PlaceGates` | 城门最小间距 m |
| | `WalledInfill` 城内填充 | wall、routes、plaza；编辑 `town.growth` | 城内沿道路继续填充到城内面积的一定比例，其余留作菜园 | 填充比例 |
| 四期 城外 | `Suburbs` 城外街带 | gates、routes、wall；编辑 `town.growth` | 从城门沿道路向外长出街带，面积为城内建成区的一定比例；渡口桥头另长一小片 | 面积比例、最大离门距离 m |
| 分区 | `LandUse` 功能分区 | growth、plaza、landmarks、shore、wall → `town.land_use` | 已建成或城内的每格一个用途：市场核心（离市场、教堂近）、手工业（近水，鞣革、染坊、水磨需要水）、居住（其余临路处）、菜园（城内未建成）、城外街带（四期） | 核心半径 m、手工业离水距离 m |

`town.core` 退役：原来读它的地方（`Main` 取景、`Checks`）改读 `town.origin` 或 `town.plaza`。

### 3.4 共用工具：沿路生长（`tools/Growth.cs`）

各期节点只决定“从哪里长、长多大”，生长本身是同一个工具：

1. 在道路格上从种子点（起源点、市场或城门）做 Dijkstra，得到每个道路格的“沿路距离”。
2. 每个未建成的可建设格取最近道路格，得分 = 该道路格的沿路距离 + 进深系数 × 离路距离 − 宜居度权重 × 宜居度。
3. 按得分从小到大收入，直到达到目标面积；只收入与已建成区或道路连通的格。

离路距离超过临路进深的格得分急剧上升，所以早期是沿路的带状，面积大了才往纵深填。节点可以传入额外的限制（城内/城外）。

### 3.5 路网分组的衔接

| 节点 | 变化 |
| --- | --- |
| `RoadZoning` 通行区 | 改读 `eval.travel_cost`（无值 = 不可通行）、`town.wall`、`town.gates`，其余不变 |
| `SkeletonLinks` 骨架道路（新） | 读 `region.routes`、`road.zones`、`town.plaza`、`town.gates` → `road.links`：道路在城内的部分记为主街，城外的部分记为城外道路，市场接入，锚点照旧（core、gate、entry）。替换 `MainStreets` 和 `OuterRoads` |
| `StreetMesh`、`ExtractRoads` | 不变（在 4 m 网格上重新核对参数和耗时） |

渡口在 `road.links` 里是一段普通的城外道路，`ExtractRoads` 把它变成路段；`roads` 的契约“不进水域”要放开为“只在渡口处进入水域”。桥的造型属于渲染，放到以后。

## 4. 数据与键

### 4.1 新类型

- `CategoryField`（加进 `city/data/Fields.cs`）：共用 `SiteGrid`，一格一个 `byte`，带各类别的名称。`eval.buildable`（原因：可建设、水域、易淹、河岸退让、坡度过大、森林）和 `town.land_use` 都用它。着色、图例、探针按类别取色。
- `CrossingSet`：渡口列表，每个渡口有位置、过河方向、两岸桥头点、河宽、得分。
- `RouteNetwork`：图边入口（位置、所在的边、在河哪一岸）和道路列表；每条道路是一串 `site.grid` 格号加简化后的折线，记录经过的渡口。
- `TownOrigin`：位置、起源类型（桥头 / 路口 / 路边）、相关道路。
- `GrowthHistory`：`byte[] Phase`（每格建成于第几期，0 为未建成）加每期的名称和面积。期是嵌套的，所以“显示到第 N 期”就是 `Phase ≤ N`；需要多边形时按需从格转成 `Region`。
- `LandmarkSet`：地标列表（类型、`Region` 用地、所属期）。目前只有教堂，市场仍是 `town.plaza`。

`Plaza`、`WallRing`、`Gate`、`GateSet`、`RoadZones`、`RoadLinks` 保持不变。

### 4.2 键

| 键 | 类型 | 写 | 编辑 |
| --- | --- | --- | --- |
| `eval.buildable` | `CategoryField` | `BuildableLand` | |
| `eval.habitability` | `ScalarField` | `Habitability` | |
| `eval.travel_cost` | `ScalarField` | `TravelCost` | |
| `region.crossings` | `CrossingSet` | `RiverCrossings` | |
| `region.routes` | `RouteNetwork` | `RegionalRoutes` | |
| `town.origin` | `TownOrigin` | `ChooseOrigin` | |
| `town.growth` | `GrowthHistory` | `Village` | `MarketGrowth`、`WalledInfill`、`Suburbs` |
| `town.plaza` | `Plaza` | `MarketPlace` | |
| `town.landmarks` | `LandmarkSet` | `ChurchSite` | |
| `town.wall` | `WallRing` | `EnclosingWall` | `RouteGates` |
| `town.gates` | `GateSet` | `RouteGates` | |
| `town.land_use` | `CategoryField` | `LandUse` | |

退役：`site`（第 6 步）、`town.core`。

### 4.3 规模与耗时预算

4 m 格全图 25 万格。场地评价逐格并行；过境道路是 4–10 次全图 A\*；生长是几次局部 Dijkstra。目标：规划三组合计 < 0.5 s（Debug），整张图不慢于现在太多。路网在 4 m 网格上的 A\* 格数是原来的 4 倍，第 1 步实测，必要时给 `RoadSearch` 换成数组而不是字典。

## 5. 可视化

### 5.1 阶段页

| 阶段 | 地面着色 | 叠加图层 |
| --- | --- | --- |
| 场地评价 | 可建设判定（分类）、宜居度、通行代价 | — |
| 区域格局 | 宜居度 | 过境道路（按全图视角画粗线）、图边入口、渡口（标出过河方向）、起源点 |
| 城镇生长 | 生长分期 或 功能分区（分类） | 市场、教堂、城墙、城门；阶段页加一个“显示到第 N 期”的滑条，图层和着色都只显示到该期 |

新增图层（`CityLayer`）：`Region`（道路、入口、渡口、起源点）、`Growth`（按期着色的建成区，叠在地面上）、`Landmarks`。原来的 `Site` 图层改为显示 `eval.buildable`。

### 5.2 着色与探针

- `FieldStyles` 增加分类色带：每个类别一种颜色，图例逐项列出。宜居度用顺序色带，通行代价用对数色带（1×–5×，不可通行灰色）。
- 探针读数增加：`可建设 / 易淹`、`宜居度 0.72`、`通行 1.8×`、`第 2 期 · 居住`。
- 节点摘要示例：渡口“3 处，最窄 41 m”；过境道路“4 个入口，3 条道路，12.6 km，1 处过河”；起源点“桥头 (−212, 348)”；生长“一期 2.1 ha / 二期 6.0 ha / 三期 14.3 ha / 四期 3.2 ha”。

### 5.3 截图

`capture` 增加：区域格局的全图俯视（道路、渡口、起源点）；城镇生长四期各一张同角度透视；功能分区透视。

## 6. 不变量（写进 `Contracts`）

| 键 | 检查 |
| --- | --- |
| `eval.buildable` | 使用共用网格；离水 < 0 的格都是“水域”；“易淹”的格高出水面都低于阈值；可建设格坡度不超过上限 |
| `eval.habitability` | 在 [0, 1]；不可建设格为 0；至少有一格大于 0.5 |
| `eval.travel_cost` | 有值的格 ≥ 1；水面格都无值 |
| `region.crossings` | 至少一处；每处中点在水面上，两个桥头都是可通行的干地，河宽不超过上限 |
| `region.routes` | 入口都在图边 10 m 以内；道路网连通；每一格可通行或属于某个渡口；相邻格是 8 邻域 |
| `town.origin` | 在可建设格上，离最近道路不超过一格进深 |
| `town.growth` | 各期面积递增且与参数目标相差在一成以内；建成格都可建设；每期的新格都与此前的建成区或道路连通 |
| `town.plaza` | 在一期建成区内或紧邻，在城墙内（沿用） |
| `town.landmarks` | 教堂用地在城墙内、在可建设格上，不压道路和市场 |
| `town.wall` | 简单多边形；包住一、二期全部建成格、市场和教堂；不进水域 |
| `town.gates` | 至少一座，都在城墙上（沿用）；道路穿墙的每一处都有城门，城门都在道路上 |
| `town.land_use` | 城内每格都有用途；“手工业”格离水都在设定距离内；“城外街带”格都在城外 |
| `roads` | 原有检查不变，“不进水域”改为“只在渡口处进入水域” |

节点图检查照旧：不改读到的值、过期按依赖传播（例如让“渡口”过期后，场地评价不受影响，区域格局及其下游全部过期）、两次生成一致。三个种子的城镇会和现在不同，不再要求逐位相同。

## 7. 界面与阶段

阶段列表变为：地形 → 场地采样 → 场地评价 → 区域格局 → 城镇生长 → 路网 → 街区与地块 → 建筑（数字键 1–8）。阶段由节点的 `Group` 自动生成，不需要改 `StageRail`；需要做的是 `MainInterface` 里三个新阶段的可视化选项，以及“显示到第 N 期”的 `Setting<int>`。

## 8. 实施顺序

先广后深：每一步结束时三个种子都能跑完、检查全过、截图可看。

1. ✅ **场地评价**：`CategoryField`、`BuildableLand`、`Habitability`、`TravelCost`、`LegacySite`；分类色带、探针；下游切到 4 m 网格并核对参数与耗时。删除 `SiteAnalysis`，地形自带的“适宜性”着色由宜居度取代。
   - 结果（三个种子）：可建设约 70%，其余依次是森林 15–18%、坡度过大 5–7%、水域 3%、易淹 3%；“河岸退让”几乎为 0，因为河岸低处先被判为易淹。宜居度中位数 0.3，高值集中在河谷台地上。
   - 下游在 4 m 网格上无需改参数，只有 `PlaceGates` 的“门外开阔度”从按格计数改为按米计算。通行区、主街、城外道路的 A\* 格数变为 4 倍，城外道路最慢约 0.2 s，`RoadSearch` 暂不需要优化。
2. **区域格局**：`RiverCrossings`、`RegionalRoutes`、`ChooseOrigin`；`Region` 图层与全图截图。过渡期 `ChooseOrigin` 同时写一个 `town.core`（起源点 + 默认半径），让 `StarWall` 及下游继续工作。
3. **一、二期**：`Growth` 工具、`Village`、`MarketPlace`、`ChurchSite`、`MarketGrowth`；`Growth`、`Landmarks` 图层和分期滑条。删除 `SquarePlaza`。
4. **三期与骨架道路**：`EnclosingWall`、`RouteGates`、`WalledInfill`、`SkeletonLinks`；`roads` 契约放开渡口。删除 `ChooseCore` 遗留的 `town.core`、`StarWall`、`PlaceGates`、`MainStreets`、`OuterRoads`。至此“没有桥”的局限解除。
5. **四期与分区**：`Suburbs`、`LandUse`；分区着色与截图。
6. **收尾**：下游改读 `eval.buildable`，删除 `LegacySite` 和 `site`；更新 ARCHITECTURE 的节点表、不变量和已知局限。

## 9. 待定问题

- **城堡 / 领主宅邸**：是否作为二期或三期的一个地标（岗地上、靠墙）？会影响城墙形状。
- **城外道路在地块阶段是否也切临街地块**：四期的街带目前只是分区，地块节点只在城内工作。
- **分区何时被下游使用**：建议在“街区与地块形态”加深时，让地块按分区选用不同的面宽、进深和建筑类型，并把市场、教堂用地从街区里扣除。
- **多座城镇 / 村庄**：区域格局已经有道路和渡口，以后可以在其他交汇点放小村落，但不在本阶段。
