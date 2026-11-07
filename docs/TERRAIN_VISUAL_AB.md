# 地形视觉 A / B 实施与验收

日期：2026-11-04。对应[升级方案](TERRAIN_VISUAL_UPGRADE.md)的 A（地表与色调）和 B（河岸与河水）。

后续外观微调：已减弱平铺重复感。地表改为三个相邻采样区域的随机旋转、偏移和尺度混合，边界连续，法线随纹理方向一起变换；微细节颜色打包到法线/粗糙度纹理的 A 通道，减少读取次数。小斑块加入坐标扰动和不同尺度混合，并减弱远处斑点对比。河面纹理随水流一起扰动，避免固定周期条带。

本次微调截图独立保存在 [`artifacts/terrain_visual/less_tiling`](../artifacts/terrain_visual/less_tiling/cameras.json)，保留下面 A/B 初次交付的对比记录。主要对比：[微调前低角度](../artifacts/terrain_visual/after/08_low_angle.png)、[微调后低角度](../artifacts/terrain_visual/less_tiling/08_low_angle.png)，以及[河岸近景](../artifacts/terrain_visual/less_tiling/04_bank_close.png)。

微调验证：构建零警告/错误；三种地图配置和引擎内材质/诊断/动画/重建检查通过；同相机截图已复查。RTX 4090、1440 × 900 下三个视角的 P95 为 0.556–0.702 ms，较 A/B 初次交付增加约 0.038–0.050 ms，显存增加约 2.0 MiB。记录：[数据测试](../artifacts/terrain_less_tiling_tests.log)、[运行时检查](../artifacts/terrain_less_tiling_ui.log)、[截图与性能](../artifacts/terrain_less_tiling_capture.log)、[汇总](../artifacts/terrain_visual/less_tiling/verification.json)。

## 已实现

- 六类自然表面：草甸、干草、林下腐殖土、裸土、湿泥、沙砾。使用世界坐标、宏观色调、中尺度斑块、近景颗粒与法线混合；自然控制场读取现有地图，原始地形和 PCG 数据不变。
- 两组共享的 1K、六层程序纹理数组，分别提供表面变化与法线/粗糙度。包含 mipmap、各向异性过滤和按屏幕采样足迹衰减；远景跳过不可见微细节采样。纹理由代码确定性生成，无外部美术依赖。
- 河岸使用局部河段水位、实际地表高度与坡度控制湿泥和间断沙砾，不再使用固定名义河宽描边。
- 水面使用沿河累计长度、横向位置和局部流向；两组不规则流动法线和轻微表面变化跟随河道。水深在 shader 中读取原始高度纹理，采用与 `HeightAt` 一致的三角形插值。水面不透明、金属度为零，不依赖屏幕深度、SSR 或 Decal。
- 水面扩展到干岸以下，并限制于地图边界；去除边界夹取造成的退化竖直薄片。默认水面 32,588 个三角形。
- 自然景观新增“简洁 / 标准 / 细节”预设。切换只修改材质参数；诊断图层仍使用原始无光照颜色，网格按图层缓存。
- 日光改为较中性的暖色，环境光略偏冷。F9 隐藏或恢复面板，可直接检查无界面场景。

## 操作与配置

```powershell
.\tools\project.ps1 run
.\tools\project.ps1 test
.\tools\project.ps1 test-terrain-visual-ui
.\tools\project.ps1 capture-terrain
.\tools\project.ps1 benchmark-terrain
```

`test` 已包含新的地形视觉数据验证。`test-terrain-visual` 可单独运行三种地图配置的验证。`capture-terrain` 生成 12 张截图；`benchmark-terrain` 额外记录 30 秒性能数据，测量期间关闭 VSync 和 FPS 限制，进程结束即恢复常规运行环境。

默认参数在 [`configs/terrain_visual.tres`](../configs/terrain_visual.tres)，完整可导出参数见 [`TerrainVisualSettings.cs`](../scripts/terrain/TerrainVisualSettings.cs)。界面质量选择仅对当前会话生效，重建地图时保留本次选择。

主要代码：[`TerrainVisualData.cs`](../scripts/terrain/TerrainVisualData.cs)、[`TerrainVisualTextures.cs`](../scripts/terrain/TerrainVisualTextures.cs)、[`RiverVisualMesh.cs`](../scripts/terrain/RiverVisualMesh.cs)、[`terrain.gdshader`](../shaders/terrain.gdshader)、[`river.gdshader`](../shaders/river.gdshader)。

## 自动验证

构建：`dotnet build`，0 警告、0 错误。

完整数据回归覆盖原始地图、环境采样、聚落、道路及道路几何、地块、建筑、场景布置。新的地形视觉测试覆盖：

| 配置 | 验证重点 |
| --- | --- |
| 种子 73129，河宽 48 米 | 默认场景、确定性控制图 |
| 种子 7，河宽 20 米，无森林 | 窄河道和空森林控制场 |
| 种子 22017，河宽 100 米 | 宽河道和地图边界 |

测试比较视觉生成前后的全部环境数组、法线、河流和树木指纹；验证局部河段查询与原查询一致、水面高程、下游坐标、单位流向、边界范围、干岸覆盖和三角形绕序。报告：[`terrain_visual_validation.json`](../artifacts/terrain_visual_validation.json)。

引擎内运行时验证覆盖六个 PCG 结果在材质切换后保持同一对象、三档质量、四个缓存图层、河面开关、F9、固定时间截图复现、时间推进产生水面变化，以及连续四次地图重建后的旧节点释放与显存稳定。纹理的 mipmap 使用实际上传图像读回检查。报告：[`terrain_visual_ui.json`](../artifacts/terrain_visual_ui.json)。

测试记录：[完整回归](../artifacts/terrain_full_regression.log)、[地形视觉数据](../artifacts/terrain_visual_tests.log)、[引擎内交互](../artifacts/terrain_visual_ui.log)、[截图与性能](../artifacts/terrain_visual_capture.log)。

## 截图检查

升级前后均使用种子 73129、1440 × 900 和同一组相机参数；水面时间固定为 5 秒。灯光调整属于 A 阶段，因此新版使用调整后的灯光。相机记录保存在各目录的 `cameras.json`。

| 视角 | 升级前 | 升级后 |
| --- | --- | --- |
| 全图斜俯视 | [前](../artifacts/terrain_visual/before/01_overview.png) | [后](../artifacts/terrain_visual/after/01_overview.png) |
| 正俯视 | [前](../artifacts/terrain_visual/before/02_top.png) | [后](../artifacts/terrain_visual/after/02_top.png) |
| 河湾 | [前](../artifacts/terrain_visual/before/03_river_bend.png) | [后](../artifacts/terrain_visual/after/03_river_bend.png) |
| 岸边近景 | [前](../artifacts/terrain_visual/before/04_bank_close.png) | [后](../artifacts/terrain_visual/after/04_bank_close.png) |
| 林缘 | [前](../artifacts/terrain_visual/before/05_forest_edge.png) | [后](../artifacts/terrain_visual/after/05_forest_edge.png) |
| 北侧河口 | [前](../artifacts/terrain_visual/before/06_river_north.png) | [后](../artifacts/terrain_visual/after/06_river_north.png) |
| 南侧河口 | [前](../artifacts/terrain_visual/before/07_river_south.png) | [后](../artifacts/terrain_visual/after/07_river_south.png) |
| 低角度 | [前](../artifacts/terrain_visual/before/08_low_angle.png) | [后](../artifacts/terrain_visual/after/08_low_angle.png) |
| 坡度诊断 | [前](../artifacts/terrain_visual/before/09_slope.png) | [后](../artifacts/terrain_visual/after/09_slope.png) |
| 城镇整体 | [前](../artifacts/terrain_visual/before/10_town.png) | [后](../artifacts/terrain_visual/after/10_town.png) |
| 市场 | [前](../artifacts/terrain_visual/before/11_market.png) | [后](../artifacts/terrain_visual/after/11_market.png) |
| 操作面板 | [前](../artifacts/terrain_visual/before/12_interface.png) | [后](../artifacts/terrain_visual/after/12_interface.png) |

首轮检查后降低了过强的干草色斑与浅滩亮度，将规则正弦水纹替换为不规则流动纹理。最终检查重点为：全图色调与层次、近景纹理重复、浅深水过渡、河口边界、诊断色和面板布局。固定时间的水面序列保存在 `artifacts/terrain_visual/motion/flow_0.png` 至 `flow_3.png`，第 1 与第 4 帧为相同时间，像素指纹必须一致。

## 性能记录与范围

性能环境：本机 NVIDIA GeForce RTX 4090，OpenGL 3.3 / 驱动 610.88，Compatibility，1440 × 900，无 UI，VSync 关闭且不限帧。全图、区域、河岸三个固定视角分别预热 2 秒、采样 10 秒。记录总帧间隔、CPU/GPU 渲染时间、提交次数、图元和显存。

原始记录：[升级前](../artifacts/terrain_visual/before/performance.json)、[升级后](../artifacts/terrain_visual/after/performance.json)。这是自然地形场景的本机测量，不代表低端显卡或完整城镇的性能上限。城市完整链已进行数据与界面验证，但此表不测量城镇性能。

| 视角 | 原中位数 / P95 | 新中位数 / P95 | P95 变化 |
| --- | --- | --- | --- |
| 全图 | 0.441 / 0.679 ms | 0.431 / 0.661 ms | -2.5% |
| 区域 | 0.398 / 0.628 ms | 0.421 / 0.651 ms | +3.6% |
| 河岸近景 | 0.354 / 0.501 ms | 0.346 / 0.506 ms | +1.0% |

本次采样三个视角均满足相对帧耗时增幅 20% 以内和 P95 小于 16.7 ms 的目标。约 2% 的下降视为运行波动，不作为提速结论。提交次数均为 16；引擎统计的显存较基线增加约 **79.5 MiB**，低于新增 128 MiB 预算。四次重建后内存收敛，后三次一致。

最终 12 张截图已逐张检查，日志无引擎/着色器错误；前后相机记录完全一致。坡度诊断截图有 98.13% 像素完全一致，水面外观变化包含在剩余差异内。汇总见 [`verification_summary.json`](../artifacts/terrain_visual/verification_summary.json)。

新增共享表面纹理和每张地图的控制/高度纹理合计 **81,682,760 字节，约 77.9 MiB**。首次运行还需要生成共享纹理；后续同进程重建复用纹理。控制图生成约 0.7 秒，首次地图约 6 秒，后续重建约 3.1 秒；这些是本机观测值。

## 本轮边界

原始 5.21 米地形网格保持不变，极近景和低角度仍可能看出岸线折角。新水面细分改善流向和水位插值，不改变陆地轮廓。

道路、院落和农田仍沿用现有地面网格及简化材质；树木仍是原有三种简模。它们与新自然地表之间的接缝、清理后的林下覆盖及灌木/芦苇实体，分别属于 C、D 阶段。A/B 没有引入植被散布、地形位移、碰撞、导航或水体模拟。
