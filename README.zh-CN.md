# Custom Unity CLI

[![Unity](https://img.shields.io/badge/Unity-2022.3-black.svg?logo=unity)](https://unity.com/releases/editor/whats-new/2022.3.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![PowerShell](https://img.shields.io/badge/PowerShell-5.1%20%2F%207+-5391FE.svg?logo=powershell&logoColor=white)](https://learn.microsoft.com/powershell/)

**面向 Unity Editor 的零配置命令行工具。**

`cucli` 让你从终端、CI 或 AI Agent 驱动 Unity，不需要端口、不需要常驻服务、不需要下载 SDK，
也不需要为每个工程写一行配置。往工程里丢一个目录，整个 Editor 就变成可脚本化的。

> **仅支持 Unity 2022.3。** Unity 6 工程请改用 [Unity CLI](https://docs.unity.com/en-us/unity-cli/unity-cli-reference)。详见 [兼容性](#兼容性--先读这一段)。

```powershell
# 没有安装步骤，没有配置文件，没有要启动的服务。
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 status
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 object create --name Ball --primitive sphere
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 scene get-hierarchy --page-size 50 --format json
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 refresh compile --wait
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 console --level error --tail 20
```

[English README](README.md)

## 目录

- [兼容性](#兼容性--先读这一段)
- [为什么要再做一个 Unity CLI](#为什么要再做一个-unity-cli)
- [能做什么](#能做什么)
- [安装](#安装)
- [文档](#文档)
- [贡献](#贡献)
- [许可](#许可)

---

## 兼容性 —— 先读这一段

**本工具面向 Unity 2022 工程。** 它针对 **Unity 2022.3** 开发与验证；Editor Worker、命令面、
文件协议全部以这一代 Editor API 为前提。

> ### Unity 6 请用 Unity 官方的 CLI
>
> **本插件暂时不面向 Unity 6，这是范围划分，不是遗漏。**
>
> 如果你的工程在 Unity 6 上，请使用 **Unity CLI** —— Unity 为那一代提供的官方命令行接口。那才是
> Unity 6 上受支持的路径：它与 Editor 同步维护，而且根本不需要一个桥接包。
>
> - [Unity CLI reference](https://docs.unity.com/en-us/unity-cli/unity-cli-reference)
> - [Meet the Unity CLI](https://unity.com/blog/meet-the-unity-cli)
> - [安装与上手 Unity CLI](https://unity.com/cn/resources/unity-pipeline-cli-technical-walkthrough)
>
> 本设计**没有考虑** Unity 6 的使用情况。文件队列传输、Domain Reload 契约与命令面都只在
> 2022.3 上构建和测试过，不应假定它们在 Unity 6 上可用。
>
> 低于 2022 的版本同样如此：2021.3 LTS 未经测试。**请把 2022.3 当作受支持的目标**，
> 其余版本一律视为不受支持。

有一处容易读起来自相矛盾：少数命令实现里带了前向兼容守卫 —— 比如 `PhysicMaterial` / `PhysicsMaterial`
的别名，以及 Unity 6 Build Profile 的反射分支。它们存在的唯一目的是：万一有人在更高版本的 Editor 里
打开这个包，程序集仍然**能编译**。这不是支持声明，这些版本上的行为一律未经测试。

正因为"能编译"不等于"受支持"，这条边界是**被强制执行**的，而不只是写在文档里：Worker 首次加载时
会检查当前 Editor 版本，只要不是 2022.3，就会在 Console 打一条警告，写明实际版本并指向 Unity CLI。
包一导入你就能看到，所以不会有人在 Unity 6 上装完、把沉默当成绿灯。

## 为什么要再做一个 Unity CLI

现在做 Unity 自动化基本只有三条路：带端口、还要给客户端接线的 MCP 服务器；每个任务单独写一个
`-executeMethod` 批处理脚本；或者一堆只有人能点的 Editor 菜单项。三者都能用，代价是每换一台机器、
每开一个新工程都要重新搭一遍。

`cucli` 换了一种传输方式：**一个装满 JSON 文件的目录**。

```
<工程>/Library/CustomUnityCli/
├── worker/heartbeat.json          # Editor 每秒发布存活状态与当前状态
├── worker/commands.json           # 当前 Editor 里真实编译出来的命令目录
├── console/entries.ndjson         # 抓取的 Console 输出，Unity 忙的时候也能读
└── jobs/
    ├── control/                   # 允许绕过"Editor 空闲"闸门的作业
    ├── queued/   running/   waiting/
    ├── succeeded/ failed/ cancelled/
    └── cancel-requests/
```

CLI 写一个作业文件；Editor Worker 在下一个 update tick 取走执行，再写回结果文件。
下面这些性质几乎都是这一个决定的副产物。

| 性质 | 为什么文件队列天然具备 |
| --- | --- |
| **没有端口、没有服务** | 没有东西需要 bind，也就不存在防火墙、端口冲突和残留进程。 |
| **能扛过 Domain Reload** | 作业可以停在 `waiting/` 跨越脚本重编译，在重载之后完成。socket 做不到这件事。 |
| **不会把 Editor 卡死** | 一条极小的第二队列（`control/`）专门承载必须在 Editor 编译期间也执行的少数命令，这是解开 Play Mode 与编译互锁的关键。 |
| **Unity 忙时也能用** | Console 与作业状态都是文件，`cucli console`、`cucli job list` 从不等 Editor。 |
| **可审计** | 每个请求和结果都是带时间戳的文件，能读、能 diff、能留档。 |
| **崩溃安全** | 所有写入都是同目录原子替换，读写双方永远看不到半个文件。 |

## 能做什么

**32 个 Editor 命令组、417 个 action**，分成 **Core** 稳定契约和 **Extended** 高风险组（仍可调用，但不是默认自测范围），另有 9 个完全不需要 Editor 的离线命令组。
用 `cucli commands list --surface core`（或 `extended`）过滤，
用 `cucli commands describe <group>` 看某一组的参数 Schema。

**离线 —— Unity 关着也能用**

| 命令组 | 用途 |
| --- | --- |
| `project` | CLI 认为工程在哪、需要哪个 Unity 版本。 |
| `status` | Worker 心跳、队列深度、命令目录来源。 |
| `doctor` | 诊断工程、包、Worker、Unity 安装与 registry 漂移；`--deep` 会跑一次完整往返。 |
| `logs` | tail `log.txt`、CLI 日志或抓取的 Console。 |
| `commands` | 列出、描述、重新导出命令目录。 |
| `job` | 列出、查看、等待、取消、清理队列作业。 |
| `open` | 拉起本工程的 Unity（优先复用已开的 Unity Hub 实例）。 |
| `version` | CLI、包、PowerShell 版本。 |

**Editor Core —— 稳定契约，默认自测**

| 命令组 | 覆盖内容 |
| --- | --- |
| `editor` | 状态、Play Mode、菜单项、撤销重做、当前工具、Tag、Layer、选中集。 |
| `refresh` | 资产刷新、脚本编译（可跨重载等待）、重新导入、等待空闲。 |
| `scene` | 场景新建/加载/保存/关闭、层级导出、构建设置、截图。 |
| `object` | GameObject 与组件的查找/新建/修改/删除/复制；属性读写走反射或 SerializedProperty。 |
| `prefab` | 预制体创建/检查/修改、Prefab Stage。 |
| `asset` | 资产导入/创建/修改/删除/移动/复制、依赖搜索、目录操作。 |
| `script` | C# 文件读写与校验、精确文本编辑、内容哈希。 |
| `so` | ScriptableObject 创建与修改，支持 `dryRun` 校验。 |
| `material`、`shader`、`texture` | 材质属性、Shader 源码、程序化贴图与导入设置。 |
| `console` | 按级别、正则、时间读取与清空 Console。 |
| `capture` | Game 视图、Scene 视图、多视图截图。 |
| `test` | 以异步作业运行 Unity Test Framework 套件并轮询结果。 |
| `meta` | 导出运行期目录、生成静态 registry、报告环境、脚手架项目专属命令。 |
| `selftest` | 工具自测：内省断言、action 安全性面、示例报告。 |

**Editor Extended —— 仍可调用；`cucli selftest -IncludeExtended` 才会默认探测**

| 命令组 | 覆盖内容 |
| --- | --- |
| `ui` | UI Toolkit 文档、元素、内联样式与 USS 类。 |
| `graphics` | 环境、天空盒、雾、反射、太阳、光照烘焙、质量等级、URP 管线设置与 Volume。 |
| `camera` | 相机检查与配置；装了 Cinemachine 时含 Cinemachine。 |
| `animation` | 动画片段、曲线、AnimatorController、层、参数、预设。 |
| `physics` | 设置、碰撞矩阵、刚体、力、物理材质、关节、查询、单步模拟。 |
| `probuilder` | ProBuilder 网格操作（需 `com.unity.probuilder`）。 |
| `vfx` | VFX Graph、粒子系统、拖尾、线段（需 `com.unity.visualeffectgraph`）。 |
| `profiler` | Profiler 会话、采样、计数器、对象内存、导出。 |
| `memory` | 资产引用与对象内存审计。 |
| `frame` | 单帧检查与连续多帧抓取。 |
| `input` | 向 Editor 合成点击、拖拽、按键；检查光标下的 UI。 |
| `recorder` | 截图与录制会话。 |
| `build` | 以异步作业执行 Player 构建、构建目标、场景、设置。 |
| `package` | Package Manager 增删、嵌入、搜索、scoped registry。 |
| `code` | 执行 C# 片段，以及一次往返批量执行多个操作。 |
| `reflect` | 运行期查询 Unity 类型、成员与签名。 |

## 安装

仓库根目录**就是**包根目录，所以两种装法都行。

> **先确认目标版本。** 本包支持 **Unity 2022.3**。如果你的工程在 **Unity 6** 上，请改用
> [Unity CLI](https://docs.unity.com/en-us/unity-cli/unity-cli-reference) —— 详见本文开头的
> **兼容性**一节。

**作为 plugin 目录（推荐 —— 一个目录，不动 manifest）：**

```powershell
git clone https://github.com/MlsMoon/custom-unity-cli.git Assets/Plugins/CustomUnityCli
```

**作为 UPM 包**（加进 `Packages/manifest.json`）：

```json
{
  "dependencies": {
    "com.igp.custom-unity-cli": "https://github.com/MlsMoon/custom-unity-cli.git"
  }
}
```

然后开一次 Unity。Editor Worker 会通过 `[InitializeOnLoad]` 自动启动，一秒内写出第一个心跳。验证：

```powershell
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 doctor --deep
```

### 环境要求

- **Unity 2022.3** —— 受支持且已验证的目标版本。Unity 6 暂不在范围内，请改用
  [Unity CLI](https://docs.unity.com/en-us/unity-cli/unity-cli-reference)；2021.3 LTS 未经测试。
- `com.unity.nuget.newtonsoft-json` —— 已声明为包依赖，会自动装上。
- PowerShell 5.1 或 PowerShell 7+。**仅此而已**：不需要 .NET SDK、不需要 Node、不需要 Python、
  不需要 MCP 客户端。
- 可选包在运行期探测。ProBuilder、VFX Graph、Cinemachine、Test Framework 只有用到对应命令时才需要；
  其余功能在没有它们的工程里照样编译、照样运行。

## 日常用法

```powershell
# 现在什么情况？
./cucli.ps1 status
./cucli.ps1 editor state

# 在场景里造点东西。
./cucli.ps1 object create --name Ball --primitive sphere --position 0,1,0
./cucli.ps1 object add-component --target Ball --component Rigidbody
./cucli.ps1 object set-property --target Ball --component Rigidbody --property mass --value 2.5

# 检查，但不要刷爆终端。
./cucli.ps1 object inspect --target Ball --max-depth 2 --max-items 20
./cucli.ps1 scene get-hierarchy --page-size 50 --format json

# 强制重编译并等待，跨越 Domain Reload。
./cucli.ps1 refresh compile --wait --timeout 300

# 长操作变成可观察的作业。
./cucli.ps1 build build --target StandaloneWindows64 --detach
./cucli.ps1 job wait <jobId>

# 只看错误，最新 20 条。
./cucli.ps1 console --level error --tail 20
```

### 输出与退出码

`--format human|json|ndjson`。human 是紧凑可 grep 的摘要；json 返回 `cucli.result/v1` 信封：

```json
{
  "schemaVersion": "cucli.result/v1",
  "success": true,
  "command": "object",
  "action": "create",
  "requestId": "…",
  "jobId": "…",
  "data": { "…": "…" },
  "errors": [],
  "warnings": [],
  "artifacts": [],
  "durationMs": 42,
  "exitCode": 0
}
```

输出被重定向到管道时，格式自动切到 `json`。

| 退出码 | 含义 |
| --- | --- |
| `0` | 成功 |
| `1` | 未分类的主机或命令错误 |
| `2` | 用法错误 |
| `4` | 找不到工程、缺可选包，或需要确认 |
| `6` | 操作、校验或超时失败 |
| `7` | Editor / Worker 不可用 —— 可重试 |
| `8` | 输入非法，或命令/动作不存在 |
| `130` | 已取消 |

判断结果时**同时**看进程退出码和 `success`。长操作保留 `jobId`，之后可以回查结果。

## 验证与自测

本仓库的 Agent 维护说明见 [`Skills~/cucli-develop-plugin/SKILL.md`](Skills~/cucli-develop-plugin/SKILL.md)。
宿主集成见 [`Skills~/cucli-use-plugin/SKILL.md`](Skills~/cucli-use-plugin/SKILL.md)。入口是 [`AGENTS.md`](AGENTS.md)。
**Git 提交说明只用英文**（Conventional Commits：`type(scope): imperative summary`），不要写中文 commit message。

两个工具回答两个不同的问题，都不需要关掉你的 Editor。

```powershell
# 构建门：用 Unity 自带 Roslyn + 官方引用程序集编译 Editor 源码，几秒出结果
Tools/verify.ps1
Tools/verify.ps1 -WithUnity   # 额外把一个丢弃式工程导入一遍（该工程不声明任何可选包）

# 行为门：黑盒自测，全程通过 CLI 驱动 CLI
cucli selftest                          # 契约探针 + 行为流
cucli selftest -Tier AB                 # 契约探针 + 行为流
cucli selftest -Group object,asset -Action object/create
```

`verify.ps1` 是**构建**门。它用 Unity 自己的 Roslyn 对着 Unity 自己的引用程序集编译 Editor 源码，
所以能在几秒内抓到真实类型错误，且不开 Editor 进程、不抢工程锁、不触发 Domain Reload。
`-WithUnity` 会把包导入一个 manifest 里**没有任何可选包**的丢弃式工程 —— 这让「不依赖任何可选包」
从一句承诺变成一次实测。两种模式都不会启动、聚焦或触碰正在运行的 Editor。

`cucli selftest` 是**行为**门，也是「这东西到底能不能用」的答案。它什么都不伸手去拿：从在线 worker
目录读取命令面，然后**只通过 `Tools/cucli.ps1`** 与 Editor 对话。因此一次全绿证明的是整条链路 ——
PowerShell 解析器 → 作业文件 → Editor worker → 命令 → 结果文件 → 信封 → 退出码。

| 层 | 做什么 |
| --- | --- |
| A | 契约探针。对每个**只读** action 无参调用一次。接线正确的命令要么给出真实载荷，要么给出参数校验错误；未知命令、分派错位或未捕获异常都算缺陷。这一层专门抓注册表漂移、Schema 缺口和模式门 bug，覆盖面是全量命令面。 |
| B | 行为。在 `Assets/CucliSelfTest` 下建沙箱、用真实载荷跑真实流程，然后删掉沙箱。 |
| heavy | 需显式 `-IncludeHeavy`：编译、包解析、测试运行。**永远不会**做真实出包、光照烘焙或写 manifest。 |

报告落在 `Library/CustomUnityCli/selftest-report.md`；`-KeepSandbox` 会把沙箱留下来供排查。
每个已声明的 action 都恰好归属一层，所以「全部都测过了」这句话随着命令面增长仍然成立。

> 注：A 层对**只读** action 无参调用是安全的；会改变 Editor 或工程状态的 action（undo、play、
> compile、clear 等）由各层显式声明，不会在契约探针里被误触。

## 本工具遵循的设计规则

1. **不为工程增加配置。** 别名、离线命令组和安全元数据都随 `commands/registry.json` 一起发布，
   其余全部从编译出的 Editor 里自动发现。没有设置窗口，也没有 `.cuclirc` 要写。
2. **配置只写在静态文件里。** `commands/registry.json` 由 `cucli commands export` 从编译产物生成，
   离线目录因此不可能悄悄漂移 —— `cucli doctor` 会比对两个哈希并告诉你何时不一致。
3. **默认输出有界。** 所有列表都分页（`pageSize` ≤ 500，游标 `nextCursor` 不透明），所有对象导出都
   限制深度与条目数。原始 dump 一个 `TextMeshProUGUI` 或 `Mesh` 会输出几兆字节；本工具拒绝这样做。
4. **可选就是可选。** ProBuilder、VFX Graph、Cinemachine、Test Framework 一律走运行期反射，
   核心程序集不引用任何可选包，因此不可能弄坏一个没装这些包的工程。缺包时返回
   `OPTIONAL_PACKAGE_MISSING` 并附安装提示，而不是编译错误。
5. **写入行为与 Editor 一致。** 属性写入优先走 `SerializedObject`，因此会登记 Undo、变成预制体覆盖、
   并把资产标脏；反射只是退路。
6. **破坏性操作需要显式同意。** 删除、覆盖、清空集合都要求 `--force`，否则返回 `CONFIRMATION_REQUIRED`。
7. **失败可机器判定。** 每个失败都带稳定 `errorCode`，CLI 退出码由它推导，
   所以 `if ($LASTEXITCODE -eq 7) { retry }` 是安全的。

## 扩展

写一个带公共无参构造函数的类就多了一组命令：

```csharp
namespace CustomUnityCli.Editor
{
    internal sealed class HelloCommand : CucliCommand
    {
        public override string Name => "hello";
        public override string Summary => "从 Editor 里问好。";
        public override CucliActionInfo[] Actions => Declare("say", "返回一句问候。");
        public override bool MutatesProject => false;

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string who = context.Input.GetString("who") ?? "world";
            return CucliCommandResult.Ok(new { greeting = "hello " + who });
        }
    }
}
```

注册表会在下次 Domain Reload 时靠反射发现它，发布到 `worker/commands.json`，
然后 `cucli hello say --who unity` 立刻可用。不用再注册别的东西，不用改 CLI，不用改 manifest。

完整契约见 [`docs~/extension.md`](docs~/extension.md)，文件协议见 [`docs~/protocol.md`](docs~/protocol.md)。

### 按项目自我迭代 —— 但永远不会被发布出去

项目总会长出一些只对自己有意义的命令：资产命名检查、内容校验、构建步骤、团队反复解释的约定。
`cucli` 就是按这种生长方式设计的，并且保证这些代码**不可能进入本仓库**。

扩展区是**项目里**的一个目录，位于包之外：

```text
<工程>/Assets/Plugins/CustomUnityCli.Extensions/
├── extensions.json           项目清单：别名与离线命令组
├── registry.local.json       生成物：本项目自己的命令目录
├── README.md                 向发现这个目录的人解释它是什么
├── Notes/                    项目约定，agent 可以据此重新发现上下文
└── Editor/                   必须叫 Editor，否则 Unity 不会编译它
    └── Commands/             项目自己的命令组放这里
```

`Editor/` 这一层不是装饰。位于名为 `Editor` 的目录下的源码会编译进预定义的
`Assembly-CSharp-Editor`，而它本来就引用了本包的程序集 —— 所以既不用写 asmdef，也不用接引用。
同一个文件放在 `Editor` 之外就会落进运行时程序集，那里根本看不到 `UnityEditor`。

生成一组命令、让它编译、直接用：

```powershell
./cucli.ps1 meta new-command --group assetpolicy --summary "本项目的资产命名与目录约定" --action check --action fix
./cucli.ps1 refresh compile --wait
./cucli.ps1 console --level error --tail 20
./cucli.ps1 assetpolicy check
```

不需要注册任何东西，也不需要写任何配置文件。`cucli extend path` 打印解析出的根目录；
`cucli extend init` 在 **Unity 关着**的情况下创建这套目录；`$env:CUCLI_EXTENSIONS` 可以改位置，
首选目录不存在时还会依次发现几个备选目录名。

**仓库边界是强制保证的，不只是写在文档里：**

| 机制 | 保证什么 |
| --- | --- |
| 来源标记 | 每条命令都带 `source: package` 或 `source: project`，由它被编译进哪个程序集推导。`cucli commands list` 会显示。 |
| `cucli commands export` | **只把 package 命令**写进 `commands/registry.json`。project 命令改写进扩展区旁边的 `registry.local.json`，那属于项目自己的仓库。 |
| 两个目录哈希 | `catalogHash` 只覆盖 package 命令，`fullCatalogHash` 覆盖全部。因此项目长了自己的命令之后，漂移检测依然有效。 |
| `cucli meta verify` | 校验扩展区、逐条比对命令的 action 列表与它声明的 JSON Schema，并报告任何被误放进**包目录内部**的源文件 —— 那是唯一会泄漏的摆放方式。 |
| `cucli meta extensions` | 对账报告：哪些组来自包、哪些来自项目、本地 registry 是否过期。 |
| `cucli doctor` | 增加 `extensions.placement`、`extensions.group-conflicts`、`package.checkout`，让泄漏在日常体检里就被抓到，而不是等到评审。 |
| 不许 shadow | 项目扩展不得复用包已发布的组名，冲突会被拒绝并报告。只能**增加**（`buildchecks`、`assetrules`），不能重定义 `object` / `asset`。 |
| `cucli extend init\|list\|path` | **Unity 关着也能**创建与检查扩展区，所以项目在第一次打开 Editor 之前就能开始。 |

包目录是 git 检出时，`package.checkout` 会在包根跑 `git status --porcelain`，因此"有人就地改了已发布
产物"这件事会被报告出来，而不是等到下次合并冲突才发现。

预期的工作方式是：项目自由迭代自己的命令；当某条命令被证明到处都有用时，再把它作为一次**被审阅的
变更**主动上游，而不是让它在跑命令时顺手溜进去。完整安排（包括让下一轮迭代能接着上一轮所学继续的
`Notes/` 目录）见 [`docs~/self-iteration.md`](docs~/self-iteration.md)。

## 目录结构

```text
Assets/Plugins/CustomUnityCli/          仓库根 == 包根
├── package.json                        UPM 元数据
├── commands/registry.json              静态命令声明（离线目录 + 别名）
├── Editor/
│   ├── CustomUnityCli.Editor.asmdef
│   ├── Core/                           协议、作业队列、Console 抓取、反射工具
│   └── Commands/                       命令组，一组一个文件
├── Tools/cucli.ps1                     整个 CLI：参数解析、离线命令、作业传输
├── .mlsmoon/                           Moon Game Dev Tool Manager 路由 Skill
├── Skills~/                            Agent skill（以 ~ 结尾，Unity 不会导入）
├── AGENTS.md                           Agent 入口
└── docs~/                              包文档（以 ~ 结尾的目录 Unity 不会导入）
```

运行期状态只写在 `Library/CustomUnityCli/`。不会回写包目录，因此这个目录始终保持只读可升级，
删掉 `Library/CustomUnityCli/` 永远安全。

## 文档

- [`docs~/protocol.md`](docs~/protocol.md) —— 完整的文件协议：作业生命周期、Domain Reload 契约、
  取消、原子写入。
- [`docs~/extension.md`](docs~/extension.md) —— 如何写一个命令组。
- [`docs~/self-iteration.md`](docs~/self-iteration.md) —— 项目如何在**不被发布出去**的前提下长出
  自己的命令。
- [`docs~/mcp-parity.md`](docs~/mcp-parity.md) —— 与 MCP for Unity 的逐工具对照，以及刻意保留的差异。
- [`CHANGELOG.md`](CHANGELOG.md)

## 贡献

见 [CONTRIBUTING.md](CONTRIBUTING.md)。提交说明只用英文 Conventional Commits。包文档放在 `docs~`，
Unity 不会把这些 markdown 当资产导入。

## 许可

[MIT](LICENSE)。Copyright (c) 2026 [MlsMoon](https://github.com/MlsMoon)。
