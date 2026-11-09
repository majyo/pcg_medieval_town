# 架构

## 1. 组件

```
scripts/
  Main.cs, MainInterface.cs, Capture.cs
             组合入口：创建各组件并接线（MainInterface 搭界面并把控件绑到地图、节点图、城市视图和大气）；
             --test / --capture / --seed。自身不含生成、渲染、相机或控件逻辑
  terrain/   地形生成    ITerrain（对外的只读查询）、TemperateMapData（生成实现）、GradientNoise、MapSettings、TerrainChecks
  city/      城市生成    core/（CityGraph、CityNode、Key、Region、[Param]）、data/（键的数据类型）、nodes/（各节点）、
                         tools/（共用算法）、Keys、TownGraph、Contracts、Checks
  camera/    相机控制    MapCamera：透视环绕相机，鼠标键盘操作、复位、取景
  rendering/ 渲染        TerrainRenderer（地面、裙边与环境资产的组装）、GroundSurface（Terrain3D 地面）、CityView（城市各键的画法）、
                         FieldStyles（采样场的色带、图例与读数）、MeshKit
    environment/         Atmosphere（天空、太阳、雾、AO、色调映射）、WaterSurface（河面）、Forest + TreeMeshes（程序树木）、
                         GroundCover（GPU 草与野花）、Rocks（岩石）、Exclusion（植被避让区）、ProceduralMesh
shaders/     着色器      ground（地面，Terrain3D 的 shader override）、water、tree、grass、rock；common/painterly.gdshaderinc（共用的光照、噪声、风）
  ui/        界面        SandboxLayout（顶栏、右侧分页面板、底部节点图、状态栏、左下角指北针与图例）、
                         NodeGraph（节点图：点节点只看它的输出）、StagePage（节点卡片与参数）、Inspector（带默认值和 [Param] 反射的参数行）、
                         Toolbar、Legend、MapCompass、Setting（多个控件共用的值）、UiStyle（主题）
```

依赖方向（箭头表示“可以引用”）：

```
terrain   ← city ← rendering
camera      city ← ui
（Main 引用全部；camera 和 terrain 不引用任何其他组件）
```

第三方插件 `addons/terrain_3d`（Terrain3D v1.1.0，C++ GDExtension + 生成的 C# 封装）只由 `rendering/GroundSurface.cs` 使用。

- **城市生成只通过 `ITerrain` 读地形**：高程、采样、最近河段、地图范围。换一种地形实现不需要改城市代码。
- **地形和城市生成不碰场景节点**，可以在无界面检查里直接运行。
- **相机只知道自己的活动范围**，不知道地形、城镇或界面；取景由调用方传入焦点，贴地由调用方传入地面高度回调。
- **界面只发事件、执行别人给的回调**：`ui/` 里的控件不知道地形渲染、城市预览或大气的存在，`MapCompass` 只依赖 Godot 的 `Camera3D`。`NodeGraph`、`StagePage` 是节点图的查看器和编辑器，所以依赖 `city`。“阶段”是界面的组织方式：地形加上节点图的各分组（`Stage.FromGraph`）。
- **画面显示什么只由一个焦点决定**（`GraphFocus`：一个节点、一个阶段或无）。有焦点时，`Main` 取出焦点节点写出的每个键（按节点取版本：`CityGraph.Output`；编辑型节点另取它读到的版本 `Input`，用来标出它新加的部分），能画在地面上的场交给 `FieldStyles`，其余交给 `CityView`；无焦点时按“视图”页的地面着色和图层显示最终结果。图例由同一份显示内容生成。
- **每个键有自己的画法**：场和逐格数据（采样网格、场地、通行区）在 `FieldStyles` 里登记地面着色，其余键在 `CityView` 里登记叠加画法和图例。`CityView.Show` 只画给它的那些值，值不变就不重建。新增一个键时在其中一处登记它的画法，节点图里点到写它的节点就能看到。
- **跨组件的联动只写在 `Main` 里**，例如把城内和道路交给环境资产作为避让区（`Exclusion`）、鼠标探针、按种子重建地图、地面最细 LOD 与地被跟随相机、阴影范围随相机距离变化。
- **地形只有一个高度来源**：`TemperateMapData` 的高度网格（默认 1 m，2001×2001，恰好覆盖 ±1000 m）。`HeightAt` 在它上面双线性插值；地面渲染原样导入它；城市叠加层和树都用 `HeightAt` 贴地。另有约 5 m 的分析网格（坡度、林地、适宜性、离河距离），其高度取自 `HeightAt`，坡度按 5 m 尺度计算，不受米级起伏影响。
- **高度的组成**：区域地貌（大尺度噪声 + 起伏）→ 河谷与河槽 → 米级细节（100 m 到 2.5 m 五个八度，中间两个做域扭曲；在深水河槽内淡出）。噪声是纯 C# 实现（`GradientNoise`），可并行、可复现；高度网格并行生成。
- **最近河段查询**：按 16 m 分桶预先列出每个桶可能的最近河段（桶内任意点的真实最近河段必在列表中），查询只测这几段。生成高度时只关心 260 m 以内的河段。
- **地面由 Terrain3D 绘制**：几何裁剪图（clipmap）网格，顶点着色器按高度图抬高顶点。`GroundSurface` 把高度网格原样导入（每个网格点一个 Terrain3D 顶点）；地图外、Terrain3D 区块多出的部分标成洞。
- **地面着色**（`shaders/ground.gdshader`，顶点阶段和逐像素法线取自 Terrain3D 的最小着色器）：柔和的手绘风格。默认视图下颜色图存的不是颜色而是遮罩：R 林地覆盖、G 高出水面的高度、B 相对高程，由 `TerrainRenderer` 在每个高度网格点上计算。着色器据此算出草甸（两种色调，高处偏干）、林下、陡坡裸土、河滩沙、湿泥的权重，类型之间是几米宽的柔和过渡，边界再由低频噪声和两侧的细节纹理推移。近处有程序生成的地表细节：草地是两种尺寸的草丛加草叶，土和沙是卵石加细颗粒，林下是落叶和苔藓；细节同时改变颜色和法线，并按每个像素覆盖的地面大小淡出，远处不闪烁。光照以包裹漫反射为主，混入少量单阶卡通光，背光处补冷色。分析视图把颜色直接写入颜色图，着色器按 `view_mode = 1` 原样显示：采样场（高程、坡度、坡向……）和场地评价（可建设判定、宜居度、通行代价）按 `FieldStyles` 的登记（连续场用色带，分类场每类一色）通过 `GroundSurface.Paint` 在每个高度网格点上取色。调色板、过渡宽度、细节强度和光照都是着色器的 uniform。
- **环境资产**（`rendering/environment/`，全部程序生成，共用 `shaders/common/painterly.gdshaderinc` 里的柔和光照和全局风参数 `wind_direction`、`wind_strength`，后者登记在 `project.godot` 的 `[shader_globals]`）：
  - 大气：程序天空兼作环境光，指数雾带空气透视，SSAO、AgX 色调映射、轻微泛光；太阳按时刻定位（6 时正东、12 时正南、18 时正西），低角度时偏暖，阴影软边，阴影范围随相机距离收放。全部参数在 `AtmosphereSettings`（太阳、天空、雾、后期、画风与风五组，带 `[Param]`，界面据此生成控件），另有几套命名预设；`Atmosphere.Apply` 一次推送到灯光、天空、环境和着色器全局参数。
  - 画风：包裹漫反射、卡通分阶、暗部补光与色调是全局着色器参数 `style_*`（登记在 `project.godot`），地面和所有环境资产共用。
  - 河面：沿中心线的一条连续带状网格（宽度为河宽的 `WaterSurfaceWidthFactor` 倍，压在岸坡下）；着色器按深度缓冲算出的水体厚度决定颜色和透明度，浅水泡沫，顺流波纹，掠射角偏天空色。
  - 树木：阔叶、桦类、针叶三类，每类 4 个变体，树冠由法线"球化"的团块组成；按地形数据的 `TreePlacement` 摆放；随风摆动。
  - 地被：草丛和野花在 GPU 上摆放：固定格子的实例跟随相机整格移动，顶点着色器按格子抖动位置，读高度、地面遮罩和避让贴图决定是否生长、是否为芦苇；相机离焦点 320 m 以外隐藏。
  - 岩石：陡坡和河岸为主，按地图种子确定位置。
  - 城内、墙外的建成格和道路由 `Main` 构造的 `Exclusion` 交给渲染层：树木和岩石逐点判断，地被用栅格化的避让贴图。

`tools/project.ps1 test` 在编译前检查这些规则：每个目录的命名空间、允许引用的组件，以及 `terrain`、`city` 不出现场景节点类型。违规即失败。

## 2. 节点图

城市生成是一张由节点组成的图。目标是**每个决策都能单独替换**：换一种城墙算法、街巷网算法，只改一个节点，不动其他节点、渲染和检查。

- **键（`Key<T>`）是契约。** 节点之间只通过命名的键传数据（`Keys.cs` 列出全部键），从不互相引用。键的数据类型（`data/`）加上 `Contracts` 里登记的不变量，就是这份契约：任何写这个键的节点都要满足同样的规则。`CityView`、`Checks`、`Main` 都按键读取，不关心是哪个节点写的。
- **节点（`CityNode<TOptions>`）只做一个决策，实际工作在它自己的 C# 代码里完成。** 节点声明读哪些键（`Reads`）、新写哪些键（`Writes`）、编辑哪些键（`Edits`：读取上游版本，换成新版本）。`Run` 是纯函数：同样的地形、输入和选项必然得到同样的输出。
- **编排写在 `TownGraph.Create` 里**：节点列表的顺序就是运行顺序，连线由读写声明自动推导。节点读取的键来自它前面最近一个写或编辑该键的节点，所以节点可以读前面任意节点的数据，不限于上一个。构建图时检查：读了前面没人写的键、两个节点都“新写”同一个键、两个不同的键同名，都直接报错。
- **共用算法放在 `tools/`**（城内网格 A\* `RoadSearch`、全图道路寻路与拉直 `RouteSearch`、沿路生长 `Growth`、格集合的闭运算与填洞 `Morphology`、到格集合或墙线的距离 `Proximity`、窗口均值 `SummedArea`、折线简化、距离、哈希、等值线 `Contours`），`Region` 是唯一的平面多边形类型。节点里只写它特有的逻辑；新写一个变体时直接复用这些工具。
- **存下来的值不可修改。** 编辑型节点先复制上游值（如 `RoadLinks.Copy()`），改副本再写入。检查会核对每个节点的输出在写入后没被别人改过。
- **失效按依赖传播。** 修改某个节点的参数，只有它和直接、间接读它输出的节点变为“已过期”；换地形时全部过期。运行时按列表顺序重跑所有不是“有效”的节点；输入不全有效的节点标为“等待上游”，不相关的节点照常运行。失败或取消的节点不发布结果。
- 每次运行先复制一份选项，界面可以随时修改正在使用的选项对象。节点在后台线程运行。
- 节点的摘要由它写入的值（实现 `ISummarized`）拼成，提示由 `context.Notice` 给出。
- 界面上出现的每个名词都带说明：节点的 `Description`（一句话说明它做什么决定，显示在卡片上，也是节点图的悬停提示）、`[Param]` 的 `Hint`（参数控制什么、调大调小会怎样，悬停显示）、键的说明（`Key` 构造时给出，悬停在卡片的“读 / 写”上显示）。检查要求三者都不为空。

新增参数：在选项类里加一个带 `[Param]` 的属性并写上 `Hint`，面板控件（含恢复默认）、过期处理和复制都会自动生效。`[Param]` 也用于渲染层的设置对象（如 `AtmosphereSettings`），支持 bool、整数、浮点和颜色。
替换算法：写一个读写同样键的新节点（写上 `Description`），在 `TownGraph.Create` 里换掉那一行。
新增数据：在 `Keys` 里加键（带说明）、在 `data/` 里加类型，在 `Contracts` 里登记它的不变量。

## 3. 节点

“场地采样”分组只输出带单位的测量值（不含评分和阈值判定），目前不被城镇节点读取；计划与进度见 [SITE_SAMPLING.md](SITE_SAMPLING.md)。

| 分组 | 节点 | 读 → 写 | 内容 |
| --- | --- | --- | --- |
| 场地采样 | `SampleGrid` 采样网格 | 地形 → site.grid | 所有采样场共用的网格（默认 4 m），以原点为中心、覆盖全图 |
| | `SampleHeight` 高程 | site.grid → site.height | 每格 k×k 个子样本的平均高程，滤掉米级起伏 |
| | `SlopeAspect` 坡度坡向 | site.height → site.slope、site.aspect | Horn 3×3 差分；坡向为下坡方向的方位角（北为 −Z，顺时针），平地无坡向 |
| | `SurfaceWater` 地表水 | site.height → site.shore、site.above_water | 离水距离：到最近河段河道边（半宽处）的距离，水里为负；高出水面：采样高程减最近河段水位。不做径流和汇水 |
| | `LandformPosition` 地形位置 | site.height → site.relief | 高程减去周围边长 2R 方窗的平均高程（积分图）；正为岗地、台地边缘，负为谷地 |
| | `SampleForest` 林地 | site.grid → site.forest | 格心的林地覆盖 0–1，取自地形 |
| 场地评价 | `BuildableLand` 可建设判定 | site.slope、site.shore、site.above_water、site.forest → eval.buildable | 分类场：每格可建设或第一个不可建设的原因，依次为水域（离水 < 0）、易淹（高出水面 < 2.5 m）、河岸退让、坡度过大、森林 |
| | `Habitability` 宜居度 | eval.buildable 与各采样场 → eval.habitability | 0–1，不可建设格为 0。几项因子相乘：坡度平缓、离水在取水距离内、高出水面在安全高度以上、地势略高于周围、不背阴、林地少 |
| | `TravelCost` 通行代价 | site.slope、site.shore、site.forest → eval.travel_cost | 每走 1 m 相当于平地几米：随坡度和林地上升；水面和超过 20° 的陡崖不可通行（无值）。与方向无关 |
| | `LegacySite` 场地汇总（过渡） | eval.buildable、site.height、site.slope、eval.habitability → site | 把评价打包成下游仍在读的 `Site`（采样网格上，适宜性即宜居度）；下游改读评价键后删除 |
| 区域格局 | `RiverCrossings` 渡口 | site.shore、site.above_water、site.slope、eval.travel_cost → region.crossings | 在河道中线上每 20 m 取候选，按最短横断面过河；河窄、两岸高出水面 1–6 m、两岸引道平缓的得分高；取相距 300 m 以上的最好 3 处。桥头是两岸第一个可通行的格 |
| | `RegionalRoutes` 过境道路 | eval.travel_cost、site.height、site.shore、region.crossings → region.routes | 图边选 4 个入口（朝内好走、相距 600 m 以上、河两岸至少各一个、南北边靠近河谷的优先）；相距最远且能连通的两个入口之间修干线，其余入口按接入代价从低到高接到已有路网上，形成 T 形路口。寻路按通行代价计价，河面只能走渡口，坡度超过 6% 的部分按超出量的平方加价（与方向有关，路会斜着爬坡）；结果再拉直：直线按真实长度计价不贵于网格路径就用直线 |
| | `ChooseOrigin` 起源点 | region.routes、region.crossings、eval.habitability、eval.buildable → town.origin | 道路上离图边 320 m 以上的可建设格，得分 = 周围 300 m 方窗的平均宜居度 + 桥头（220 m 内有使用中的渡口）/ 路口（60 m 内）加分 − 离图心的小惩罚 |
| 城镇生长 | `Village` 一期 村落 | town.origin、region.routes、eval.habitability、eval.buildable → town.growth | 从起源点沿过境道路长出 2 ha（`tools/Growth`）：每格代价 = 沿路距离 + 3 × 离路距离（超过 30 m 进深后每米再加 12）− 80 m × 宜居度，从道路和已建成区向外按代价从低到高收入，只走可建设格 |
| | `MarketPlace` 市场 | town.origin、region.routes、town.growth、eval.buildable、site.slope → town.plaza | 村落里有路口时，市场是几条路各取一点围成的三角形（2000 m²）；否则在离起源点 150 m 内的村落道路上，把道路加宽成长宽比 3 的纺锤形，取近、平、不用缩小的一处。放不下时按 0.9 倍逐步缩小 |
| | `ChurchSite` 教堂 | town.plaza、town.growth、region.routes、site.height、site.relief、eval.buildable → town.landmarks | 市场 160 m 内、紧挨村落的 44 × 28 m 东西向整格用地，全在可建设格上，离过境道路中线 10 m、离市场 6 m 以上；取比市场高、地形位置高、离市场近的一处。教堂祭坛朝东，钟塔在西端 |
| | `MarketGrowth` 二期 市镇 | town.plaza、town.landmarks、region.routes、eval.habitability、eval.buildable；编辑 town.growth | 市场和教堂用地先算作二期建成，再以市场下的道路格为起点同样生长，到累计 8 ha |
| | `EnclosingWall` 城墙 | town.growth、town.plaza、town.landmarks、region.routes、site.shore、site.slope → town.wall | 从市场和教堂用地向外，在离水 20 m 以外、坡度 15° 以内的格上按“离市场的路程 − 100 m × 是否已建成”从低到高收入，到筑墙前建成面积的 1.25 倍；收进墙外 40 m 内的路口和出墙不到 120 m 又回来的路段；闭运算（20 m）填凹口、填洞，取外轮廓，切角平滑、简化成墙线。平滑后再按墙线检查道路，仍有短距离出入的路段就再收进来重做，最多三遍。长臂的末端可以留在墙外 |
| | `RouteGates` 城门 | region.routes、site.shore；编辑 town.wall → town.gates | 过境道路每穿过墙线一次开一座门：门点是穿墙处城外一侧的道路格格心（在水里时取城内一侧），插进墙线（因此编辑城墙）；城门通向沿道路往墙外走最先到达的图边入口 |
| | `WalledInfill` 三期 城内填充 | town.wall、town.gates、town.plaza、region.routes、eval.habitability、eval.buildable；编辑 town.growth | 只在城内、离墙 8 m 以外，从市场和城门下的道路格同样生长，默认填满城内可建设的地（菜园在各家地块后院）；调小填充比例可以留出成片菜园 |
| | `Suburbs` 四期 城外街区 | town.wall、town.gates、town.plaza、region.routes、region.crossings、eval.habitability、eval.buildable；编辑 town.growth | 新建面积 = 城内建成面积 × 0.35。先在城内、离墙 8 m 以外填剩下的可建设地（三期默认填满，所以通常没有）；余下的从城门下的道路格沿过境道路往城外长，离墙 15 m 以外，墙外已建成的长臂自动接上。长到城外时，离城最近、对岸桥头离墙不超过 400 m 的使用中渡口，在对岸桥头 200 m 内另长 0.5 ha（那里已有建成就不长） |
| 用地分区 | `TownReach` 离城路程 | eval.travel_cost、site.height、town.wall、town.gates、region.routes、region.crossings → land.reach | 从各城门出发的全图 Dijkstra，按过境道路的价格（通行代价每米加超坡惩罚，`RouteCosts`），只有使用中的渡口能过河；城内为 0，不能穿城墙抄近路；走不到的格无值 |
| | `TownQuarters` 城镇分区 | site.shore、town.growth、town.wall、town.plaza、town.landmarks、region.routes → land.use | 水域；城内建成格分公共用地（市场、教堂用地）、市场核心（离它们 60 m 内）、居住；墙外建成格是城外街区；没建成、墙内 8 m 或墙外 15 m 以内是城墙空地；城内其余是城内园地；城外其余过境道路格是城外道路；剩下的“未分配” |
| | `Workshops` 作坊 | land.use、land.reach、site.shore、site.above_water、site.height、site.slope、eval.buildable、town.wall、town.growth、region.routes、region.crossings；编辑 land.use | 都在未分配的格上，从离城路程最近、连成一片够大的候选格开始按路程往外收：湿作坊 1.5 ha，离水 60 m 内、水位比城低 0.5 m 以上（城的水位取离城墙最近的水格）、离墙 50–400 m、坡度 8° 内，可在河岸退让带上；火作坊每片 1 ha、最多两片、相距 200 m 以上，离任何建成格 80 m 以上、离墙 600 m 内，起点在过境道路 60 m 内；水磨两处各 0.1 ha，离水 12 m 内、离墙 100–500 m、离使用中的渡口 80 m 以上、相距 150 m 以上 |
| | `Countryside` 农业带 | land.use、land.reach、site.slope、site.aspect、site.shore、site.above_water、eval.buildable、eval.travel_cost；编辑 land.use | 剩下的格依次：森林格是林地；易淹地和离水 30 m 内、高出水面不到 2 m 的低地是草场；离城外街区或城墙空地 120 m 内的可建设格是菜园果园；朝南（135°–225°）、坡度 4°–15°、离城路程 1.2 km 内是葡萄园；坡度 6° 内、离城路程 1.2 km 内是耕地；其余可通行的是牧场；剩下的是荒地 |
| 路网 | `RoadZoning` 通行区 | site、town.wall、town.gates → road.zones | 哪些格可以修路：不是水、坡度不超限，城内或城外，只能在城门处穿墙 |
| | `SkeletonLinks` 骨架道路 | region.routes、town.wall、town.gates、town.plaza → road.links | 过境道路的格子连线原样抄入：两端都在城内的是主街，其余是城外道路，一直到图边，过河处走渡口；锚点是市场（市场里离中心最近的道路格）、城门、图边入口 |
| | `StreetMesh` 街巷网 | road.zones；编辑 road.links | 城内按间距撒点，最近点对连成树，再加少量不形成锐角的环 |
| | `ExtractRoads` 提取路网 | site、road.links、town.plaza → roads | 网格连线中度为 2 的链变成折线，锚点、路口、端点和道路等级变化处成为节点；道路加广场成为铺装区。不需要线段求交。街巷按 0.4 格简化；主街和城外道路抄自拉直的过境道路，按 0.75 格简化（否则格子台阶留下小折，铺装会切掉地块的角），碰到水的段仍按 0.4 格 |
| 街区与地块 | `StreetBlocks` 街区 | site、town.wall、roads、town.landmarks、town.growth → blocks | 可建区 = 建成格（格边圆滑 4 m），城墙内侧退让 8 m、外侧退让 15 m，− 铺装区（外扩 5 cm，免得沿路几公里的边界留下舍入细缝）− 地标用地 − 不可建设格，按连通部分分成街区；没建成的地（陡坡、菜园）不建房 |
| | `FrontageParcels` 临街地块 | blocks、roads → parcels | 沿每条街和城外道路两侧按面宽切出 16 m 深的地块，先主街、再城外道路、后街巷，后切的扣除已占用部分；然后所有地块每轮各向后取一条 2 m 的条带，两边街道的地块在街区中间相遇，最深 50 m；街角、弯处剩下的空隙（不超过 1500 m²）并给相邻边界最长的地块 |
| 建筑 | `GableHouses` 临街双坡房屋 | parcels → buildings | 每个地块临街一栋矩形房屋，层数随机，主街加一层；双坡屋顶，窄长房屋山墙朝街；偶尔留出小巷 |

`road.links` 是累加型数据：骨架道路新建，街巷网在副本上添加；A\* 偏好已有连线，所以街巷会并入主街。`town.growth` 也是：每格记下建成于第几期，后一期在副本上加格，所以点“一期 村落”只看到一期，点“二期 市镇”看到一、二期两种颜色。

## 4. 不变量

`tools/project.ps1 test` 对种子 73129、7、22017 跑完整张图，检查：

- 地形（`TerrainChecks`）：高度网格以原点为中心、恰好覆盖地图、间距不大于设定值；没有非有限值；相邻点坡度不超过 45°；`HeightAt` 在网格点上复现网格值，分析网格的高度取自 `HeightAt`；分桶最近河段查询（含限距查询）与逐段查询结果一致；所有水域格都在渲染的河面范围内；同一种子两次生成的高度网格逐位相同。
- 各键的契约（`Contracts`）：
  - site.grid：覆盖整张地图，超出不到一格，以原点为中心。
  - 所有采样场：使用共用的网格实例，每格一个有限值，统计的最小/最大值与数据一致。site.height 与格心地面高度相差不到 1 m；site.slope 在 [0, 45°]；site.aspect 在 [0, 360) 或为无值。site.shore 为负的格都是地形水面，地形水面格离河道边不到 8 m（真实水线在河道边外几米，岸坡在那里升出水面）。site.above_water 在离水 2 m 以内的水面格不高于水位 0.5 m 以上，离水 30 m 以外不低于水位。site.relief 全图平均接近 0，河道深处（离水 −10 m 以内）至少九成格低于周围。site.forest 在 [0, 1]，水面格为 0。
  - eval.buildable：使用共用网格，类别与原因一一对应，可建设比例在合理范围内；“水域”恰好是离水距离为负的格；易淹格的高出水面都低于其余陆地格，“坡度过大”格的坡度都大于可建设格（任何阈值下都成立，不是阈值判定就会失败）。eval.habitability 在 [0, 1]，不可建设格为 0，最高值大于 0.5。eval.travel_cost 有值的格不小于 1，水面格不可通行。
- 等高线：采样高程按 2 m 间距提取的每个端点，在该处取高程都等于它的高度（误差 1 cm 内）。
  - site：使用共用的采样网格，可建设比例在合理范围内。
  - region.crossings：至少一处；格子从一个桥头逐格（只走上下左右）到另一个桥头；中点在水面上，桥头是可通行的陆地。region.routes：入口离图边不超过 10 m；每条道路从入口出发，到另一个入口或接到更早的道路上，格子 8 邻域相邻，每格可通行或属于它经过的渡口；全部入口连通。town.origin：在可建设格上，在过境道路上。
  - town.growth：使用共用网格；期数从 1 起，每期格数与记录一致且不为 0；建成格都可建设；每一期结束时，由上下左右相邻的建成格连成的每一片都接到过境道路上（含相邻）。四期的格不贴城墙（离墙线一格以上）；四期长到城外时，城内离墙 30 m 以外不再有挨着建成区的可建设空地（先城内、后城外）。
  - town.plaza：覆盖的格都可建设；中心两格以内有一期建成格；在城墙内。town.landmarks：用地在网格内，与它的格一致，全在可建设格上，不压过境道路格，不和市场重叠，在所属那一期结束时已全部建成。
  - town.wall：墙线不自相交，不进水域，包住地标用地，围住筑墙前建成区的一半以上。town.growth 另加：三期城内填充的格都在城内。
  - town.gates：至少一座，都在城墙上、在过境道路格上，去向是图边入口；过境道路每一处穿墙 1.5 格内都有城门。
  - roads：引用有效，没有零长度路段，全部连通，城门全部接入；只在使用中的渡口处进入水域（离桥头连线一格以内）；城内道路只在城门处穿过城墙。
  - blocks：可建区不压城墙（墙线两侧各 2 m），不超出建成格一格以上（菜园不建房），不压铺装区。
  - parcels：在可建区内，不压道路，互不重叠。
  - buildings：在各自地块内，互不重叠，高度有效。
  - land.reach：城内和城门格为 0，没有负值，建成格都走得到。
  - land.use：使用共用网格；没有“未分配”；水域恰好是离水 < 0 的格；建成格恰好是公共用地、市场核心、居住、城外街区四类，城内类别只在城内、城外类别只在城外；城墙空地离墙线不超过 60 m 加一格（参数上限）；湿作坊离水 60 m 内、水位比城低；火作坊不挨着建成格；水磨离水 12 m 内；林地都是森林格，城外的森林格只要归农业带就是林地；草场、菜园果园、葡萄园、耕地、牧场、荒地都满足各自的条件。
- 说明：每个节点有 `Description`，每个节点参数有 `Hint`，`Keys` 里每个键有说明。
- 节点图：读了没人写的键、重复新写同一个键时构建报错；没有节点改动过它读到的值；让“骨架道路”过期后，恰好它和它的下游变为过期（场地、区域、城镇生长、用地分区和通行区保持有效）；让“作坊”过期后，只有作坊和农业带过期；让“教堂”过期后，一期村落和市场保持有效，二期、城墙及以后全部过期；让“城墙”过期后，一、二期和市场、教堂保持有效；让“宜居度”过期后，采样场、其余评价节点、渡口和过境道路保持有效，起源点和整座城镇过期；让“高程”过期后，只有“采样网格”和“林地”保持有效；三种情况重跑后结果都与完整生成相同。
- 同一种子生成两次，结果相同。

## 5. 加功能的规则

1. **先广后深。** 整张图在三个种子下都“看起来对”，才加深某一个节点。
2. **一次只加深或替换一个节点。** 需要改某个键的数据类型时，先改类型和它的契约并跑检查，再继续。
3. **节点的粒度**：一个节点对应一个可能被单独替换或调参的决策，它写出的值能单独显示、单独检查。一个节点里出现两组互不相关的参数，就该拆开；反复使用的算法放进 `tools/`。
4. **每个新功能附带一条不变量**，城市写进 `Contracts`（挂在它约束的键上），地形写进 `TerrainChecks.Invariants`，不另建验证脚本或命令行开关。
5. **新代码放进它所属的组件**；需要跨组件联动时写在 `Main`，不要让组件互相引用。新增组件时在 `tools/project.ps1` 里登记它允许的依赖。
6. **展示细节放到最后。** 门窗、材质、植被、LOD 等，等生成结果稳定后再做。
7. **代码格式由 `.editorconfig` 约束：** 一行一条语句，单行不超过 140 字符，提交前执行 `dotnet format whitespace`。

## 6. 已知局限与后续方向

- 桥只是渡口处的一块桥面板（成果画面的“道路”图层），没有桥墩和引桥；道路在河面上的部分贴着河床，被桥面遮住。
- 道路贴着原地形，没有纵断面或地形修整。如果以后要做，用“叠加高度场”实现，不复制地形数据。
- 地块只有临街一种类型，街区深处留作后院；公共建筑只有教堂（街区避开它的用地），没有市政厅和城外街带。
- 城墙、城门、路网、街区节点仍读过渡用的 `site` 键（由 `LegacySite` 从评价键打包），整座城镇已在 4 m 采样网格上生成；改读评价键是城市规划阶段的最后一步（见 [CITY_PLANNING.md](CITY_PLANNING.md) §8）。
- 地图生成约 0.6 秒，场地采样约 0.15–0.3 秒，场地评价约 0.1 秒，区域格局约 0.2–0.4 秒（大部分是全图寻路），城镇生长约 0.3–0.5 秒，其余节点合计约 0.5 秒（Debug 编译）。
- 用地分区只用于检视、着色和探针：作坊格上没有作坊建筑，耕地、葡萄园、草场在成果画面上仍是普通地表，地块和房屋也不按用途区分（[CITY_PLANNING.md](CITY_PLANNING.md) §9）。
- 牧场占全图四到五成：坡度超过 6° 的地都算不上耕地，而这张图起伏较多。水磨放在约 40 m 宽的大河岸边，相当于船磨或短引水渠。
- 城外街区只沿过境道路切临街地块，没有自己的街巷，长臂只有一两排房子深。
- Terrain3D 没有纹理资源时会在着色器末尾追加棋盘格调试代码，所以 `GroundSurface` 注册了一个不会被采样的占位纹理。
- 远离焦点的粗 LOD 网格碰到地图边缘的洞时整块三角形会被丢弃，地图近侧边缘与裙边之间可能露出一条细缝。
- 城市部分（房屋、道路）仍是普通的顶点色材质，风格还没有和地面、植被统一。
- 环境资产只做到基本效果：树木没有 LOD 和叶片卡；地被只覆盖相机附近约 50 m；岩石形状简单；天空没有云；全图视角的雾偏重。
- 河面是逐段的四边形，段与段之间有楔形缝隙，近看明显；属于河流/水面的工作，尚未处理。
- 道路、广场直接贴着带起伏的地面，没有整平。

建议的加深顺序：场地判定与选址改读采样场 → 路网形态 → 街区与地块形态 → 公共建筑与建筑类型 → 城外街带与桥 → 地形修整 → 视觉细节。

其中“场地判定与选址”以及城墙、城门、骨架道路、城外街带、渡口的重做合并为城市规划阶段（场地评价 → 区域格局 → 城镇生长，按历史分期生长），方案见 [CITY_PLANNING.md](CITY_PLANNING.md)。

界面和可视化按键重组（键的画法登记表、检视 / 成果两种模式、节点聚焦）的方案见 [VISUALIZATION.md](VISUALIZATION.md)。
