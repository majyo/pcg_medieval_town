# 场地采样 · 第一阶段方案

> 状态：第 1 步（骨架）已完成，第 2–5 步未开始。基于节点图架构（[ARCHITECTURE.md](ARCHITECTURE.md) §2–5）。

## 1. 目标与范围

第一阶段只做两件事：

1. **采样**：把地形、水文等与建城有关的信息，采成节点图里一组独立的数据键（下称“场”）。
2. **可视化**：每个场都能在地面上着色显示、在鼠标探针里读数、在右侧面板看到摘要。

判断一件事属不属于第一阶段的标准：**输出是不是一个带单位的物理量或可直接测量的几何**（高程 m、坡度 °、汇流面积 m²、高出水面 m、河宽 m）。凡是带权重、偏好或“好不好”的东西（适宜性评分、城址打分、渡口评价、可建设判定的阈值）都留给第二阶段。

明确不做：

- 不改 `site` 键、`SiteAnalysis` 和它下游的任何节点。三个测试种子生成的城镇必须逐位不变。
- 不改地形生成（`TemperateMapData`）的算法，只给 `ITerrain` 增加一个只读查询（见 §3.3）。
- 不做评分、选址、渡口/桥位评价、道路相关的东西。

## 2. 现状与问题

- `SiteAnalysis` 一个节点同时做了“采样”和“判定”：在 8 m 网格上读 `Sample()`，再按阈值写出 `SiteBlock`。采样结果（高程、坡度、适宜性）存在 `Site` 里，但只有 `Block` 能被看到（`CityLayer.Site` 的色块）。
- 坡度、林地、适宜性来自地形内部约 5 m 的分析网格，城市侧拿不到自己网格上的坡度算法，换不掉。
- **没有水文**：地形里只有一条手写的河（中心线 + 水位 + 半宽），城市只能问“最近河段多远”。地表径流往哪流、哪里汇水、哪里会被淹、洼地在哪，都不知道。
- 左侧“地表着色”的高程/坡度/适宜性直接读 `TemperateMapData` 的数组，和城市用的数据不是同一份。

## 3. 节点与数据

### 3.1 新增分组“场地采样”

全部节点排在 `TownGraph.Create` 最前面（`SiteAnalysis` 之前），只读地形和彼此的键，不被现有节点读取。

| 节点 | 读 → 写 | 内容 | 主要参数 |
| --- | --- | --- | --- |
| `SampleGrid` 采样网格 | 地形 → `site.grid` | 覆盖全图的规则网格定义 | 格距 m（默认 4，范围 2–16） |
| `SampleHeight` 高程 | grid → `site.height` | 格心高程。每格取 k×k 个子样本的平均，滤掉米级起伏，避免网格混叠 | 子样本数（1–4，默认 3） |
| `SlopeAspect` 坡度坡向 | height → `site.slope`、`site.aspect` | Horn 3×3 差分。坡向 0–360°，平地记为无坡向 | 平地阈值 ° |
| `LandformPosition` 地形位置 | height → `site.relief` | 地形位置指数 TPI：高程减去半径 R 内的平均高程（m）。正为岗地/台地边缘，负为谷地 | 半径 R m（默认 60） |
| `SurfaceWater` 地表水 | grid、地形 → `site.water` | 每格：是否水面、到水边的有符号距离（水内为负）、所属河段的水位、高出该水位的高度 | 无 |
| `RiverProfile` 河流断面 | 地形、height → `site.river` | 沿河中心线每隔 Δs 一个断面：里程、流向、水位、河宽，左右两岸各自在 d 米处的岸高与岸坡、河漫滩宽度（离水 X m 内高出水面不足 h 的宽度） | Δs（默认 40 m）、漫滩高度 h（默认 2 m） |
| `DepressionFill` 洼地填平 | height → `site.filled` | Priority-Flood 填洼（带极小坡度，保证每格都有出路）。同时得到积水深度 = 填平高程 − 原高程 | ε 坡度 |
| `FlowRouting` 汇流 | filled → `site.flow` | D8 流向 + 汇流面积（m²，上游所有格面积之和） | 无 |
| `StreamNetwork` 水系 | flow、water → `site.streams` | 汇流面积超过阈值的格连成折线；Strahler 级别；河道格并入主干 | 成河阈值（默认 2 ha） |
| `FloodHeight` 洪水高度 | height、flow、streams、water → `site.flood` | HAND：沿流向走到第一个水系/水面格，记高差（m）。这是“这块地比它排水去的水面高多少” | 无 |
| `SampleForest` 林地 | grid、地形 → `site.forest` | 林地覆盖 0–1（取自 `Sample().Forest`） | 无 |

拆分依据（ARCHITECTURE §5.3）：每个节点对应一个可能单独替换的决策——填洼可换成开挖（breaching），D8 可换成多流向（MFD），TPI 可换成多尺度地貌分类，`SurfaceWater` 将来可换成读湖泊/多条河的实现——而其余节点不动。

### 3.2 数据类型（`city/data/Fields.cs`）

```csharp
// 唯一的网格定义。所有场共用同一个 SiteGrid 实例，因此格号一致、可直接逐格组合。
public sealed class SiteGrid { Vector2 Origin; float CellSize; int Nx, Nz; int Count;
    Vector2 Center(int k); int CellAt(Vector2 p); bool InGrid(int x, int z); /* 8 邻域 */ }

// 标量场：一个数组 + 单位 + 实际取值范围。可视化、探针、契约检查都只认这个类型。
public sealed class ScalarField : ISummarized { SiteGrid Grid; float[] Values; string Unit;
    float Min, Max; float Sample(Vector2 p) /* 双线性 */; }

public sealed class WaterField   { SiteGrid Grid; bool[] Wet; float[] EdgeDistance; float[] WaterLevel; float[] AboveWater; }
public sealed class FilledHeight { ScalarField Filled; ScalarField PondDepth; }
public sealed class FlowField    { SiteGrid Grid; int[] Downstream /* -1 = 出图或水面 */; float[] Area; }
public sealed class StreamSet    { List<Stream> Streams; /* 折线、Strahler 级别、末端汇流面积 */ ScalarField Distance; }
public sealed class RiverProfile { List<RiverStation> Stations; }
```

`site.slope`、`site.aspect`、`site.relief`、`site.forest`、`site.flood` 直接是 `ScalarField`；复合类型对外再暴露各自的 `ScalarField` 视图（如 `WaterField.AboveWaterField`），让可视化只处理一种类型。

`SiteGrid` 从 `Site` 里抽出来；第一阶段 `Site` 可以改为内部持有 `SiteGrid`，但公开成员和行为不变。

### 3.3 `ITerrain` 的唯一改动

`RiverProfile` 需要沿河走，`NearestRiver` 只给距离不够。增加一个只读属性：

```csharp
/// <summary>河中心线，自上游到下游：位置、水位、半宽。</summary>
IReadOnlyList<RiverPoint> RiverCenterline { get; }
```

`RiverPoint` 已经在 `terrain` 里定义，不新增类型。地形依旧是水面位置的唯一来源；`SurfaceWater` 用 `NearestRiver`，`RiverProfile` 用中心线。

### 3.4 规模与耗时预算

默认 4 m 格：500 × 500 = 25 万格。目标是场地采样分组合计 < 0.5 s（Debug），其中填洼是 O(n log n)。逐格循环用 `Parallel.For`（填洼、汇流累加除外），并按现有做法每 4096 格检查一次取消。

## 4. 可视化

### 4.1 地面着色（主视图）

左侧“地表着色”下拉框改为：**地表** + 每个已登记的场。选中一个场时，把它的颜色写进 Terrain3D 颜色图（着色器 `view_mode = 1`），颜色贴在真实地面上，透视视角下也能读出地形。

- `GroundSurface` 增加 `SetColors(Func<Vector2, Color>)`（按世界坐标取色，替换现在按分析网格顶点取色的版本）；`TerrainRenderer` 只转发，不知道“场”是什么。
- 新增 `rendering/FieldStyles.cs`：一张登记表，每项是 `键 → 名称、取值函数、色带、范围、图例刻度`。下拉框、图例、探针都从这张表生成，**新增一个场只需加一行登记**。
- 色带：顺序型（高程、坡度、汇流面积取对数、林地）、发散型（TPI、有符号离水距离，以 0 为中点）、分级型（洪水高度：< 1.5 m / 1.5–4 m / > 4 m 三档，只作显示分级，不进数据）、环形（坡向）。
- 场过期或缺失时，该项在下拉框里灰掉，地面退回“地表”。场的值换了（引用变化）才重绘颜色图，沿用 `CityView.Update` 的判断方式。
- 现有的“高程 / 坡度”两项由 `site.height`、`site.slope` 取代，避免两份数据；“适宜性”暂时保留（属于第二阶段要替换的评分）。

### 4.2 叠加图层（新 `CityLayer` 分组“采样”）

在 `CityView` 里新增，各自有开关：

| 图层 | 键 | 画法 |
| --- | --- | --- |
| 等高线 | `site.height` | 行进方块法提等值线，2 m 细线、10 m 粗线，贴地抬高 0.3 m |
| 水系 | `site.streams` | 贴地折线，线宽随 log(汇流面积)，颜色按 Strahler 级别 |
| 流向 | `site.flow` | 每隔 N 格一个小箭头（默认关闭，调试用） |
| 河流断面 | `site.river` | 每个断面一条横跨河道的短线，两端按岸高着色；河漫滩用浅色带标出 |
| 积水洼地 | `site.filled` | 积水深度 > 0.3 m 的格画半透明蓝色块 |

等值线提取放在 `city/tools/`（将来道路纵断面、地形修整也会用），画线放在 `rendering`。

### 4.3 探针与图例

- 鼠标探针改为遍历 `FieldStyles`，列出光标处所有有效场的读数，例如：
  `(-212, 348) 高程 23.4 m · 坡度 3.1° · TPI +1.8 m · 离水 64 m · 高出水面 4.2 m · 汇流 1.2 ha · HAND 2.1 m · 林地 0.12`
- 下拉框下方显示当前场的图例：色带 + 最小/最大值 + 单位，分级型显示色块和区间。

### 4.4 右侧面板

不需要改 `StagePanel`：新节点自动出现在“场地采样”分组下，摘要来自各值的 `ISummarized`，例如 `500×500 格，4 m`、`坡度中位数 2.4°，>9° 占 6%`、`最大流域 3.6 km²，出口在 (40, 1000)`、`河网 9.8 km，最高 4 级`、`断面 52 个，河宽 41–57 m`。

## 5. 不变量（写进 `Contracts`）

| 键 | 检查 |
| --- | --- |
| `site.grid` | 覆盖整张地图；格距在参数范围内 |
| 所有 `ScalarField` | 长度等于格数；无非有限值；`Min/Max` 与数组一致；共用同一个 `SiteGrid` 实例 |
| `site.height` | 与格心 `HeightAt` 之差 < 1 m（子样本平均只滤米级起伏） |
| `site.slope` / `site.aspect` | 坡度在 [0, 45°]；坡向在 [0, 360) 或标为无坡向 |
| `site.water` | 水面格的 `AboveWater` ≤ 0.5 m；所有水面格都在渲染河面范围内（与 `TerrainChecks` 一致） |
| `site.filled` | 填平高程 ≥ 原高程；每个非出口格都有严格更低（含 ε）的邻格 |
| `site.flow` | 下游格是 8 邻格且不高于本格（按填平高程）；沿下游走必在 `Count` 步内结束（无环）；每格面积 = 自身 + 上游之和；所有出口面积之和 = 全图面积 |
| `site.flow` 与地形一致 | 面积最大的出口距河流出图点不超过一个河宽——D8 网络必须“找到”那条河 |
| `site.streams` | 每条折线的级别不低于汇入它的支流；河道格都在水系内 |
| `site.flood` | ≥ 0；水系和水面格为 0 |
| `site.river` | 断面按里程递增；水位沿程不升；岸高 ≥ 水位 |

另外沿用现有的节点图检查：不改读到的值、过期按依赖传播、两次生成一致；并检查三个种子下 `site` 及其下游的输出与改动前逐位相同。

## 6. 实施顺序

每一步结束时三个种子都能跑完、检查全过、截图可看。

1. **骨架**：`SiteGrid`、`ScalarField`、`SampleGrid`、`SampleHeight`、`SlopeAspect`；`FieldStyles`、按世界坐标着色、图例、探针。完成后“高程/坡度”视图换成新数据。
2. **地表水**：`ITerrain.RiverCenterline`、`SurfaceWater`、`RiverProfile`；河流断面图层。
3. **水文**：`DepressionFill`、`FlowRouting`、`StreamNetwork`、`FloodHeight`；水系、洼地、流向图层。这一步风险最高（河道是手工刻出来的，D8 是否沿河走要靠 §5 的一致性检查确认）。
4. **地貌与林地**：`LandformPosition`、`SampleForest`；等高线图层。
5. **收尾**：`capture` 增加两个分析视角（汇流 + 水系、洪水高度 + 等高线）；更新 ARCHITECTURE §1、§3、§4 和 README 的操作说明。

## 7. 留给第二阶段

- 用这些场重写 `site` 的判定（`SiteAnalysis` → 读场的 `BuildableLand`），把 `site` 改名为 `site.buildable`，`SiteBlock` 增加“易淹”“洼地”。
- 适宜性评分与城址选择改为读场：台地（HAND 2–6 m、TPI > 0）、近水不淹、渡口附近。
- 渡口/桥位评价：读 `site.river` 断面，找窄、浅、两岸缓且不淹的位置。
- 多尺度地貌分类、多流向汇流、湖泊与多条河（换 `SurfaceWater` 和地形实现即可）。
