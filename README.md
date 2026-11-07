# PCG City Sandbox

基于 Godot 4.7.2 .NET、C# 和 .NET 8 的中世纪城市宏观构建实验项目。

当前主场景为 **2 × 2 公里的温带平原测试地图**，包含长程缓坡、曲流河道和简模森林，支持固定种子重建、自由观察与环境诊断图层。

已接入城镇 PCG 节点 **环境采样**：读取已有地图，独立运行、取消、调整采样范围和精度，并预览采样数据及建设限制。基础地图生成仍在城镇管线外。

城镇采用**一次性节点生成工作流**：环境采样 → 聚落布局 → 道路生成（路网规划 + 几何定型）→ 03B 道路竖向与地形修整 → 街区与地块 → 建筑生成 → 场景布置。每个节点根据固定输入、参数和种子生成完整结果；调参后重跑当前节点，再按需更新下游。

## 设计方案

- [城市规划展示与简模细节升级方案](docs/CITY_VISUAL_UPGRADE.md)：V1–V3 已实现统一查看工具栏、贴地道路/院地、建筑门窗屋檐和立体示意城防；[实现与验收](docs/CITY_VISUAL_V1_V3.md)包含操作、对比截图和验证。V4–V5 留待后续。
- [演示场景地形视觉升级方案](docs/TERRAIN_VISUAL_UPGRADE.md)：A、B 阶段已实现六类自然材质、湿岸和沿河水流；[实施与验收记录](docs/TERRAIN_VISUAL_AB.md)包含前后截图、性能数据及测试。城市人工地面已随 V2 接入，进一步田园与自然植被细节仍待后续。
- **当前实现**：[城镇区域布局](docs/REGIONAL_TOWN_IMPLEMENTATION.md)。独立设防核心、城门外街带、城外修道院聚落和田园已接入道路与地块；分别统计城内外实际宅地。[总体方案](docs/REGIONAL_TOWN_LAYOUT_REDESIGN.md)中的跨河桥头部分留待显式桥位支持。
- [十三至十五世纪城市形态修订](docs/MEDIEVAL_URBAN_FORM.md)：历史结构参考及已接入的城墙/城门、公共院区、街巷与前街后院规则；属于程序化形态原型，不是具体城市的考古复原。
- [简单节点式 PCG 框架](docs/PCG_FRAMEWORK_DESIGN.md)：一次性生成的节点契约、依赖、结果复用和局部重跑。已落地环境采样的独立运行入口；聚落布局已接入，通用图执行器和图编辑器仍为方案。
- [第一个节点：环境采样](docs/ENVIRONMENT_SAMPLE_NODE.md)：已实现的输入、参数、只读结果、预览、运行命令和验证说明。
- [聚落布局节点](docs/SETTLEMENT_LAYOUT_NODE.md)：已实现规划范围、主核心、对外入口与核心广场，支持独立生成、手动点选和分层预览。
- [道路生成节点](docs/ROAD_GENERATION_NODE.md)：已实现主次路生成、全宽占地与拓扑检查、独立运行和四层预览。
- [道路几何定型](docs/ROAD_GEOMETRY.md)：道路节点内部完成转角平顺、路口展开、路宽过渡和统一占地；地块与路面共用边界及其来源。
- [街区与地块节点](docs/BLOCK_PARCEL_NODE.md)：已实现道路与广场扣除、街区提取、临街地块细分、建筑退界、用途分配和四层预览。
- [建筑体量节点](docs/BUILDING_MASSING_NODE.md)：已实现主屋、共享墙与通院小巷、公共院区灰模、双坡屋顶和基础，提供独立生成及四层预览。
- [场景布置节点](docs/SCENE_PLACEMENT_NODE.md)：已实现地面和建筑装配、院落与公共附属物、带开口的围栏、植被避让、来源追溯及四层预览。
- [道路竖向、地形修整与正式路面](docs/ROAD_CONSTRUCTION_NODE.md)：已接入 03B 设计面与修整地形、04–06 高度链、正式铺装、分块碰撞、道路导航/运行图及城镇导出。操作和验收见该文档第 15 节。
- [城市一次性生成方案](docs/CITY_GENERATION_PLAN.md)：环境采样至场景布置的七个独立节点已实现；通用图编辑器与正式美术资源仍待后续接入。
- [温带平原测试地图](docs/TEMPERATE_MAP.md)：地图参数、操作方法、数据查询接口与验证方式。

## 打开与运行

### Git 与 Git LFS

仓库使用 Git LFS 管理模型、贴图、音视频及 Godot 二进制资源，具体类型见 `.gitattributes`。首次使用前安装 Git LFS 并执行 `git lfs install`；克隆仓库后可执行 `git lfs pull` 获取完整资源。

源码、Godot 文本场景与资源（`.tscn` / `.tres`）、`.uid` 和配置直接纳入 Git。`.godot/`、`bin/`、`obj/`、`.tools/`、`artifacts/` 和 `generated/` 属于缓存、本地环境或生成结果，由 `.gitignore` 排除；城镇导出可通过 `export-town` 重新生成。新增 LFS 类型时，先执行 `git lfs track "*.扩展名"` 并提交更新后的 `.gitattributes`。

### 运行项目

使用 Godot **.NET 版**导入根目录的 `project.godot`，构建项目后按 F6 运行当前场景，或按 F5 运行主场景。

也可以在项目根目录执行：

```powershell
.\tools\project.ps1 run
```

滚轮缩放，WASD 或中键平移，右键旋转，Home 复位。左侧面板可切换图层、显示森林或河面、输入种子重建地图。

地表表现可选择“简洁 / 标准 / 细节”，F9 隐藏或恢复面板。视觉参数在 `configs/terrain_visual.tres`，调整材质不改变城镇 PCG 结果。`test-terrain-visual` 验证视觉数据，`test-terrain-visual-ui` 验证运行时行为，`capture-terrain` 输出 12 张截图，`benchmark-terrain` 额外记录性能。

直接查看完整城市：`.\tools\project.ps1 preview-city`。02–06 节点共用“简模展示 / 规划分析 / 问题检查”工具栏，可隐藏屋顶、切换低墙和收放标注；点击对象查看来源，F 聚焦，滚轮支持单体近景。原节点查看下拉框已由共享工具栏替代，下文旧图层名称对应同类分析主题。`configs/city_visual.tres` 保存城市显示默认值。城市验证：`test-city-visual` / `test-city-visual-ui`；截图与性能：`capture-city` / `benchmark-city`。

右侧“环境采样”面板点击 **独立运行采样**。默认采样间距 10 米；可修改范围、间距和坡度上限。结果支持基础建设状态、高程、坡度、水域、植被与限制原因图层，网格显示和原因筛选。修改参数或重建地图后，旧采样结果明确标为过期，需要重新运行。界面参数本次会话有效，默认参数保存在 `configs/environment_sample.tres`。

右上节点选择器切换到 **02 城镇区域布局**。先运行环境采样，再点击“独立生成布局”；默认规划半径 300 米、广场边长 40 米、最小区域 2 公顷、期望 2 个入口，并启用城墙占位及教堂/领主院区。城墙包含退让、墙体、缓冲带和与外部入口关联的城门；可调整或关闭。可设置种子、手动核心及入口，切换布局总览、排除原因、核心选址评分；“定位布局 / 正俯视”便于检查。参数、地图或采样变化会使旧布局过期。面板可滚动；参数当前仅在运行会话中保存。

验证布局：`.\tools\project.ps1 test-settlement`。完整验证：`test`；渲染与交互验证：`capture`，包含三个 `settlement_*.png` 布局截图。

右上选择 **03 道路生成**，在环境采样与聚落布局均有效时点击“独立生成道路”。默认采用不规则商贸城镇模式，可调整主次路宽、街巷疏密、坡度和种子；规则规划新城模式另有主方向与扰动设置。道路须经过城门并避让墙体及公共院区；提供路网总览、连接关系、道路占地、坡度与问题四层。点击入口高亮到核心的路径，点击道路查看属性。改道路参数复用上游；采样、聚落或地图变化会使道路过期。

道路验证：`.\tools\project.ps1 test-roads`；`test` 包含全链数据验证；`capture` 包含四张 `roads_*.png` 截图与引擎内交互验证。参数在当前运行会话中保存。

道路定型默认“转角平顺”3 米、“路口展开”2 米，修改后需要重跑道路及下游；“定位路口 / 近景”检查细部。`test-road-geometry-ui` 验证新参数、四层预览及道路到地块、建筑的完整衔接，并保存 `roads_final_*.png`。正式路面、碰撞与导航已接入最终装配。

选择 **03B 道路竖向**，生成纵断面、平整路口/广场及修整地形；可切换原地形、挖填热图和纵断面，修改参数会使 04–06 过期。验证：`test-road-grading` / `test-road-grading-ui`。

选择 **04 街区与地块**，在 03B 有效时点击“独立生成地块”。默认采用狭长宅地、共同背界、临街建筑带和后院；支持面宽、最大进深、最小面积、连续临街长度、退界、混合用途目标与种子；预览街区边界、地块用途、可建筑范围和剩余空地。点击地块查看其临街道路与地形统计。验证：`.\tools\project.ps1 test-parcels`。

无界面单独采样：`.\tools\project.ps1 sample`，摘要写入 `artifacts/environment_sample.json`。只验证采样：`.\tools\project.ps1 test-sampling`。

验证：`.\tools\project.ps1 test`；截图：`.\tools\project.ps1 capture`。工具脚本支持通过 `-GodotPath` 指定本地 Godot .NET 可执行文件。

## 文件

- `project.godot`：Godot 项目配置。
- `PcgCitySandbox.sln` / `PcgCitySandbox.csproj`：C# 解决方案与项目。
- `scenes/main.tscn` / `scripts/Main.cs`：地图主场景与交互界面。
- `configs/temperate_plain.tres`：可调整的地图参数。
- `configs/environment_sample.tres`：环境采样默认参数。
- `scripts/terrain/`：环境数据、三维绘制与验证。
- `scripts/pcg/environment/`：环境采样节点、算法、只读数据、地图适配器和验证。
- `scripts/pcg/preview/` / `scripts/Main.Sampling.cs`：采样预览与操作面板。
- `tools/project.ps1`：构建、运行、测试和截图入口。

项目使用 Compatibility 渲染器。后续按上述节点顺序实现城市布局与建筑生成。

形态与城防约束回归：`.\tools\project.ps1 test-medieval`，覆盖三组地图种子。城防规划仍以二维占地和门洞走廊为准，显示层已增加立体墙体示意；墙高不是工程尺寸，不代表实际城防工程设计；导出包含示意墙体碰撞及门道导航。

新区域预设：默认研究范围 2 × 2 公里，防御核心尺度 210 米，城外街带长 480 米、宽 140 米，3 个区域入口、2 个城门。修改核心尺度和城外片区参数互相独立。区域验收：`.\tools\project.ps1 test-regional`。

田园使用沿通路与地形组织的田组、组内耕作条带、果园和条件性草地。`.\tools\project.ps1 capture-landscape` 可输出田园整体与近景截图。

建筑体量：地块有效后选择 **05 建筑体量 → 独立生成建筑**。支持墙高、屋顶、通道、基础、种子、共墙及院区开关，并有街屋/城外/院区近景。验证：`.\tools\project.ps1 test-buildings`、`test-building-ui`；直接截图：`capture-buildings`。当前为灰模，没有室内；最终装配提供建筑外壳碰撞与入口导航连接。

场景布置：建筑有效后选择 **06 场景布置 → 独立生成场景**。支持内置土木/灰模方案、公共与院落密度、围栏、植被避让、对象预算和种子。全城、市场、院落近景可查看组合效果，点击追溯来源。验证：`test-placement`；完整界面与六张截图：`test-placement-ui`。目前使用内置程序模型及 V1–V3 展示细节，已加入碰撞、道路/入口导航和导出，外部资源适配仍待后续。


正式装配：06 面板底部可调整路面精度、碰撞和导航开关，然后点击“导出当前城镇”。也可执行 `.\tools\project.ps1 export-town` 自动生成并导出完整城镇。输出目录为 `generated/town_<seed>/<UTC时间>/`，`town.tscn` 可作为独立城镇实例，`preview.tscn` 附带观察相机与光照，可在 Godot 中直接运行。运行时道路图保存在 `road_graph.res`，节点修订清单为 `manifest.json`。导出命令需要图形渲染器来保存程序纹理，完成后自动关闭窗口。

三组种子的完整道路施工验收：`test-road-construction`（也包含在 `test` 中）；同建筑、同镜头的原地形/修整地形性能对照：`benchmark-road-construction`，报告和截图位于 `artifacts/road_construction/`。有效导出路径及重载寻路结果见 `artifacts/town_export_validation.json`。
