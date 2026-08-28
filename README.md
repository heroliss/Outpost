# 🎮 SSFramework

> 这是一个自研的 Unity 游戏开发框架，采用创新的 MVC 变体代码架构。秉持"站在巨人肩膀上"的开发理念，把 UniTask、R3、YooAsset 等成熟开源库与 Unity 编辑器深度融合，同时把 Odin Inspector 这类专业付费工具保留为可选增强。让编译器替你守住代码边界、让 Inspector 替你看穿运行状态，让数据流理念把你的思路理清；需要轻量包体或未购买插件的项目也能按 Module 自由取舍。

框架特性：把传统 MVC 中臃肿的 Controller 一分为二（**System** 负责"怎么做"、**Command** 负责"做什么"），用 DI 容器和接口约束各层权限，配合 Unity 的 Hierarchy 直接表达上下文与模块关系——所有运行时状态在 Inspector 一眼看穿，所有依赖在编译期就能查验。

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
| **可插拔命令系统** | `ICommandSystem` 是接口注册，替换默认实现即可一处拦截全部命令——日志、回放、撤销/重做、优先级队列、自动化测试都能在此承载 |
| **响应式数据流统一** | 事件、属性、UniTask、协程、UnityEvent、C# event 均可互转为 `Observable<T>`；状态对 View 返回 `ReadOnlyReactiveProperty<T>` 等只读类型 |
| **自动生命周期管理** | `DisposableBag`（`Bag`）统一登记订阅 / 资源句柄 / 池租借，`OnDestroy` 时一并清理，无需手动维护 |
| **异步取消传导** | Context Dispose 级联取消所有相关异步操作；View 的 `ExecuteCommandAsync` 自动绑定 destroy token |

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

## 🧪 工程质量基线

以下不是愿景，是当前仓库里**可复核**的维护纪律：

- **测试**：892 条 PlayMode + EditMode 测试（2026-08-28 基线：528 + 364，全绿）——覆盖容器解析契约、命令分发（含 struct 零装箱路径）、事件总线、初始化失败事务与生命周期级联、AI PlayMode 无弹窗预检、诊断面板编辑态防误报、Editor 窄窗口响应式布局、中文 Inspector / 诊断状态契约、资源四态查询 / 原生操作所有权与跨 Provider 包级并发、配置就绪 / 原始失败 / waiter-owner 取消、内嵌服务器端口回退、Demo 章节同实例生命周期、真实 DemoScene 逐章 Build、教学语义/降级契约与源码跳转防腐、ReactiveList 行身份 / 逐行释放、真实 Toolkit Cache/Destroy 重开语义、异步按钮取消/异常/防重入、本地化延迟 Source 失效刷新、对象池、UI Loading 并发所有权与窗口栈、UI 嵌入桥低清等比降采样，以及 Outpost 双后端确定性回归与“标题 → 战斗 → 撤离 → 结算 → 回标题”的真实玩家路径。核心编排（如 `UIUtility`）刻意做成渲染中立的纯 C#，可脱离场景单测。交互式 Editor 经 `SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）` 后可由 MCP 无弹窗启动；关闭本工程 Editor 后，`Tools/run-tests.ps1` 通过 ProjectVersion 驱动 Unity CLI / Direct Adapter，默认顺序跑 EditMode + PlayMode，并分别保留 NUnit XML / Editor 日志（CI / 推送前用）。
- **文档四层，且与代码同步维护**：本 README（门面）→ [用户手册 28 章](docs/framework-guide.md)（心智模型 + API）→ [ADR](docs/adr/README.md)（每个关键决策的“为什么”与代价）→ 分层 `AGENTS.md`（就近自动加载的协作约束）。改设计必须同步改文档是硬规矩，不留“文档说 A、代码做 B”。
- **权限双保险**：编译期 `ICanXxx` 接口约束 + `[Inject]` 注入期同源镜像校验（`InjectionPlan`），两条路径共用一套权限模型，堵住"扩展方法编译不过就换注入绕过"的口子。
- **防泄漏是设计目标不是补丁**：linked CTS 单槽缓存与移交释放、池租借登记的提前归还摘除、停放节点自愈重建、Dispose 幂等与逆序释放——这些边界行为都有注释解释"为什么"并有测试盯着。
- **零编译警告**：包括 XML doc 的 cref 完整性（泛型尖括号转义约定见 `AGENTS.md`）。

---

## 🛠 技术栈

构建在以下成熟开源库之上：

| 库 | 用途 | 在框架中的角色 |
|---|---|---|
| [UniTask](https://github.com/Cysharp/UniTask) | 零分配异步 | Async Command、取消令牌传导、与协程互转 |
| [R3](https://github.com/Cysharp/R3) | 响应式编程 | `ReactiveProperty`、Observable 操作符（`DisposableBag` 底层复用其 `CompositeDisposable`） |
| [YooAsset](https://github.com/tuyoogame/YooAsset) | 资源 provider | 当前默认 provider 实现（经 `IAssetProvider` 隔离，可整体替换） |
| [HybridCLR](https://github.com/focus-creative-games/hybridclr) | 代码热更 | 列表驱动的热更范围 + Boot 引导（ADR-0008） |
| [Luban](https://github.com/focus-creative-games/luban) | 配置表 | 构建期生成代码/数据/清单，运行期自加载配置服务（ADR-0009） |
| [Odin Inspector](https://odininspector.com) | 可选专业 Inspector | 项目级增强；Framework 原生基线不依赖、不随包分发（[移除与集成指南](docs/optional-odin-integration.md)） |
| [AnkleBreaker Unity MCP](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin) | 编辑器自动化 | 让 AI 经带队列与 Undo 的结构化工具安全操作 Unity Editor |

<img src="https://raw.githubusercontent.com/AnkleBreaker-Studio/unity-mcp-plugin/main/icon.png" alt="AnkleBreaker MCP logo" width="24"> **Powered by AnkleBreaker MCP**。

---

## 📚 文档

| 文档 | 适用对象 | 内容 |
|---|---|---|
| **[用户手册](docs/framework-guide.md)** | 框架使用者 | 28 章完整教程，从理念到 API 速查 |
| [持续完善计划](docs/project-improvement-plan.md) | 维护者 / 评审者 | 当前健康基线、已完成闭环与下一批优先级 |
| [Framework Module 地图](docs/framework-module-map.md) | 架构维护者 | 31 个 asmdef Module 的职责、依赖方向与删除测试 |
| [Odin 可选集成与移除](docs/optional-odin-integration.md) | 框架使用者 / 包维护者 | 原生基线、授权边界、迁移步骤与未来 Adapter 准入条件 |
| [架构决策记录](docs/adr/README.md) | 设计评审者 | 关键决策的 Context / Decision / Consequences |
| [框架使用规则](Assets/Game/AGENTS.md) | AI Agent / 团队成员 | 业务代码遵循的核心约定 |
| [框架内部编码规则](Assets/Game/Framework/AGENTS.md) | 框架维护者 | 改框架源码时的内部规范 |
| [项目协作规则](AGENTS.md) | 所有协作者 | 项目级 AI 协作约定 |
| [AI 协作方案设计原理](docs/ai-collaboration-guide.md) | 工具配置者 | 跨工具差异、用户级配置 |
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
| 核心 | Model / Command / System / Event / 双 View 后端 / 多 Context / Container / 服务注册生成 / 生命周期 / 诊断面板 |
| 能力 | 对象池 / 资源 / 存储 / 日志 / 音频 / Flow / 本地化 / 字体 / UI 窗口与列表 / UI 嵌入桥 / 网络 |
| 进阶 | R3 / YooAsset / 资源运营端到端 / HybridCLR / Luban / DOTS-ECS 融合 |

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
├── README.md                          ← 本文件
├── AGENTS.md                          ← 项目级协作规则
└── CLAUDE.md                          ← Claude Code 入口（→ AGENTS.md）
```

---

## 📌 状态

当前为开发阶段。API 已稳定可用，理念与核心抽象基本定型；细节优化和工具链建设持续迭代中。
