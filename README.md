# 🎮 SSFramework

> 这是一个自研的 Unity 游戏开发框架，采用创新的 MVC 变体代码架构。秉持"站在巨人肩膀上"的开发理念，把 UniTask、R3、YooAsset、Odin Inspector 等成熟优秀第三方工具与 Unity 编辑器等全部深度融合在一起，充分发挥各自的价值，让编译器替你守住代码边界、让 Inspector 替你看穿运行状态，让数据流理念把你的思路理清。灵活且强大的框架设计让你在规范代码的同时可以应对各种情况。

框架特性：把传统 MVC 中臃肿的 Controller 一分为二（**System** 负责"怎么做"、**Command** 负责"做什么"），用 DI 容器和接口约束各层权限，配合 Unity 的 Hierarchy 直接表达上下文与模块关系——所有运行时状态在 Inspector 一眼看穿，所有依赖在编译期就能查验。

---

## ✨ 核心特点

| 特点 | 一句话说明 |
|---|---|
| **单向数据流** | View 只观察、不写入；状态变化必须经 Command → System → Model |
| **类型驱动** | 事件、命令、服务都用类型区分，避免字符串和枚举的脆弱标识 |
| **Hierarchy 原生** | Context 父子关系直接用 GameObject 层级表达，拖动节点即可重组依赖图 |
| **多 Context 嵌套** | 子 Context 自动继承父级服务，平行 Context 完全隔离——天然适合多场景、多模块、Mock 测试 |
| **Mono / 纯代码双路径** | 业务可以挂节点（Inspector 可见、可调）也可以纯 C#（测试友好、不依赖场景） |
| **零分配 Command** | `readonly struct` + 双泛型重载，高频命令零 GC 压力 |
| **可插拔命令系统** | `ICommandSystem` 是接口注册，替换默认实现即可一处拦截全部命令——日志、回放、撤销/重做、优先级队列、自动化测试都能在此承载 |
| **响应式数据流统一** | 事件、属性、UniTask、协程、UnityEvent、C# event 均可互转为 `Observable<T>`；状态对 View 返回 `ReadOnlyReactiveProperty<T>` 等只读类型 |
| **自动生命周期管理** | `SubscriptionSet` 统一登记各类订阅，`OnDestroy` 时一并清理，无需手动维护 |
| **异步取消传导** | Context Dispose 级联取消所有相关异步操作；View 的 `ExecuteCommandAsync` 自动绑定 destroy token |

---

## 📐 架构一览

框架由五层组成——**View / Command / System / Model + Event / Utility**，外加 Context（容器，内部承载 DI Container 与 Event Bus）。业务代码通过统一接口操作，不直接碰底层容器。

![SSFramework 五层架构图](docs/SSFramework-architecture.png)

图上几条关键信息：

- **写入是单向链路** —— View 任何状态改动必须走 `Command → System → Model`，View 自身不直接写
- **读取也经过 Command** —— `ReactiveProperty` 持续推送当前值（给 UI 文本订阅）、`Event` 瞬时通知一次（给动画 / 音效）；View 通过只读查询 Command 取得订阅源，两条响应路径互不知道对方存在
- **MonoBehaviour / Rigidbody 正交于五层** —— 任意层都可以继承 MonoBehaviour 拿到 Inspector 序列化与 Unity 生命周期，引擎能力不影响架构定位
- **权限由接口编译期约束** —— `IModel` 不能调用 `ISystem`、`View` 不能写 `Model`，越界一律编译报错（不是文档约定，是类型系统强制）

---

## 🚀 极简示例

```csharp
// Model —— 持有数据，RP<T> 可订阅、Inspector 可见、覆盖任意类型（using R3;）
public class PlayerModel : MonoModelBase
{
    [field: SerializeField] public RP<int> HP { get; private set; } = new(100);
}

// System —— 修改数据的合法来源；RP<T> IS-A ReadOnlyReactiveProperty<T>，直接赋值，零分配
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
        Subs.Subscribe(hp, value => _hpText.text = value.ToString());  // 订阅即得 current value
        Subs.Subscribe<PlayerHurtEvent>(_ => PlayHurtAnim());
        Subs.Subscribe(_btn.onClick, () => this.ExecuteCommand(new TakeDamageCommand()));
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

## 🛠 技术栈

构建在以下成熟开源库之上：

| 库 | 用途 | 在框架中的角色 |
|---|---|---|
| [UniTask](https://github.com/Cysharp/UniTask) | 零分配异步 | Async Command、取消令牌传导、与协程互转 |
| [R3](https://github.com/Cysharp/R3) | 响应式编程 | `ReactiveProperty`、Observable 操作符、`CompositeDisposable` |
| [YooAsset](https://github.com/tuyoogame/YooAsset) | 资源 provider | 当前默认 provider 实现 |
| [Odin Inspector](https://odininspector.com) | Inspector 扩展 | 接口类型字段序列化、自定义绘制器 |

---

## 📚 文档

| 文档 | 适用对象 | 内容 |
|---|---|---|
| **[用户手册](docs/framework-guide.md)** | 框架使用者 | 14 章完整教程，从理念到 API 速查 |
| [框架使用规则](Assets/Game/AGENTS.md) | AI Agent / 团队成员 | 业务代码遵循的核心约定 |
| [框架内部编码规则](Assets/Game/Framework/AGENTS.md) | 框架维护者 | 改框架源码时的内部规范 |
| [项目协作规则](AGENTS.md) | 所有协作者 | 项目级 AI 协作约定 |
| [AI 协作方案设计原理](docs/ai-collaboration-guide.md) | 工具配置者 | 跨工具差异、用户级配置 |

用户手册章节速览：

1. 框架理念 / 2. 架构总览 / 3. 快速开始 / 4. Context / 5. Model 与 Event
6. System / 7. Utility / 8. View / 9. Command / 10. 多上下文
11. 容器注册与解析规则 / 12. 纯代码上下文 / 13. AssetReference / 14. 数据流统一抽象

---

## 🎯 示例项目

`Assets/Game/Framework/Demo/` 提供一组可运行示例：

| 示例 | 演示 |
|---|---|
| `MainContext + CounterView` | 最小可用模型，包含同步/异步 Command、struct/class Command、返回值 Command |
| `ParallelContext + ParallelView` | 平行上下文隔离（操作不影响主上下文） |
| `ChildContext + ChildView` | 嵌套子上下文（自动继承父级服务） |
| `CodeView + PureModel / PureSystem` | 纯代码路径（不挂 MonoBehaviour 创建 Context） |
| `DynamicSpawnView` | 运行时 Instantiate 后自动注入 |

---

## 🗂️ 项目结构

```
SSFramework/
├── Assets/
│   └── Game/
│       ├── AGENTS.md                  ← 框架使用规则
│       └── Framework/
│           ├── AGENTS.md              ← 框架内部规则
│           ├── Scripts/               ← 框架源码
│           │   ├── Architecture/      ← Context、Container、DI
│           │   ├── Command/           ← Command 系统
│           │   ├── Event/             ← Event 总线
│           │   ├── Model/             ← MonoModelBase
│           │   ├── System/            ← MonoSystemBase
│           │   ├── Utility/           ← MonoUtilityBase
│           │   └── View/              ← MonoViewBase + SubscriptionSet
│           ├── Demo/                  ← 可运行示例
│           └── Test/                  ← 单元测试
├── Packages/
│   └── com.tuyoogame.yooasset/        ← 当前默认资源 provider 依赖
├── docs/                              ← 用户手册与协作指南
├── README.md                          ← 本文件
├── AGENTS.md                          ← 项目级协作规则
└── CLAUDE.md                          ← Claude Code 入口（→ AGENTS.md）
```

---

## 📌 状态

当前为开发阶段。API 已稳定可用，理念与核心抽象基本定型；细节优化和工具链建设持续迭代中。
