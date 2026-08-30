# ADR-0029：垂直切片 Outpost —— 13 模块整合验收与接缝发现清单

**Status:** Accepted（2026-07-11）

## Context

框架 13 个正交模块（Context/Command/Model/System/Utility/View、资源、配置表、UI、存储、音频、Flow、本地化、字体、池、列表绑定、网络、热更构建）各自有 demo 教学章验证，但**从未被同一个游戏同时消费**。垂直切片的三重目标（2026-07-06 批准，计划细节见 `Assets/Game/Outpost/Documentation~/outpost-guide.md`）：

1. **整合验收**：一个小而完整的游戏串起全部模块，暴露模块间接缝问题——本清单即核心交付物，是 UPM 抽包前的验收依据。
2. **消费边界验证**：切片以真实业务方身份从框架**外部**消费（独立 asmdef），验证 AGENTS 使用规则、服务注册生成、包名常量在业务侧的真实手感。
3. **DOTS 融合验证**（M6）：战斗模拟藏在纯 C# 接缝后，后段把 OOP 后端置换为 ECS 后端——同一套 Command/Model/View 不动只换模拟内核（roadmap Phase 3 的正确验证姿势）。

游戏定义：塔式生存自动战斗（近防炮拦截来袭弹、无限波次、托管/撤离、波间三选一升级），代号 **Outpost**。美术全几何体 + 程序网格 + 程序合成音频，零外部资产。

## Decision：切片结构与关键选择

| 决策 | 内容 | 验证的框架面 |
|---|---|---|
| 程序集 | `Game.Outpost`（游戏主体，热更）+ `Game.Outpost.Sim`（模拟接缝，纯 C# 零依赖，**永保 AOT**） | asmdef 消费边界、热更档位部署决策 |
| 模拟接缝 | `IBattleSim` contracts + `ReferenceBattleSim` 参考后端；表现（实例化渲染/池化特效/音频）全在 director 的事件翻译层 | ports & adapters；「纯 C# 接缝 = AI 可跑数调平衡」实证 |
| 根 Context | `OutpostContext : MonoGlobalContext` 场景驱动（该基类首个真实消费者）；战斗附加场景 `BattleContext` 子上下文靠 `inheritFromGlobal` 回退拿全局服务 | 作用域树、场景生命周期 |
| UI | 窗口栈全 Toolkit（uxml+uss，**uxml 窗口加载分支首次实战**）；战斗 HUD/飘字走 UGUI 场景内视图 | 同一 Context 单 UI 入口下的双后端混用 |
| 配置/存档/音频/本地化 | Luban 六表 + `outpost/*` 整存整取 + 程序合成 wav + zh/en 双语 | §16/§18/§19/§21 全消费 |
| 网络 | 进程内 dev server（HttpListener + TcpListener/RFC6455），Protobuf 全程对讲 + WS 二进制推送 | ADR-0028 双轨 + 序列化接缝 |
| 启动链（M5） | `GameEntry.Enter` 代码引导：`MonoGameContextBase`+`AssetUtility` 双 AddComponent 搭最小资源栈 → `Configure/Initialize/LoadScene` → Destroy 交棒；编辑器旁路 EditorSimulate、玩家包 Host | ADR-0008 入口约定的首次真实编排 |
| 热更档位（M5） | 热更 9 程序集（框架 7 + Game.Main + Game.Outpost，自动拓扑序）；Sim 留 AOT（M6 ECS 只依赖它） | 「热更引用 AOT」方向合法性、link.xml 裁剪保护 |
| 分包（M5） | `DefaultPackage`（全内置首包）+ `OutpostExpansionPackage`（清单内置、内容 CDN；不自动初始化 + 关按需下载 = 显式下载器 DLC 姿势；消费点=设置窗下载区 + 战斗 BGM 变体「增援电台」） | 多包、按需下载、包名常量、`ExpansionInstalled` 启动复原样板 |
| 正式包网络策略（M5 拍板） | 排行/推送整体隐藏（`OutpostNet.Available` 仅 Editor/DevBuild）——正式对端属「服务端生产化」里程碑（dev server 逻辑移植 ASP.NET Core 上云），届时再开门控 | 部署决策与代码组织解耦 |

## 接缝发现清单（核心交付物）

### A. 框架 bug / 缺口——发现即修（六件）

| # | 里程碑 | 现象 | 根因 | 修复 |
|---|---|---|---|---|
| 1 | M1 | 附加场景卡 90% 永不激活，`OnEnter` 的 await 永不返回 | `YooAssetProvider.LoadSceneAsync` 把框架 `suspendLoad` 直传 YooAsset 的 `allowSceneActivation`（语义相反） | 传 `!suspendLoad`。demo 从未经资源系统 LoadScene，潜伏至今 |
| 2 | M1 | 停 Play 抛 `YooInternalException` | `YooAssetsDriver.OnApplicationQuit` 先 Destroy，之后 Bag 级联 `YooSceneHandle.Unload` 访问已释放系统 | Unload 开头 `if (!YooAssets.IsInitialized) return` |
| 3 | M4 | Protobuf 字节经 WS envelope 后损坏 | envelope 把 payload 做 UTF8 文本二次编码 + 只发文本帧，对二进制格式破坏性 | 可选接缝 `IWebSocketEnvelopeSerializer`（实现即接管 envelope 编解码与帧类型）；JSON 老路径 wire 字节不变 |
| 4 | M5 | 场景配 EditorSimulate 进玩家包 `NotSupportedException`，且编辑器 Play 全程无症状 | 模拟模式分支是 `#if UNITY_EDITOR` 编译的，单一 `_playMode` 字段无法表达「编辑器模拟 + 玩家包 Host」 | `AssetSystemConfigModel` 拆「编辑器 / 玩家包」两模式字段 + `GetConfigError` 启动校验 |
| 5 | M5 | 热更入口在首场景加载前没有代码化资源初始化路径 | `AssetUtility.Configure` 是 internal（当时只设计了场景三组件路径），而 Boot 场景只能挂 AOT 组件 | `Configure` 提升 public；引导栈样板固化在 `GameEntry`（guide §15）；场景路径后由 ADR-0046 收敛为单 Utility |
| 6 | M5 | 玩家包（IL2CPP）从 bundle 加载 uxml 失败：`Should not occur! Internal logic error` | UI Toolkit 各元素嵌套的 `UxmlSerializedData` 只被反序列化引用，UnityLinker 静态分析看不到 → 被裁剪（Unity 6 已知问题）；随包场景无任何 Toolkit 组件的项目（热更档位必然如此）必命中 | `Game.Framework.UI.Toolkit/link.xml` 整体 preserve `UnityEngine.UIElementsModule` |

> 共性：#1/#2/#4/#6 都是「demo 覆盖不到的部署路径」——资源系统加载场景、玩家包运行模式、bundle 化 uxml 全部是切片首次真实走通，这正是垂直切片存在的理由。

### B. 框架增强——切片驱动落地

- **Protobuf 进内核**（M4，用户拍板「通用件进框架」）：手写 wire 原语 `ProtoWriter/ProtoReader` + 注册式 `ProtobufNetworkSerializer`（`Core/Network/`），字节与标准 protobuf 互通；后续「proto 生产化」里程碑将补官方 protoc + `Google.Protobuf` 适配器走真正生产路径。
- **uxml 窗口加载分支**（`[UIWindow(Asset=...)]` 按名寻址）首次实战验证（此前仅纯代码窗覆盖）。

### C. 待议记录——不动手、择期评估（挑真实痛点，防过度设计）

1. **查询束模式官方化？** `BattleReadModel` / `UpgradeChoiceReadModel` / `PlayerRecordReadModel` 三次重复同一手写模式（数据密集 View 的逐值查询命令样板多，业务自发收敛成「一个查询命令返回只读束」）。重复三次仍是 ~20 行/处的浅样板，暂不进框架。
2. **FlowState 拥有窗口的关闭桥接**：`Bag.Add(Disposable.Create(() => ui.Close<T>()))` 手写样板从 M0 用到 M5。可考虑 `Bag.OpenWindow<T>` 之类便捷件，暂record。
3. ~~**`BindLocalizedText` 的刷新信号只有 Locale**~~：**2026-08-24 已在 ADR-0024 v2 收口。**Source 现在区分 Unavailable/Missing/Found 并发 `Invalidated`，Localization 汇总为 `TextRevision`；Outpost 文本绑定无需再由 Boot 硬等配置 Ready，配置后到会在同一语言下自动重取，且不会把加载中误报成缺 key。
4. **嵌套子 GameFlow 未被切片消费**（§28 验收缺口）：波间抉择本质是 director 的暂停相位，硬套子 Flow 属过度设计。该能力的真实验收另择场景（如带内嵌状态机的副本流程）。
5. **下载尺寸暴露**：扩展包下载 UX 想标「下载 X MB」；地址查询现已升级为 `GetLocationState` 四态，但仍只给分类、不提供字节数——`GetDownloadSize` 需求真实存在，按需再加 `IAssetUtility` 尺寸 API（见 ADR-0013）。
6. **构建管线长操作 × 自动化**：Generate/资源构建/玩家包构建都是同步阻塞编辑器的分钟级操作，MCP/CI 侧只能「触发 + 标记文件轮询」。可考虑构建器写进度文件的约定，暂record。

## 验收结果

- **每里程碑**：编译零错 + PlayMode 全量测试全绿 + 编辑器 Play 冒烟零报错 + commit（M0 骨架 → M1 战斗核心 → M2 升级 → M3 存档/音频/本地化 → M4 网络排行 → M5 构建收口）。M5 时点 PlayMode 测试 325/325。
- **M5 玩家包端到端（Windows IL2CPP，非开发包）**：BootScene → HotUpdateLauncher 初始化代码包 → CDN 拉清单 → 按拓扑序加载 9 个热更 DLL → `GameEntry`(v4) 代码引导栈 → DefaultPackage（内置首包）初始化 → **OutpostGame 场景从 bundle 拉起**（含热更程序集脚本的场景首次 bundle 化实跑）→ 标题可玩；正式包下排行入口按策略隐藏。修复 A#6 后 uxml 窗口正常。热更一轮：改 `EntryVersion` → 重打代码包 + 部署 → 玩家包重启经 CDN 拉到新版代码。扩展包内置只有清单、内容真经 CDN 下载。
- **CI 护栏**：关闭本工程的交互式 Editor 后，`Tools/run-tests.ps1` 默认顺序跑 EditMode + PlayMode；也可用 `-TestPlatform PlayMode` 定向回归业务套件。

**2026-08-23 自动化补验收：**新增独立 `Game.Outpost.Smoke.Test`，不录 UI 坐标，直接经稳定业务 Interface 跑真实 OutpostGame/OutpostBattle 场景的“标题 → 战斗就绪 → 撤离 → 结算 → 回标题”。测试以同一父目录内的原子重命名暂存真实存档，结束后原样移回；失败时备份仍完整留存，不走“复制一半后删除原数据”的危险路径。它还验证战斗场景、`BattleContext`、导演与时间倍率无残留；夹具自身设置/恢复 `Application.runInBackground`，可在 Editor 失焦时完成。实测发现并修复：① Additive 隔离误删 `Code-based tests runner` 根节点；② 撤离关闭外部 `IsReady` 时误清内部导演 `_ready`，导致结算倒计时停摆；③ 自然战败与初始化/结算期间，各玩家交互按钮未统一服从 `BattleReadModel.IsReady`；④ Unity Test Framework 的续跑器不能反射 UniTask 自定义 Enumerator，需保留编译器生成的外层协程。2026-08-24 当前项目基线为 PlayMode **422/422**、EditMode **102/102** 全绿。

## Consequences

- **UPM 抽包的前置验收完成**：六处部署路径缺口修完后，「模块在真实游戏 + 真实玩家包下端到端可用」首次成立；抽包时接缝清单 C 组按需处理。
- 切片文档随游戏走（`Assets/Game/Outpost/Documentation~/`）；后续里程碑：M6 DOTS 后端置换（ADR-0030）→ proto 生产化 → 服务端生产化。
