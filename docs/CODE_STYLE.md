# 代码风格

本文根据 2026-11-11 的项目配置和自有源码整理，供新增代码、修改和审查时参考。适用范围是 `scripts/`、`shaders/` 和 `tools/`；`addons/terrain_3d/` 是第三方代码，不作为本项目风格的样本，也不随自有代码一起格式化。

项目采用 Godot .NET + C#，整体风格是：**简洁、直接，按组件组织代码；生成节点只做一个决策，通过类型化数据键协作；把数据、算法和显示分开。** 架构细节见 [ARCHITECTURE.md](ARCHITECTURE.md)。

本文区分三类约定：

- **配置规则**：已写入 `.editorconfig`、`.gitattributes`、项目文件，或已有代码检查。
- **现有惯例**：在源码中反复出现，但没有专门的自动检查；新增代码沿用所在文件的写法。
- **改进建议**：用于处理现有差异，不表示仓库已强制执行。

## 1. 文件与排版

### 配置规则

| 项目 | 规则 | 来源与边界 |
| --- | --- | --- |
| 编码 | UTF-8，文件末尾换行，删除行尾空白 | 根目录 `.editorconfig` |
| 通用换行 | 编辑器默认 LF；Git 文本文件默认 LF | `.editorconfig`、`.gitattributes` |
| 换行例外 | `.ps1`、`.sln` 的 Git 属性明确指定 CRLF | 与编辑器通用 LF 设置存在差异，见第 10 节 |
| C# 缩进 | 4 个空格 | `.editorconfig` 的 `[*.cs]` |
| C# 大括号 | 常规代码块的左大括号另起一行 | `csharp_new_line_before_open_brace = all` |
| 单行代码 | 不保留压在同一行的多条语句；允许保留单行代码块 | 两项 `csharp_preserve_single_line_*` 配置 |
| 行长 | 140 字符目标 | `max_line_length = 140`；本次空白格式检查不会据此自动折行或报错 |
| 第三方 C# | 标记为生成代码 | `[addons/**.cs]` 的 `generated_code = true` |

### 现有惯例

- `using` 后空一行，再写文件范围命名空间 `namespace PcgCitySandbox.City;`，再空一行进入类型定义。
- `using` 通常按 `System.*`、`Godot`、`PcgCitySandbox.*` 排列；命名冲突时使用局部别名，例如 `using Environment = Godot.Environment;`。
- 方法之间空一行；方法内部用空行隔开读取输入、准备数据、核心计算和发布结果等步骤。
- 长签名、长条件、长调用和插值字符串在语义边界换行，续行通常增加一级缩进。多行初始化器和 `switch` 表达式常保留尾逗号。
- 单条语句的 `if`、`foreach`、`for` 常省略大括号；分支有多条语句时使用大括号。复杂嵌套应优先保证分支归属清楚。
- 小枚举、简单 getter、短方法常写成一行；不为压缩行数而把多步操作塞到一行。

```csharp
public bool Contains(int k) => _cells.Contains(k);

public static float DistanceToLine(Vector2 p, IReadOnlyList<Vector2> line)
{
    float best = float.PositiveInfinity;
    for (int i = 1; i < line.Count; i++)
        best = Math.Min(best, DistanceToSegment(p, line[i - 1], line[i]));
    return best;
}
```

## 2. 命名与源码组织

### 现有惯例

| 对象 | 写法 | 项目中的例子 |
| --- | --- | --- |
| 目录 | 小写，按组件和业务分组 | `terrain/`、`city/nodes/roads/`、`rendering/environment/` |
| C# 文件 | PascalCase，围绕主要类型或一组紧密相关类型命名 | `MapCamera.cs`、`Roads.cs`、`Lots.cs` |
| 类型、方法、属性、事件、枚举成员 | PascalCase | `RoadLinks`、`HeightAt`、`CellSize`、`Changed`、`RoadKind.Main` |
| 接口 | `I` 前缀 | `ITerrain`、`IKeyReader`、`ISummarized` |
| 泛型参数 | `T` 或 `T` + 含义 | `Key<T>`、`CityNode<TOptions>` |
| 私有实例字段 | `_camelCase` | `_nodes`、`_running`、`_refreshers` |
| 常量、静态只读字段 | PascalCase，包括私有成员 | `HalfBand`、`Seeds`、`ShaderPath` |
| 参数与局部变量 | camelCase | `cellSize`、`maxDistance`、`buildable` |
| 生成节点与选项 | 决策名 + 对应的 `Options` | `SampleHeight`、`SampleHeightOptions` |
| 数据键字符串 | 小写分组 + 点号 + snake_case | `site.height`、`site.above_water`、`eval.travel_cost` |

Godot 回调沿用引擎签名，例如 `_Ready`、`_Input`、`_Process`。

几何和网格算法中普遍使用 `p`、`a`、`b`、`x`、`z`、`i`、`k` 等短变量；节点入口常用 `NodeContext c`、选项 `o`。这种简写适用于含义明确的小范围运算，跨多步的业务数据沿用 `buildable`、`growth`、`network` 等有含义的名称。

不强制“一文件一类型”：节点选项放在节点之前，相关数据记录、枚举、集合包装和小工具可以放在同一业务文件中。`Main` 则用 `partial` 分为 `Main.cs` 与 `MainInterface.cs`，分别承载组合逻辑和界面接线。

## 3. 命名空间与组件边界

以下映射及组件引用关系由 `tools/project.ps1 test` 中的 `Test-Dependencies` 做文本扫描检查。子目录用于组织文件，**不会继续细分命名空间**。

| 位置 | 命名空间 | 可引用的其他项目组件 |
| --- | --- | --- |
| `scripts/` 根目录 | `PcgCitySandbox` | 组合入口，可以引用全部组件 |
| `scripts/terrain/` | `PcgCitySandbox.Terrain` | 无 |
| `scripts/city/` 及其子目录 | `PcgCitySandbox.City` | `Terrain` |
| `scripts/camera/` | `PcgCitySandbox.CameraControl` | 无 |
| `scripts/rendering/` 及其子目录 | `PcgCitySandbox.Rendering` | `Terrain`、`City` |
| `scripts/ui/` | `PcgCitySandbox.Ui` | `City` |

这里的限制针对项目组件引用，不禁止使用 Godot 的 `Vector2`、`Color`、`Mathf` 等基础类型。

- 地形和城市生成不操作场景节点；现有脚本扫描 `Node`、`Node3D`、`Control`、`Camera3D`、`CanvasLayer`、`MeshInstance3D`、`SceneTree`、`GetTree` 等名称。它是基础防线，不是完整的语义依赖分析。
- 城市生成只经 `ITerrain` 查询地形，不依赖 `TemperateMapData` 的实现细节。
- 界面通过事件、getter/setter 和回调表达操作，跨组件联动在 `Main` / `MainInterface` 中接线。
- 共享城市算法放在 `city/tools/`，数据与派生查询放在 `city/data/`，节点自身保留该决策独有的逻辑。

## 4. 类型、成员与表达式

以下属于现有惯例，项目没有为命名、成员排序或表达式风格配置统一的分析器规则。

- 无继承需求的数据和实现类大量使用 `sealed`；继承 Godot 对象的脚本类使用 `partial`，基础节点使用 `abstract`。
- 类中常先放常量与字段，再放构造函数、属性和公开操作，最后放私有实现；这不是统一的强制成员顺序，局部修改保持所在类型的组织方式。
- 轻量数据使用 `sealed record`，复杂集合与查询使用类；必需初始化的数据可用 `required` + `init`。小型包装类也已有主构造函数写法。
- 优先用属性表达状态，用方法表达计算或操作。短 getter 和简单转发使用 `=>`；多步骤逻辑保留块体。
- 构造表达式或方法返回值已让类型清楚时常用 `var`；整数索引、浮点量、布尔量常显式写 `int`、`float`、`bool`。不统一改成全 `var` 或全显式类型。
- 已有目标类型明确的 `new()`、`new(...)` 和集合表达式 `[Keys.Grid, Keys.Height]`；也存在 `new[]`、`Array.Empty<T>()`，不为语法统一批量替换。
- 只读字段用 `readonly`；集合对外常暴露 `IReadOnlyList<T>` / `IReadOnlyDictionary<TKey, TValue>`，可变容器留在内部。
- 数据筛选、映射、汇总常用 LINQ；逐格计算、寻路和需要明确状态更新的代码多用循环。不会把所有循环都改写成 LINQ。
- 紧密配合的小算法常写为局部函数，例如 `Inspector.Slider` 的 `Show`、`Commit`、`Parse`；跨节点复用时再提取为公共工具。

项目已启用可空引用类型（`<Nullable>enable</Nullable>`）。可缺失结果、可选回调和可选状态使用 `T?`，调用方处理 `null`；由 Godot 生命周期稍后初始化的字段存在 `null!` 写法。新增空值抑制应有明确的初始化顺序或前置条件依据。

注意：`readonly`、只读接口和 `record` 都不保证深层不可变，数组与容器中的对象仍可能被修改。

## 5. 生成节点的固定写法

这是本项目最有辨识度的结构约定，依据是 `CityNode`、`NodeContext`、`TownGraph` 和现有节点实现。

1. 一个节点完成一个可独立替换的决策；有参数时定义同名的 `XxxOptions`，无参数时使用 `NoOptions`。
2. 参数写成带默认值的属性，以 `[Param]` 声明中文标签、范围、步长和 `Hint`。`Hint` 解释控制对象以及调大、调小的影响。
3. 节点声明 `Group`、`Title`、`Description`、`Reads`、`Writes`、`Edits`，不适用的列表沿用基类空列表。分组使用 `NodeGroups` 常量。
4. `Run` 开始集中获取输入和网格，随后计算，最后用 `c.Set` 发布结果。
5. 新建数据用 `Writes`；替换上游已有数据用 `Edits`，先 `Copy()` 或构造副本，再修改副本。节点之间不直接调用或引用彼此。
6. 选项使用框架传入的运行快照；当前 `CloneOptions` 按属性复制，属于浅复制。现有选项主要是标量，新增可变引用类型参数时需要同时处理复制语义。
7. 编排统一放在 `TownGraph.Create`，顺序就是运行顺序；不能只新增类而忘记接入生成图。

`WallLane.cs` 是编辑型节点的完整例子，`SampleHeight.cs` 是新写采样场的完整例子。典型片段为：

```csharp
var links = c.Get(Keys.RoadLinks).Copy();
// 在副本上完成本节点的决策。
c.Set(Keys.RoadLinks, links);
```

数据键在 `Keys.cs` 集中声明，包含稳定标识、中文名称和说明；数据类型按业务归入 `data/`，需要摘要时实现 `ISummarized`，不变量归入 `Contracts`。新增需要显示的数据，还应在 `FieldStyles` 或 `CityView` 中登记画法。

节点的 `Description`、节点参数的 `Hint`、键的 `Description` 不能为空，已有 `Checks.Documentation` 检查；这是界面说明要求，不等于每个公开成员都必须有 XML 注释。

## 6. 确定性、取消与错误处理

### 现有约定

- 同一地形、输入和选项应得到同样的节点输出。随机算法使用显式种子，例如 `new Random(o.Seed)`；地形和环境生成也使用确定的地图种子或位置哈希。
- 结果依赖候选次序时，代码多用 `OrderBy`、必要时加 `ThenBy` 固定次序。不要让无序集合的枚举顺序意外决定取舍。
- 节点在后台运行，长循环和共享算法传递、检查 `CancellationToken`；`Parallel.For` 通过 `ParallelOptions.CancellationToken` 响应取消。并行逐格计算通常写各自的输出位置。
- 无法完成某个局部候选但整体仍合法时，用 `c.Notice` 说明跳过或降级原因；声明错误、契约错误和不合法参数使用异常。
- `CityGraph` 集中处理取消与节点异常，失败或取消不发布本次结果；节点内部不吞掉异常并伪装成成功。
- 入口和检查代码使用 `GD.Print`、`GD.PrintErr`、`GD.PushError` 等报告结果，普通节点主要通过输出摘要和 `Notice` 提供反馈。

现有可复现性检查比较的是指纹，输出被修改的检查比较的是摘要；它们提供回归线索，不是逐字节相等或深层不可变的证明。

## 7. 注释、文案与数值语义

### 现有惯例

- 标识符和代码注释主要使用英文；README、设计文档、界面标签、提示、结果摘要和业务错误消息主要使用中文。工具脚本也有英文诊断信息。
- XML `/// <summary>` 用来说明类型职责、算法意义、输入输出、边界条件和单位；必要时用 `<see cref="..."/>`、`<paramref name="..."/>` 引用符号。
- `//` 说明局部算法为什么这样处理，例如北向坡的扣分方式、射线步进为何安全、纹理样本为何取像素中心。不要只把下一行语句翻译一遍。
- 调参量放入 Options / Settings 并给默认值；有独立含义的算法阈值常用命名常量并解释用途，例如 `WallLane.HalfBand`、`GateGap`、`JoinReach`。
- 当前代码也直接使用公式系数，不存在“所有数字都必须提取常量”的规则。

几何代码遵守统一语义：世界 X/Z 为水平面、Y 向上，1 单位为 1 m，地图中心在原点；平面 `Vector2` 的 `X/Y` 对应世界 `X/Z`。长度、坡度、坡向、面积与无值约定应在命名、注释、字段元数据或界面标签中说明。尤其不要混淆度与弧度、米与格数、平方米与公顷。

## 8. Godot、着色器与工具脚本

### Godot C#

- 场景主要由 C# 组装，`scenes/main.tscn` 只挂载入口脚本；不要把生成规则挪到场景控制脚本中。
- 资源配置使用 `[GlobalClass]` / `[Export]`，例如 `MapSettings`；运行时生成参数使用自有 `[Param]` 供界面反射生成控件。
- 控件交互常用 C# 事件与 lambda，多个控件共享同一值时使用 `Setting<T>`；新增界面外观优先复用 `UiStyle`。
- 引用资源使用 `res://...`，生成输出使用项目约定的 `artifacts/` 目录。

### 着色器

自有着色器的风格与 C# 不同：**Tab 缩进，左大括号在同一行，变量和函数使用 snake_case**；引擎内建变量保留大写。当前 `.editorconfig` 没有着色器专属缩进规则，这是源码惯例。

- 文件开头说明用途、通道含义和坐标语义，再声明 `shader_type`、`render_mode`、共用 include、uniform / varying 和函数。
- 光照、噪声、风与色彩转换共用 `common/painterly.gdshaderinc`，辅助函数使用 `pl_` 前缀。
- 参数通过 uniform 暴露，适当声明 `source_color`、纹理过滤与重复方式；跨着色器的全局参数同时在 `project.godot` 注册，由渲染层推送。

### PowerShell

`tools/project.ps1` 是当前唯一的自有 PowerShell 工具：4 空格缩进、大括号同行、函数用 Verb-Noun（`Test-Dependencies`），参数用 PascalCase，普通变量用 camelCase。它使用 `$ErrorActionPreference = 'Stop'`，检查外部程序退出码，并通过 `Push-Location` / `try` / `finally` / `Pop-Location` 恢复工作目录。新增入口操作应延续该结构。

## 9. 格式化与验证

在项目根目录执行，只处理自有 C#：

```powershell
# 只验证空白格式，不改文件。
dotnet format whitespace PcgCitySandbox.csproj --verify-no-changes --no-restore --include scripts

# 需要修正空白时执行；完成后检查 diff。
dotnet format whitespace PcgCitySandbox.csproj --no-restore --include scripts

# C# 或项目配置改动：编译。
.\tools\project.ps1 build

# 生成逻辑、数据契约、依赖或节点图改动：运行现有完整检查。
.\tools\project.ps1 test
```

`--no-restore` 要求项目已经还原依赖；新克隆先执行 `dotnet restore PcgCitySandbox.csproj`。Godot 编辑器保存 C# 时可能写入 Tab，提交前检查格式，或将编辑器缩进设置为空格。

`test` 包含组件依赖检查、编译、三个固定种子的地形与城市检查，并检查契约、说明完整性、依赖失效和指纹可复现性。它不包含上述 `dotnet format` 检查，也不替代渲染与交互的实际验证；画面变更可使用 `capture` 辅助观察。纯文档修改无需运行整个生成流程。

提交前检查：文件与命名空间位置正确；代码沿用所在文件的排版和命名；节点声明、复制与取消处理正确；新增名词有说明；没有混入第三方格式化、缓存或截图输出。`.godot/`、`bin/`、`obj/`、`artifacts/` 属于生成内容，不纳入版本库。

## 10. 本次检查结果与改进建议

本次工作只新增本文并在 README 中加入入口，没有重排或修改业务代码。以下是 2026-11-11 工作树的检查快照，不代表后续版本自动满足：

| 检查项 | 结果 |
| --- | --- |
| 自有代码范围 | 106 个 C# 文件，共 15,694 行；6 个着色器 / include；1 个 PowerShell 脚本 |
| C# 文件范围命名空间 | 106 个文件均采用该写法，归入根命名空间及五个组件命名空间 |
| C# 以 Tab 开头的行 | 0 |
| C# 行尾空白 | 0 |
| C# 超过 140 字符的行 | 6 行，分布在 `GroundCover.cs`、`TreeMeshes.cs`、`Inspector.cs`、`Legend.cs`、`SandboxLayout.cs`、`StagePage.cs` |
| 空白格式检查 | `dotnet format whitespace ... --verify-no-changes --no-restore --include scripts` 通过，退出码 0 |
| 编译、无界面生成和视觉验证 | 本次未运行；以上结论是风格检查结果 |

建议按后续实际修改逐步处理：

1. **明确换行例外**：为 `.ps1` / `.sln` 增加匹配 Git 属性的 EditorConfig 覆盖，或统一调整两处配置，避免编辑器与 Git 各自转换。本次未修改配置。
2. **把 140 字符当作阅读目标**：有机会时折开 6 处长行；格式器通过不能证明行长达标。不据此单独批量重排源码。
3. **按职责控制大文件增长**：`Contracts.cs` 为 1,114 行，`CityView.cs` 为 832 行；继续新增规则或画法时评估是否按业务拆分。项目目前没有文件行数上限，这也不构成立即重构要求。
4. **保持规则与工具一致**：命名、注释语言、成员组织、着色器风格仍靠审查；若需要自动执行，再有针对性地增加配置或检查。不要把本文中的惯例误写成已有检查能力。

## 参考源码

- 配置与执行入口：[.editorconfig](../.editorconfig)、[.gitattributes](../.gitattributes)、[PcgCitySandbox.csproj](../PcgCitySandbox.csproj)、[project.ps1](../tools/project.ps1)。
- 节点与数据协作：[CityNode.cs](../scripts/city/core/CityNode.cs)、[Key.cs](../scripts/city/core/Key.cs)、[CityGraph.cs](../scripts/city/core/CityGraph.cs)、[TownGraph.cs](../scripts/city/TownGraph.cs)、[Keys.cs](../scripts/city/Keys.cs)。
- 典型实现：[SampleHeight.cs](../scripts/city/nodes/sampling/SampleHeight.cs)、[WallLane.cs](../scripts/city/nodes/roads/WallLane.cs)、[Roads.cs](../scripts/city/data/Roads.cs)、[Geometry.cs](../scripts/city/tools/Geometry.cs)。
- 界面与渲染：[Inspector.cs](../scripts/ui/Inspector.cs)、[Setting.cs](../scripts/ui/Setting.cs)、[Forest.cs](../scripts/rendering/environment/Forest.cs)、[painterly.gdshaderinc](../shaders/common/painterly.gdshaderinc)。
- 检查：[Checks.cs](../scripts/city/Checks.cs)、[Contracts.cs](../scripts/city/Contracts.cs)、[TerrainChecks.cs](../scripts/terrain/TerrainChecks.cs)。
