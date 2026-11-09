# PCG City Sandbox

基于 Godot 4.7.2 .NET（C#、.NET 8）的中世纪城镇程序化生成沙盒。在 2 × 2 km 的温带平原地图上，用一张由节点组成的生成图造出一座带城墙的小镇，节点分为六组：

**场地采样 → 场地 → 城镇布局 → 路网 → 街区与地块 → 建筑**

每个节点只做一个决策（选城址、画城墙、修主街、切地块……），节点之间只通过命名的数据键交换数据，所以任何一个节点都可以单独替换。

结构、数据和规则见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 运行

用 Godot **.NET 版**打开 `project.godot`，或在项目根目录执行：

```powershell
.\tools\project.ps1 run       # 打开沙盒，启动后自动生成整座城镇
.\tools\project.ps1 test      # 组件依赖检查 + 无界面检查：三个固定种子 × 地形与城市不变量 + 可复现性
.\tools\project.ps1 capture   # 生成后保存地表视角和各采样场视角的截图到 artifacts/capture/，并输出各视角渲染耗时
.\tools\project.ps1 build     # 只编译
```

`-GodotPath` 可指定本地 Godot .NET 控制台程序路径。

## 操作

- 滚轮缩放，WASD 或中键平移，右键旋转，Home 复位。
- 左侧：地图种子与重建、地表着色（地表 / 地形适宜性 / 各采样场，附图例）、图层开关（森林、河面、地被与岩石、等高线、城市各图层），鼠标所指位置各采样场的读数和建设限制。
- 右侧：按分组列出每个节点，显示状态、耗时、读写的数据、结果摘要和提示；参数直接修改。改动某个节点的参数只会让它和依赖它的节点过期；默认自动重新生成。

## 仓库

- 地图参数：`configs/temperate_plain.tres`。节点参数是代码中的默认值，在界面上修改，只在本次运行有效。
- Git LFS 管理二进制资源（见 `.gitattributes`），包括插件的 DLL。首次使用先执行 `git lfs install`。
- 地面使用 [Terrain3D](https://github.com/TokisanGames/Terrain3D) v1.1.0（MIT），位于 `addons/terrain_3d`，只保留了 Windows x86_64 的库。升级时从其发布页下载对应 Godot 版本的压缩包，替换该目录并删掉其他平台的库。
- 新克隆的仓库需要让 Godot 扫描一次项目才会加载插件；`tools/project.ps1` 会在需要时自动执行 `--import`。
- 在 Godot 编辑器里保存 C# 文件会把缩进改成 Tab；提交前执行 `dotnet format whitespace` 改回空格，或把编辑器的缩进设置改为空格。
- `.godot/`、`bin/`、`obj/`、`artifacts/` 为生成内容，不纳入版本库。
