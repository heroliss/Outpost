# Outpost 垂直切片导读 —— 用一个小游戏串起框架

> 给谁看：**刚接触本框架、或已读过 [framework-guide.md](../../../../docs/framework-guide.md) 想看"真项目怎么落地"的人。**
> Outpost 是一个刻意做小的近防炮生存 demo，它存在的首要目的不是"好玩"，而是**把框架的十来个能力在一个真实消费场景里串起来、并顺手验证接缝**。本文是"游戏里看得见的现象 ↔ 框架能力 ↔ 源码位置"的对照地图。

代码位置：[`Assets/Game/Outpost/`](../)。程序集 `Game.Outpost`（业务）+ `Game.Outpost.Sim`（零引擎依赖的纯 C# 模拟）。

---

## 30 秒看懂它在演示什么

一句话：**玩家固定居中的近防炮自动转向索敌开火，敌人从四周一波比一波多地涌入（约 20 波后每波数千、同屏两三千），每波清空后哨站维修回满、三选一强化，直到六种成长全部到顶进入稳态——托管模式可以永续观战，「撤离」把分数落袋结算。** 全部美术是程序几何体 + URP 后处理辉光，没有一张贴图。

这个"最小可玩闭环"故意压得很薄，好让每一处都对应一个可指认的框架用法：

| 你在游戏里看到 | 背后的框架能力 | 落在哪 | 深读 |
|---|---|---|---|
| 标题→战斗→结算的界面切换 | **游戏流程状态机** `IGameFlow`（一次性状态、传参走构造） | [`Scripts/Flow/`](../Scripts/Flow/) | guide §20 / [ADR-0023](../../../../docs/adr/0023-game-flow.md) |
| 标题 / 结算 / 看点弹窗三个窗口 | **UXML 窗口**：`[UIWindow(Asset)]` 经资源系统加载 uxml、共享 `Outpost.uss` 主题；弹窗另演示窗口栈 + 模态遮罩 | [`Res/UI/`](../Res/UI/) + [`Scripts/Windows/`](../Scripts/Windows/) | guide §17 / [ADR-0016](../../../../docs/adr/0016-ui-framework.md) |
| HUD 实时刷血量/波次/击杀/得分/性能行 | **读写分离 + 只读订阅**（View 经查询 Command 拿 `ReadOnlyReactiveProperty`） | [`Battle/BattleHudView.cs`](../Scripts/Battle/BattleHudView.cs) | guide §5 / [ADR-0001](../../../../docs/adr/0001-five-layers-and-permission-interfaces.md) |
| 波间弹出三选一升级卡片 | **响应式集合增量绑定** `ObservableList` + `Bag.BindList` | [`Battle/UpgradeChoiceView.cs`](../Scripts/Battle/UpgradeChoiceView.cs) | guide §24 / [ADR-0027](../../../../docs/adr/0027-reactive-collections-list-binding.md) |
| 点卡片 / 托管开关 / 撤离按钮 | **命令外发**：View 不能直调 System，写意图经 `ExecuteCommand` 中转 | [`Battle/UpgradeCommands.cs`](../Scripts/Battle/UpgradeCommands.cs)、[`BattleCommands.cs`](../Scripts/Battle/BattleCommands.cs) | guide §3 |
| 数千同屏的敌人海 + 铺满战场的残骸 | **实例化渲染**（`SwarmRenderer` 批量绘制活敌；残骸烘焙成静态批次永久留存，零 GameObject） | [`Battle/SwarmRenderer.cs`](../Scripts/Battle/SwarmRenderer.cs) | 本文§2 |
| 曳光 / 脉冲 / 飘字 / 碎片成群出现又消失 | **对象池** `IPoolUtility`（`Bag.Spawn`/`Despawn` 自动借还） | [`Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs) | guide §7 / [ADR-0007](../../../../docs/adr/0007-custom-object-pool.md) |
| 敌人五种、升级六种、波次成长曲线、表现参数 | **配置表** Luban（数值列进模拟、表现列表现层直读——加敌人=加一行） | [`Configs~/`](../Configs~/) → [`Config/Gen/`](../Config/Gen/) | guide §16 / [ADR-0009](../../../../docs/adr/0009-luban-integration.md) |
| 历史最佳 / 局数跨会话保留、结算"新纪录"高亮 | **本地存储** `IStorageUtility`（`[Serializable]` 类整存整取、原子写 + 备份回退） | [`Scripts/Save/`](../Scripts/Save/) | guide §18 / [ADR-0021](../../../../docs/adr/0021-local-storage.md) |
| 标题 / 战斗双 BGM 交叉切换，爆炸 / 维修 / 升级各有其声，火墙轰鸣随射速涨落 | **音频** `IAudioUtility`（BGM 单通道 + 池化音效 + 分组音量）：BGM 导演订 `FlowChangedEvent` 一个事件、不侵入状态；爆炸音每帧限 1 发与特效同一套海量纪律；火墙循环音走炮塔挂 `AudioSource` 逐帧调制——"持续音源用引擎组件"分界的实战样本 | [`Scripts/Systems/OutpostAudioSystem.cs`](../Scripts/Systems/OutpostAudioSystem.cs) + [`Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs) | guide §19 / [ADR-0022](../../../../docs/adr/0022-audio-service.md) |
| 设置窗一键切中英文，所有界面文案（含下层窗口）实时跟变 | **本地化** `ILocalizationUtility`：文本源 = 十行 adapter 包自己的 Luban 表 `TbL10N`（一行一 key 一列一语言）；UI 绑 key 不绑死文案（upgrade 表存的就是 key）；字体链 `MonoLocaleFonts` 按语言接管 fallback | [`Scripts/Config/OutpostTextSource.cs`](../Scripts/Config/OutpostTextSource.cs) + [`Scripts/OutpostLocales.cs`](../Scripts/OutpostLocales.cs) | guide §21/§22 / [ADR-0024](../../../../docs/adr/0024-localization.md)、[ADR-0025](../../../../docs/adr/0025-font-fallback.md) |
| 音量滑条即改即生效、关窗保存、重启回灌 | **设置持久化**：音量 / 语言的运行时真源在两个 Utility 自身，设置窗只是遥控器、存档只是快照（刻意不设 SettingsModel） | [`Scripts/Windows/SettingsWindow.cs`](../Scripts/Windows/SettingsWindow.cs) + [`Scripts/Save/SettingsCommands.cs`](../Scripts/Save/SettingsCommands.cs) | guide §18/§19 |
| 整个战斗的规则演算 | **纯 C# 模拟接缝** `IBattleSim`（可 AOT / 可单测 / 可置换 ECS） | [`Sim/`](../Sim/) | [ADR-0014](../../../../docs/adr/0014-realtime-simulation-ownership.md) |

---

## 三个最值得看的设计

### 1. `IBattleSim`：把"规则"关进零引擎依赖的盒子，后端可整体置换

战斗的所有数值演算（刷怪、移动、转向开火、结算、波次成长）都在 [`Sim/`](../Sim/) 里，那是一个 **`noEngineReferences` 的程序集**：不 `using UnityEngine`，坐标用 `System.Numerics.Vector2`。

- **可单测 / AI 可验证**：规则是确定性纯函数，`new ReferenceBattleSim()` 喂一段 Tick 序列毫秒级跑完，不用进 Play。本轮全部难度标定就是无头跑数做的（托管策略跑 110 波、逐波打印消耗/峰值/耗时曲线）。同一红利还做成了游玩入口：director 的 `_startWave` > 1 时开局**无头快进**——加载期静默跑完前面的波次（升级按托管贪心自动拿、击杀直接铺成残骸），快进 19 波实测 85ms。
- **可 AOT**：热更/裁剪环境永不炸。
- **可置换后端**：`BattleDirectorSystem` 顶部有一个 `BattleSimBackend` 枚举 + `CreateSim()` 工厂——M6 的 ECS/DOTS 后端只需加一个分支，事件→表现翻译层、Model、HUD 全部零改动。HUD 左下角的**性能行**（后端名 · 敌人数 · 残骸数 · 模拟耗时 · fps）就是为"同题对比"准备的度量面板。

### 2. 事件 → 表现翻译层，与"实例化渲染 × 对象池"的分工

模拟只往外发**领域事件**：`EnemySpawned` / `EnemyHit` / `TurretFired` / `EnemyDetonated` / `WaveCleared`，它不知道什么是 GameObject。[`BattleDirectorSystem`](../Scripts/Battle/BattleDirectorSystem.cs)（一个 `System`）把事件翻成表现，并把聚合值写进 `BattleModel` 供 HUD 只读订阅。**换 ECS 后端时这层原样保留。**

表现层按"数量级"分两条路径，这是海量同屏下的关键取舍：

- **海量常驻单位 → 实例化渲染**：[`SwarmRenderer`](../Scripts/Battle/SwarmRenderer.cs) 每帧直接遍历模拟快照，`Graphics.DrawMeshInstanced` 按原型分批绘制全部敌人（出生弹出/呼吸/白闪/血量变暗全部逐实例数值计算）——敌人不占任何 GameObject，两三千同屏不掉帧。
- **死亡 → 残骸层**：击杀不是消失——每具尸体沿弹道短促滑出落定后，**烘焙进静态实例批次**永久留存（环形上限复写，默认 3 万），战场地面逐渐积出击杀分布的"历史地图"。这既是千级击杀率下的保底反馈（爆炸特效有每帧预算、残骸没有），也是实例化渲染的持续压力源：数万静态实例的矩阵/颜色只在落定时写一次，每帧零重建直接提交（实测 3000 活敌 + 3.6 万残骸约 100fps）。
- **少量瞬时特效 → 对象池**：曳光/脉冲/烟/碎片/飘字走 `Bag.Spawn` 借还，并有**每帧演出预算**（命中/击毁/出生特效各有限量，超出只结算数值不演出）；玩家受创（漏怪自爆+拦截溅射）在 0.25s 窗口内**聚合**成一次震屏/红闪/汇总飘字——每秒上百次受创时逐条演出会刷屏。

### 3. 难度模型：数量爬坡到平台期 + 双方全封顶 = 托管永续

无限模式的稳态是**设计出来的，不是调出来的**，三个机制缺一不可（都有无头跑数实证）：

- **波间维修**：撑过一波血量回满——"每波消耗多少血"成为独立的单波压力指标（标定目标≈一半）。若靠持续回血续命，"伤害<回血则永生、>则必死"是个双稳态，调不出中间态。
- **敌人规模平台期**：各角色数量按 `CountGrowth^波次` 指数爬坡（约 20 波到每波数千）、到 `MaxCount` 封顶；数值成长 `StatGrowth^波次` 同样有 `MaxStatScale` 封顶——否则后期单只漏怪伤害无界，任何稳态都会被击穿。
- **玩家全成长封顶**：六种升级（攻击/攻速/射程/回转/血量/回血）全部有顶，到顶的移出三选一池、全部到顶后不再弹面板——若玩家火力无限成长，平台期的每波消耗会衰减到零。攻速下限 0.004s（每分钟一万五千发的火墙）；**回转封顶**是漏怪的结构性来源：360° 密集来袭时炮塔扫不过来，约半数炮灰漏网、每只只削一小口血。

于是：托管（自动选卡）可以永续观战、每波消耗稳定在四到六成；**「撤离」是一局的常规结束方式**（把分数落袋进结算/存档）；失守只在极端情况发生。难度数值全在 5 个 json（`battleglobal` / `enemy` / `waverole` / `wavescaling` / `upgrade`），改完重生成即调、无需碰代码。

---

## 推荐阅读路径（顺着代码读一遍）

1. [`Scripts/Flow/`](../Scripts/Flow/) —— 先看宏观阶段：`BootState → TitleState → BattleState → ResultState`，理解 `IGameFlow` 怎么切界面 + 传参。
2. [`Sim/IBattleSim.cs`](../Sim/IBattleSim.cs) + [`ReferenceBattleSim.cs`](../Sim/ReferenceBattleSim.cs) —— 规则内核，纯 C#，最好懂。
3. [`Battle/BattleContext.cs`](../Scripts/Battle/BattleContext.cs) + [`BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs) —— 子上下文注册了什么、导演怎么把事件翻成表现和 Model、后端工厂在哪。
4. [`Battle/SwarmRenderer.cs`](../Scripts/Battle/SwarmRenderer.cs) + [`EnemyVisuals.cs`](../Scripts/Battle/EnemyVisuals.cs) —— 海量单位怎么画、表现参数怎么从表里来。
5. [`Battle/BattleHudView.cs`](../Scripts/Battle/BattleHudView.cs) + [`BattleQueries.cs`](../Scripts/Battle/BattleQueries.cs) —— 只读订阅一侧长什么样（给 HUD 加一个值 = Model→ReadModel→View 三步）。
6. [`Res/UI/`](../Res/UI/) + [`Scripts/Windows/`](../Scripts/Windows/) —— uxml 布局 + uss 主题 + 代码接线的窗口标准姿势。
7. [`Systems/OutpostAudioSystem.cs`](../Scripts/Systems/OutpostAudioSystem.cs) + [`Config/OutpostTextSource.cs`](../Scripts/Config/OutpostTextSource.cs) + [`Windows/SettingsWindow.cs`](../Scripts/Windows/SettingsWindow.cs) —— 音频 / 本地化 / 设置三个横切服务的消费侧：BGM 订流程事件、文本源 adapter、设置窗直连 Utility + 关窗落盘。

---

## 视觉为什么"全是几何体"

这是刻意的，不是没时间做美术。Demo 的重点是**框架 + DOTS 置换验证**，美术资产越少，越能让读代码的人把注意力放在数据流和接缝上，也让海量同屏不受美术拖累。

- 敌人用程序网格换形状表达身份（[`OutpostMeshes.cs`](../Scripts/Battle/OutpostMeshes.cs)），颜色/形状/体型/爆炸倍率全在配置表的表现列（[`EnemyVisuals`](../Scripts/Battle/EnemyVisuals.cs) 解析）：炮灰无人机=小三角、突袭者=箭头、装甲兵=六边形、掠袭机=细针、攻城核=八边形，有向形状逐帧转向来袭方向。
- 背景是"火控台"读法（[`ArenaDecor.cs`](../Scripts/Battle/ArenaDecor.cs)）：射程圈内淡填充盘 + 旋转雷达扫描臂 + 外缘暖红警戒环；射程升级后整体外扩、成长直接可见。
- 炮塔回转与开火时机都是**内核真机制**（按回转速度逐帧转向、炮口对准才结算命中，hitscan 同帧结算）；表现层按 `TurretAngle` 画炮管、按击发节奏自算"火力热度"驱动核心辉光与曳光散射（高射速读成一片弹雨）。
- 实例化渲染用一个手写的最小 unlit shader（[`OutpostSwarmUnlit.shader`](../Res/Shaders/OutpostSwarmUnlit.shader)）：per-instance 颜色 + `editor_sync_compilation`（`DrawMeshInstanced` 不触发编辑器异步 shader 编译，不加这行敌人海在编辑器里会整体不可见）。

---

## 里程碑与已知偏离

里程碑：M0 骨架闭环 → M1 战斗核心 + 视觉 → M2 波间三选一 → **M3 存档 + 音频 + 本地化 + 设置（已完成）** →（M4 网络排行 → M5 构建收口 → M6 DOTS 置换）。当前形态：**无限模式 + 托管永续 + 数千同屏 + 双语**——这正是为 M6 准备的真实压力场景：`Reference` 后端的 O(n) 线性扫描是刻意保留的对比基线，HUD 性能行随时读数。

已知偏离与接缝观察（诚实记录）：

- **波间抉择未用"嵌套子 Flow"**：抉择本质是导演的一个暂停相位，现有相位机自然容纳，再套一层子 `GameFlow` 属过度设计。§28 的嵌套子 Flow 验收留给更贴合的场景单独做。
- **失守终态基本不可达**：全封顶 + 波间维修的稳态下，任何 build 最终都会收敛到全到顶——失守只在中段成长严重偏科时可能发生。这是"托管永续"目标的直接推论，撤离才是常规收束。
- **本地化绑定要求文本源先就绪**：`BindLocalizedText` 的刷新信号只有 `Locale`，文本源（配置表）异步后到不会触发重绑——绑定先于就绪 = 裸 key 定格。业务解法 = `BootState` 进标题前 `await` 配置就绪（详见[技术笔记的 M3 档案节](outpost-tech-notes.md#2026-07--m3-收尾音频--本地化--设置窗272930-消费落点)）。
- **框架看点弹窗刻意不本地化**：它是指向中文文档的教学文案墙，翻译只添维护噪音——本地化范围 = 游戏 UI。

更完整的框架能力清单、每个 §N 的教学与 API 见 [framework-guide.md](../../../../docs/framework-guide.md)；架构决策的来龙去脉见 [docs/adr/](../../../../docs/adr/)。
