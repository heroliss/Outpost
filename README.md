# 🎮 SSFramework

**SSFramework 是一套面向真实游戏生产与长期迭代的 Unity 模块化框架，也是一套让人类与 AI Agent 在同一工程事实和验证闭环中协作的开发基线。**

运行时以 Context + MVCS（View / Command / System / Model + Event / Utility）组织单向数据流：Command 表达“做什么”，System 承担“怎么做”，接口和程序集边界让常见越权在编译期暴露。生产侧把资源、配置、热更新、UI、存档、音频、网络、构建和诊断拆成可组合 Module，并通过 Unity Inspector、Editor 工作台和可替换接缝保持可观察、可裁剪。

本仓库不只交付运行时代码：它还包含 35 章可运行 Demo、1396 项回归基线、分层项目规则、5 个 Project Skill，以及把编译、测试、运行、截图、性能和构建证据收口起来的 Unity 验证 Harness。Codex 是当前已验证的主要 Agent，但公共真值保存在 `AGENTS.md`、`.agents/skills/`、文档、测试和项目工具中，未来其他 Agent 可以从同一证据继续承接。

---

## 🧭 从当前仓库开始

当前可验证交付物是一套完整 Unity 工程，而不是已经发布到 Registry 的 UPM 包：

1. 使用 **Unity 6000.3.22f1** 打开仓库根目录；先让 Package Manager 和脚本完成导入。
2. 打开 `Assets/Game/Framework/Demo/Scenes/DemoScene.unity`，进入 Play Mode；左侧 35 个自动发现章节覆盖核心概念、常用能力与接入工作流。
3. 从菜单 `SSFramework/工具中心` 进入配置、生成、构建与诊断入口；高风险动作会在按钮与 Implementation 两层校验前置条件。
4. 框架 API 从[用户手册 §3 快速开始](docs/framework-guide.md#3-快速开始)起读；要把另一个 AI 接入本项目，从[接入与 Handoff](docs/ai-agent-onboarding.md)起读。
5. 交互式验证先运行 Unity Test Runner；工程外完整回归需先关闭 Editor，再执行 `Tools/run-tests.ps1`。PlayMode 的 MCP 流程见[Unity MCP 项目要点](docs/unity-mcp-tips.md)。

第一次开发真实游戏时，建议先在 `Assets/Game/<GameName>/` 建独立业务程序集并只消费 Framework 公共 API，不复制框架源码。只有实战证明是跨游戏通用缺口时才回补 Framework；UPM 抽包留到第二个真实消费方能验证安装、删除与依赖边界之后。

---

## ✨ 核心特点

| 特点 | 一句话说明 |
|---|---|
| **单向数据流** | View 只观察、不写入；状态变化统一经 Command，简单操作直达 Model，复杂规则委托 System |
| **类型驱动** | 事件、命令、服务都用类型区分，避免字符串和枚举的脆弱标识 |
| **Hierarchy 原生** | Context 父子关系直接用 GameObject 层级表达，拖动节点即可重组依赖图 |
| **多 Context 嵌套** | 子 Context 自动继承父级服务，平行 Context 完全隔离——天然适合多场景、多模块、Mock 测试 |
| **Mono / 纯代码双路径** | 业务可以挂节点（Inspector 可见、可调）也可以纯 C#（测试友好、不依赖场景） |
| **零分配 Command** | `readonly struct` + 双泛型重载，高频命令零 GC 压力 |
| **可插拔命令分发器** | `ICommandSystem` 是历史命名的基础设施契约（不是五层 `ISystem`）；替换默认实现即可一处拦截全部命令——日志、回放、撤销/重做、优先级队列、自动化测试都能在此承载 |
| **响应式数据流统一** | 事件、属性、UniTask、协程、UnityEvent、C# event 均可互转为 `Observable<T>`；状态对 View 返回 `ReadOnlyReactiveProperty<T>` 等只读类型 |
| **自动生命周期管理** | `DisposableBag`（`Bag`）统一登记订阅 / 资源句柄 / 池租借，`OnDestroy` 时一并清理，无需手动维护 |
| **异步取消传导** | Context Dispose 级联取消相关异步操作；Mono View 无参调用自动绑定 destroy token，纯 C# View 用 Bag / host token 明确界面生命周期 |
| **可删除的模块边界** | 19 个生产程序集把 UI 后端、YooAsset、HybridCLR、Luban、字体与 Protobuf 等能力隔离；主要可选 Module 有独立测试和删除边界 |
| **AI 可承接开发** | 分层 `AGENTS.md`、开放 Project Skill、领域文档与 Handoff 证据包共同减少隐式记忆和客户端锁定 |
| **证据驱动 Harness** | 按风险编排编译、EditMode / PlayMode、玩家路径、截图、性能和隔离构建；明确区分“测试通过”与“体验成立” |

---

## 🤖 AI 协作与验证 Harness

这里的 Harness 不是第二套 Unity Test Runner，也不是“让 AI 自动做一切”的宣传词。它是围绕真实任务建立的执行闭环：

```text
目标与改动
  → 就近项目规则 / 按需 Project Skill
  → Unity MCP、Editor Seam 或 CLI 执行
  → 编译、测试、运行、截图、性能或构建证据
  → 失败恢复、清理与可复核交付摘要
```

| 组成 | 当前已落地 |
|---|---|
| **共享项目真值** | 根目录与业务 / Framework / Demo 分层规则，CONTEXT、ADR、guide 和测试共同描述当前契约 |
| **5 个 Project Skill** | 架构深化、规则演进、Unity 后台自动化、截图取证、按风险选择验证范围 |
| **Unity 执行层** | AnkleBreaker MCP 负责带队列与 Undo 的 Editor 操作；项目 CLI Adapter 负责工程外测试和构建 |
| **可观察 Oracle** | 1396 项测试、真实玩家路径、Demo 运行、截图检查、隔离构建与逐步增加的性能 / 视觉基线 |
| **跨 Agent 承接** | 当前以 Codex 为主，其他 Agent 从公共 Markdown、项目工具和 Handoff 清单做能力探针，不复制规则正文 |

AI 可以提高实现、搜索、验证和重复生产的吞吐量，但不能仅凭绿灯或单帧截图证明游戏好玩、审美成立或具备市场。完整设计边界见 [AI 协作方案](docs/ai-collaboration-guide.md)，长期能力补全见 [AI 游戏开发能力图谱](docs/ai-game-development-capability-map.md)。

---

## 📐 架构一览

框架由五层组成——**View / Command / System / Model + Event / Utility**，外加 Context（容器，内部承载 DI Container 与 Event Bus）。业务代码通过统一接口操作，不直接碰底层容器。

本文和工具统一采用“中文职责 + 可检索英文术语”的写法：上下文（Context）表示能力环境；模块（Module）表示一组 Interface 与 Implementation；接缝（Seam）是可替换行为所在的位置；适配器（Adapter）是接在 Seam 上的具体实现；配置资产（Profile）是可入库的项目配置。特别注意，UPM 包（Package）是源码安装/版本边界，YooAsset 资源包（Resource Package）是运行时资源发布边界，两者不是一回事。C# 类型、程序集名和命令参数始终保留原文，便于从中文说明直接检索代码。

![SSFramework 五层架构图](docs/SSFramework-architecture.png)

图上几条关键信息：

- **写入是单向链路** —— View 任何状态改动都先经过 Command；简单原子操作可 `Command → Model`，复杂或可复用规则走 `Command → System → Model`，View 自身不直接写
- **读取也经过 Command** —— `ReactiveProperty` 持续推送当前值（给 UI 文本订阅）、`Event` 瞬时通知一次（给动画 / 音效）；View 通过只读查询 Command 取得订阅源，两条响应路径互不知道对方存在
- **MonoBehaviour / Rigidbody 正交于五层** —— 任意层都可以继承 MonoBehaviour 拿到 Inspector 序列化与 Unity 生命周期，引擎能力不影响架构定位
- **权限由接口编译期约束** —— `IModel` 不能调用 `ISystem`、`View` 不能写 `Model`，越界默认编译报错；`[Inject]` 注入在注入期做同源校验。这是**防误用**的类型约束（刻意绕过——如强转 Context——仍然可行），目标是让"顺手写错"变得困难、让越界必须显式可见，而非运行时沙箱

---

## 🚀 极简示例

```csharp
// Model —— 持有数据，RP<T> 可订阅、Inspector 可见、覆盖任意类型（using R3;）
public class PlayerModel : MonoModelBase
{
    [field: SerializeField] public RP<int> HP { get; private set; } = new(100);
}

// System —— 可复用业务规则的主要写入者；RP<T> IS-A ReadOnlyReactiveProperty<T>，直接赋值，零分配
public interface IPlayerSystem : ISystem
{
    ReadOnlyReactiveProperty<int> HP { get; }   // 只读响应式状态，View 经查询 Command 订阅
    void TakeDamage(int amount);
}

public class PlayerSystem : MonoSystemBase, IPlayerSystem
{
    [Inject] private PlayerModel _model;

    ReadOnlyReactiveProperty<int> IPlayerSystem.HP => _model.HP;   // RP<int> 直接赋给 ReadOnlyReactiveProperty<int>，无需转换

    public void TakeDamage(int amount)
    {
        _model.HP.Value -= amount;
        this.SendEvent(new PlayerHurtEvent(amount));
    }
}

// Command —— 封装"做什么"，连接 View 和 System
public readonly struct TakeDamageCommand : ICommand
{
    public void Execute(ICommandContext ctx) => ctx.GetSystem<IPlayerSystem>().TakeDamage(10);
}

public readonly struct GetHPStateCommand : ICommand<ReadOnlyReactiveProperty<int>>
{
    public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx) => ctx.GetSystem<IPlayerSystem>().HP;
}
// 自定义类型同理：RP<PlayerData> 在 Model，ReadOnlyReactiveProperty<PlayerData> 在 System 接口

// View —— 只发 Command，不直接获取 Model/System/EventBus
public class HudView : MonoViewBase
{
    protected override void Awake()
    {
        base.Awake();
        var hp = this.ExecuteCommand(new GetHPStateCommand());
        Bag.Subscribe(hp, value => _hpText.text = value.ToString());  // 订阅即得 current value
        Bag.Subscribe<PlayerHurtEvent>(_ => PlayHurtAnim());
        Bag.Subscribe(_btn.onClick, () => this.ExecuteCommand(new TakeDamageCommand()));
    }
}
```

点击按钮 → Command → System.TakeDamage → Model.HP 变化 → View 响应式刷新 + 受击动画播放。两条响应路径完全解耦，互不知道对方存在。

---

## 💡 设计理念

- **把 Controller 拆开**：System（怎么做）+ Command（做什么）+ 接口约束 = 不会膨胀的"控制层"
- **重新理解数据**：可观察 × 有状态的二维分类——`ReactiveProperty` 和 `Event` 同属数据层，只是生命周期不同
- **一切皆数据流**：响应式属性、事件、异步、协程都是"在时间线上产生值"，统一为 `Observable<T>` 后用 LINQ 风格操作符自由组合
- **类型即标识**：类本身就是一种类型，能用类型表达的就不要用字符串或枚举
- **Hierarchy 即依赖图**：拖动节点改变继承关系，Inspector 实时看 Model 状态，调试不再靠日志推断

> 详细阐述见用户手册 [§1 框架理念](docs/framework-guide.md#1-框架理念)

---

## 🧪 可验证工程基线

截至 **2026-09-01 Framework Baseline**，以下证据已在当前仓库完成；最新命令、日志与边界见[持续完善计划](docs/project-improvement-plan.md#已验证基线2026-09-01)：

| 证据 | 当前基线 |
|---|---|
| **Unity 编译** | Unity 6000.3.22f1，0 error / 0 warning |
| **EditMode** | 622 / 622，通过纯 C# 契约、Editor 工具、生成管线和模块边界 |
| **PlayMode** | 774 / 774，通过生命周期、UI / 资源 / 网络组合、Demo 和真实玩家路径 |
| **总计** | **1396 / 1396**；测试集合必须非空，不能把错误筛选当成绿灯 |
| **运行与视觉** | 35 章 Demo 可运行；关键 Game View / EditorWindow 截图需实际检查，不以“截图成功”代替视觉结论 |
| **工程外自动化** | `Tools/run-tests.ps1` 通过 ProjectVersion 驱动 Unity CLI / Direct Adapter，并保留 NUnit XML 与 Editor 日志 |

维护纪律同样是可审查的工程契约：

- **文档与代码同源**：README（门面）→ 用户手册（用法）→ ADR（取舍）→ 就近 `AGENTS.md`（协作边界）；只同步真正受影响的层，不制造机械文档噪音。
- **权限双保险**：编译期 `ICanXxx` 接口与 `[Inject]` 注入期同源校验共用一套权限模型，让越界必须显式可见。
- **生命周期按失败设计**：取消、池租借、资源句柄、订阅和子 Context 都有明确 owner、幂等释放与失败后清理证据。
- **验证与风险相称**：局部纯函数不必跑完整 Player Build；公共架构、场景行为或发布链路则不能只凭一个单测结论交付。

---

## 🛠 技术栈

构建在以下成熟依赖与可选工具之上：

| 库 | 用途 | 在框架中的角色 |
|---|---|---|
| [UniTask](https://github.com/Cysharp/UniTask) | 零分配异步 | Async Command、取消令牌传导、与协程互转 |
| [R3](https://github.com/Cysharp/R3) | 响应式编程 | `ReactiveProperty`、Observable 操作符（`DisposableBag` 底层复用其 `CompositeDisposable`） |
| [YooAsset](https://github.com/tuyoogame/YooAsset) | 资源 provider | 当前默认 provider 实现（经 `IAssetProvider` 隔离，可整体替换） |
| [HybridCLR](https://github.com/focus-creative-games/hybridclr) | 代码热更 | 列表驱动的热更范围 + Boot 引导（ADR-0008） |
| [Luban](https://github.com/focus-creative-games/luban) | 配置表 | 构建期暂存校验后事务发布代码/数据/清单，运行期自加载配置服务（ADR-0009） |
| [Odin Inspector](https://odininspector.com) | 可选专业 Inspector | 项目级增强；Framework 原生基线不依赖、不随包分发（[移除与集成指南](docs/optional-odin-integration.md)） |
| [AnkleBreaker Unity MCP](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin) | 编辑器自动化 | 让 AI 经带队列与 Undo 的结构化工具安全操作 Unity Editor |

仓库是开发工作区，不等同于可整体再分发的 Framework 包。根 `LICENSE` 适用于 SSFramework 自有部分；第三方 Package、DLL、字体和资产仍受各自许可证或商业授权约束。当前项目中的 `Assets/Plugins/Sirenix` 是可选开发工具，Framework 原生基线不依赖它，未来可分发包也不应包含其付费 DLL；详见[Odin 可选集成与移除](docs/optional-odin-integration.md)。

---

## 📚 文档

| 文档 | 适用对象 | 内容 |
|---|---|---|
| **[用户手册](docs/framework-guide.md)** | 框架使用者 | 28 章完整教程，从理念到 API 速查 |
| [愿景与路线图](docs/roadmap.md) | 产品 / 框架维护者 | 已完成阶段、下一款真实游戏的验证策略与长期候选 |
| [持续完善计划](docs/project-improvement-plan.md) | 维护者 / 评审者 | 当前健康基线、已完成闭环与下一批优先级 |
| [Framework Module 地图](docs/framework-module-map.md) | 架构维护者 | 38 个一方程序集（19 个生产 + 19 个测试）的职责、依赖方向与删除测试 |
| [Odin 可选集成与移除](docs/optional-odin-integration.md) | 框架使用者 / 包维护者 | 原生基线、授权边界、迁移步骤与未来 Adapter 准入条件 |
| [架构决策记录](docs/adr/README.md) | 设计评审者 | 关键决策的 Context / Decision / Consequences |
| [框架使用规则](Assets/Game/AGENTS.md) | AI Agent / 团队成员 | 业务代码遵循的核心约定 |
| [框架内部编码规则](Assets/Game/Framework/AGENTS.md) | 框架维护者 | 改框架源码时的内部规范 |
| [项目协作规则](AGENTS.md) | 所有协作者 | 项目级 AI 协作约定 |
| [AI 协作方案设计原理](docs/ai-collaboration-guide.md) | 工具配置者 | 跨 Agent 公共真值、规则演进、Skill 与 Harness 的边界 |
| [其他 AI 接入与 Handoff](docs/ai-agent-onboarding.md) | 新 Agent / 交接者 | 最薄接入步骤、能力探针、证据包与失败降级 |
| [AI 游戏开发能力地图](docs/ai-game-development-capability-map.md) | 游戏制作人与 Agent | 从产品目标到工程、美术、音频、体验、发布的能力谱系与补全策略 |
| [首款商业 3D 游戏策略](docs/commercial-3d-game-strategy.md) | 产品 / 游戏开发者 | Steam / Windows 优先的平台选择、当前产品定位、证据 Gate 与生产边界 |
| [《游牧工坊》产品愿景与第一版地基](docs/nomad-workshop-game-vision.md) | 游戏设计 / 开发者 | 自动居民、目标点旅行、单层建造、世界持久化与 Foundation Prototype 范围 |
| [《游牧工坊》首个技术 Spike](Assets/Game/NomadWorkshop/README.md) | 游戏开发者 / Agent | 有界随机 Utility AI、原子预留、实时 3D、Humanoid / 五动作资产管线、设施锚点与验证边界 |
| [AI 音乐与音效生产候选](docs/ai-audio-production-research.md) | 音频制作人与 Agent | 生成平台、商用授权、后期软件、运行时边界与首轮 Audio Spike |
| [Unity CLI 与项目自动化](docs/unity-cli-automation.md) | 自动化维护者 | CLI / Pipeline / MCP / OS UI 分工、命令示例与项目 Adapter |

用户手册章节速览：

1. 框架理念 / 2. 架构总览 / 3. 快速开始 / 4. Context / 5. Model 与 Event
6. System / 7. Utility / 8. View / 9. Command / 10. 多上下文
11. 容器注册与解析规则 / 12. 纯代码上下文 / 13. AssetReference / 14. 数据流统一抽象
15. 热更新（HybridCLR） / 16. 配置表（Luban） / 17. UI 框架 / 18. 本地存储
19. 音频 / 20. 游戏流程 / 21. 本地化 / 22. 字体 / 23. 框架诊断面板
24. 响应式集合与列表绑定 / 25. 网络 / 26. 推荐项目结构 / 27. UI 嵌入桥 / 28. 日志

---

## 🎯 示例项目

`Assets/Game/Framework/Demo/` 是一个可运行的交互式教学 demo（模块化章节外壳 + 左侧导航），由简入深覆盖框架全部能力：

| 分类 | 章节 |
|---|---|
| 入门 | 框架总览 / 最小闭环 / 接入你的项目 |
| 核心 | Model / Command / System / Event / 双 View 后端 / 多 Context / Container / 生命周期 / 诊断面板 |
| 能力 | 对象池 / 资源 / 存储 / 日志 / 音频 / Flow / 本地化 / 字体 / UI 窗口与列表 / UI 嵌入桥 / 网络 |
| 进阶 | R3 / YooAsset / 资源运营端到端 / HybridCLR / Luban / DOTS-ECS 融合 / 服务注册生成 / 模块依赖与裁剪 |

能力点章节的按钮通常保持“一次操作、一个可观察结果”，旁边附「查看源码」跳转以便直接对照因果；端到端工作流章节会明确组合前面已讲过的原语，让读者看到完整编排。

---

## 🗂️ 项目结构

```
SSFramework/
├── Assets/
│   └── Game/
│       ├── AGENTS.md                  ← 框架使用规则
│       └── Framework/
│           ├── AGENTS.md              ← 框架内部规则（含程序集结构表）
│           ├── Core/                  ← 运行时内核（Game.Framework）
│           │   ├── Context/ Internal/ ← GameContext、Container、DI、注入
│           │   ├── Command/ Systems/ Model/ Event/ Utility/ View/ ← 五层
│           │   ├── Lifecycle/ Reactive/ Pool/ Asset/              ← 生命周期与通用原语
│           │   └── Audio/ Flow/ Localization/ Logging/ Network/ Storage/ Diagnostics/
│           │                              ← 可按 Interface 组合的运行时能力
│           ├── Asset.Yoo/             ← YooAsset provider 模块
│           ├── UI/ UI.UGui/ UI.Toolkit/ UI.Bridge/ ← UI 核心、双后端与嵌入桥
│           ├── Config/                ← 配置表运行时模块 + Luban 生成管线（Editor）
│           ├── Fonts/ Network.Proto/  ← 可删除的字体链 / Protobuf Adapter 模块
│           ├── Boot/                  ← 热更引导薄壳（AOT）
│           ├── Build/Editor/          ← YooAsset 普通资源构建（不依赖 HybridCLR）
│           ├── Build/HybridCLR/       ← 可删除的 HybridCLR 热更新构建
│           ├── Editor/                ← 通用编辑器（RPDrawer / AssetReferenceDrawer）
│           ├── Demo/                  ← 可运行教学 demo
│           └── Test/                  ← EditMode + PlayMode 测试
├── docs/                              ← 用户手册、ADR、协作指南
├── .agents/skills/                    ← 跨 Agent 可读取的项目工作流真值
├── README.md                          ← 本文件
├── AGENTS.md                          ← 项目级协作规则
└── CLAUDE.md                          ← Claude 的低成本根规则导入（→ AGENTS.md）
```

---

## 📌 状态

**2026-09 Framework Baseline 已形成。** 核心抽象、19 个生产程序集、对应测试、交互式 Demo、Editor 工具、文档和 AI 协作入口已经达到可复核的阶段冻结点。它不是“从此不再改”的终点，也不是已经完成 SemVer / UPM 发布承诺；下一阶段将以《游牧工坊》这一 Steam / Windows 优先的正规 3D 商业游戏验证真实生产，只让被产品证据证明的通用缺口回流框架。平台与证据 Gate 见[首款商业 3D 游戏策略](docs/commercial-3d-game-strategy.md)，当前玩法假设与首版范围见[《游牧工坊》产品愿景与第一版地基](docs/nomad-workshop-game-vision.md)，框架延后项见[持续完善计划](docs/project-improvement-plan.md)。
