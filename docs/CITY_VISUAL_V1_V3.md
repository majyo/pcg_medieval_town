# 城市规划沙盘 V1–V3：实现与验收

日期：2026-11-05。对应[城市视觉升级方案](CITY_VISUAL_UPGRADE.md)的 V1–V3。

## 打开演示

```powershell
.\tools\project.ps1 preview-city
```

该入口生成默认地图和六个节点，随后打开完整城镇的简模展示。普通 `run` 入口保留逐节点操作流程。

02–06 节点使用统一查看工具栏：

- **简模展示 / 规划分析 / 问题检查**：切换外观和诊断，不重新生成布局。
- **规划主题**：功能与用途、道路与路口、地块与后院、占地与通行、剩余空地、防御范围。只有已有的数据才显示；区域通路在道路尚未生成时用虚线表示。
- **结果**：隐藏/恢复结果，保留展示几何缓存。
- **细节、标注、屋顶**：独立控制；隐藏屋顶时露出的是占地示意地板，没有虚构房间。
- **城墙 / 低墙 / 隐藏城墙**：默认墙高 6 米，为示意尺寸，真实占地与城门开口沿用规划结果。
- **底图参数**：展开原地形面板；城市模式默认折叠它，让主视图更宽。
- 单击选择，点击空白取消；按 **F** 或“聚焦选择”定位。滚轮现在可以缩放到约 25 米的单体观察范围。
- **F9** 隐藏操作面板；场景标注通过独立的“标注”开关控制。

原来的节点查看下拉框由共享工具栏替代；生成、取消、参数、种子和节点有效性规则保留。01 环境采样继续使用原有环境诊断界面。

默认显示配置为 [`configs/city_visual.tres`](../configs/city_visual.tres)：墙体示意高度、近景细节阈值和初始标注/细节开关。查看选项对当前会话生效。

## V1：展示与交互

新增 [`CityPresentation`](../scripts/pcg/preview/CityPresentation.cs) 和 [`Main.CityView`](../scripts/Main.CityView.cs)，作为六个节点的只读展示消费者，没有增加生成节点。

展示从同一份结果沿上游引用装配环境、布局、道路、地块和建筑，避免混用不同版本。过期结果灰化，并显示“旧结果”；换地图后不能继续显示上一张地图的城市。选择绑定来源对象，重生成后清除失效选择。

用途配色跨地块和建筑保持一致。非用途分析主题淡化建筑配色，规划/问题模式降低自然底图对比并隐藏自然树木，突出当前主题。实体默认深度测试，二维分析覆盖允许穿透显示，二者明确区分。

标签按优先级做屏幕避让，跳过面板区域与屏幕外锚点。选中对象使用独立轮廓、入口走廊和详情；道路、地块、建筑、附属物、城门与现有问题位置可查询。门窗等细节继承父建筑 ID。拾取先按对象与 48 米空间块筛选，再求三角形命中。

新工具栏的模式、主题、细节、屋顶、城墙和结果显示开关复用几何，保持 PCG 结果对象及其数据指纹不变。原有场景生成配方和生成参数继续遵循原节点的失效规则。

## V2：道路与城市地面

[`CityGroundMesh`](../scripts/pcg/preview/CityGroundMesh.cs) 将源多边形裁切到原始地形的每个三角形，再使用同一高度插值贴地。先按较大区块裁剪轮廓，再处理本地网格，避免对整张地图反复计算完整路网。

默认路面、街巷、广场、院场、后园和田地使用共享材质与轻微非周期表面变化。道路外边界内侧增加约 0.25 米的路肩色带；该色带裁剪在权威道路占地内，不扩大路宽，不新增现代路缘石。

地面顶点法线继承原始地形的平滑法线，避免院地出现明显的三角形明暗接缝。普通贴地覆面抬高 0.025 米作为绘制间隔；路肩再增加 0.004 米，均不写回规划数据。

规划模式按尺度收放街区、地块、建筑带、后院与临街边；单独保留剩余空地主题及原始原因。现有农田行线继续使用源数据，在远处降低线条密度。农业资产与完整田园细节仍属 V4。

## V3：建筑与城防

建筑节点和最终场景共用 [`BuildingDetailBuilder`](../scripts/pcg/preview/BuildingDetailBuilder.cs)：

- 在原门位增加门框和门板分隔；台阶沿用现有体量。
- 按真实楼层和立面可用长度布置窗框、窗面和分格；避让门与共墙。
- 增加墙脚、楼层分隔、檐口厚度和山墙收边；不提高屋脊，不扩大屋顶规划范围。
- 教堂通高主殿使用高窗，塔楼沿真实楼层表达；修道院沿原有翼部细化。未生成的可选体块不会补造。
- 普通立面采用不超过 2 厘米的浅层示意表皮，屋檐收边使用毫米级绘制间隔；它们不是实际室内开洞。相关范围在验证中明确考虑这层表皮容差。

默认种子 73129 的展示包含 **654 个门位框饰、8,597 个窗位、96,574 个新增建筑细节三角形**。源建筑数量仍为 654，附属物仍为 1200；门窗不作为额外场景布置对象计数。细节按空间区块合批，只在近景显示，并带缩放滞回。

[`FortificationPreviewBuilder`](../scripts/pcg/preview/FortificationPreviewBuilder.cs) 从已有 `WallFootprint` 生成墙身、墙顶和垛口。默认 10,244 个三角形；门洞直接保留原 `Gate.Passage`，没有缩窄开口。低墙是同一占地的查看版本。首版没有在规划占地外增加塔楼或门楼。

## 验证记录

- 构建：0 警告、0 错误。
- 完整数据回归：环境、区域、道路及道路几何、地块、建筑、布置、地形视觉，以及新城市视觉检查。
- 城市几何验证覆盖种子 **73129 / 7 / 22017**：地面面积与贴地、窗位和楼层、共墙、门位、屋顶包络、墙体占地、城门净空、源数据不变与视图缓存。
- 默认种子的贴地顶点最大插值差约 `3.2e-6 m`、三角形中心最大插值差约 `2.5e-5 m`；这是与原网格的数值对照，不代表规划测量精度。
- 引擎内验证：模式和主题切换、缓存显示开关、可见屋顶拾取、选中来源、聚焦、隐藏屋顶、旧结果状态、重生成后选择清理，以及 1440×900 / 1280×720 窗口检查。
- 原建筑和场景布置界面检查继续回归；拾取测试显式使用新工具栏的简模模式。

命令：

```powershell
.\tools\project.ps1 test
.\tools\project.ps1 test-city-visual-ui
.\tools\project.ps1 capture
.\tools\project.ps1 capture-city
.\tools\project.ps1 benchmark-city
```

`test-city-visual` 可单独检查三组种子的城市展示几何。`capture-city` 输出固定视角截图；`benchmark-city` 增加完整城镇性能采样。

报告：[三种种子](../artifacts/city_visual_validation.json)、[交互](../artifacts/city_visual_ui.json)、[完整回归](../artifacts/city_full_regression.log)、[整套界面回归](../artifacts/city_full_ui.log)、[城市截图日志](../artifacts/city_visual_capture.log)。

## 前后截图

默认种子、相机位置和方向保持一致，水面时间固定。以下比较使用完整城镇，不是自然地形单独场景。

| 视角 | 原展示 | 升级后 |
| --- | --- | --- |
| 城镇全景 | [前](../artifacts/city_visual/before/01_overview.png) | [后](../artifacts/city_visual/after/01_overview.png) |
| 核心俯视 | [前](../artifacts/city_visual/before/02_core_top.png) | [后](../artifacts/city_visual/after/02_core_top.png) |
| 街屋 | [前](../artifacts/city_visual/before/03_street.png) | [后](../artifacts/city_visual/after/03_street.png) |
| 单体近景 | [前](../artifacts/city_visual/before/04_house.png) | [后](../artifacts/city_visual/after/04_house.png) |
| 城门 | [前](../artifacts/city_visual/before/05_gate.png) | [后](../artifacts/city_visual/after/05_gate.png) |
| 教堂 | [前](../artifacts/city_visual/before/06_church.png) | [后](../artifacts/city_visual/after/06_church.png) |
| 修道院 | [前](../artifacts/city_visual/before/07_abbey.png) | [后](../artifacts/city_visual/after/07_abbey.png) |
| 市场 | [前](../artifacts/city_visual/before/08_market.png) | [后](../artifacts/city_visual/after/08_market.png) |
| 后院 | [前](../artifacts/city_visual/before/09_yard.png) | [后](../artifacts/city_visual/after/09_yard.png) |
| 路口 | [前](../artifacts/city_visual/before/10_junction.png) | [后](../artifacts/city_visual/after/10_junction.png) |
| 低角度 | [前](../artifacts/city_visual/before/11_low_angle.png) | [后](../artifacts/city_visual/after/11_low_angle.png) |
| 操作界面 | [前](../artifacts/city_visual/before/12_interface.png) | [后](../artifacts/city_visual/after/12_interface.png) |

另有[选中建筑](../artifacts/city_visual/checks/selected_building.png)、[地块分析](../artifacts/city_visual/checks/mode_1.png)、[通行关系](../artifacts/city_visual/checks/access.png)、[低墙和隐藏屋顶](../artifacts/city_visual/checks/low_wall_no_roof.png)、[较小窗口](../artifacts/city_visual/checks/interface_1280.png)。

## 性能与后续范围

性能比较使用本机 RTX 4090、Compatibility、1440×900、相同完整城镇（654 个建筑、1200 个附属物）；隐藏操作面板，保留场景标注，关闭 VSync 和 FPS 限制。全城、街屋、城门各预热 2 秒、测量 10 秒。[基线](../artifacts/city_visual/before/performance.json)与[升级后数据](../artifacts/city_visual/after/performance.json)单独保存。

完整展示几何初次装配约 1.25 秒；查看模式与选择变化复用缓存。该时间是本机观测值，不是跨设备承诺。

| 完整城镇视角 | 原中位数 / P95 | 新中位数 / P95 |
| --- | --- | --- |
| 全城 | 0.532 / 1.410 ms | 0.569 / 1.315 ms |
| 街屋 | 0.361 / 0.860 ms | 0.402 / 0.718 ms |
| 城门 | 0.369 / 0.842 ms | 0.389 / 0.726 ms |

中位数增幅为约 5.6%–11.4%，P95 本次采样没有增加，满足方案中 25% 的目标。显存增量最大约 32.2 MiB，低于 128 MiB 的首轮预算。点击回调实测约 7.7 ms，示例屋顶命中只测试 140 个候选三角形；这不是跨设备的端到端延迟保证。

12 组固定相机记录已对齐，最终截图已逐张检查，测试日志无引擎或着色器错误；汇总见 [`verification_summary.json`](../artifacts/city_visual/verification_summary.json)。

当前完成 V1–V3。V4 的院落设施新造型、果园实体和进一步田园表现，以及 V5 的完整问题筛选清单、逐段净宽测量、更多局部透视和高级来源联动仍按原方案后续实施。墙高仍为示意，门窗无真实室内，规划算法、碰撞和导航未因此改变。
