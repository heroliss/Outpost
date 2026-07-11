# ADR-0030：Outpost M6 —— IBattleSim 的 DOTS 后端置换（roadmap Phase 3 落地）

**Status:** Accepted（2026-07-11）

## Context

roadmap Phase 3 对 DOTS 的定位是**协调 ECS，而非替换**：框架不改造成数据范式，而是验证「OOP 架构层（Context/Command/Model/View/Flow）+ ECS 演算内核」的组合姿势。切片从 M1 起就为此埋好接缝——战斗模拟藏在纯 C# 契约 `IBattleSim` 后（`Game.Outpost.Sim` 程序集，零引擎零框架依赖，M5 起刻意留 AOT），表现 / 数据 / 编排全部在接缝外。

M6 要回答三个问题：

1. **置换成本**：OOP 参考后端整体换成 Entities + Burst 后端，消费方（director 事件翻译层、Model、HUD、存档、音频）是否真的零改动？
2. **行为等价**：换后端后还是不是「同一个游戏」？（对拍验证）
3. **规模收益**：切片的难度设计以数量密度为核心（平台期数千同屏、射速上万发/分），DOTS 在这条曲线上买到了什么？

## Decision

| 决策 | 内容 | 为什么 |
|---|---|---|
| 程序集 | 新增 `Game.Outpost.Sim.Ecs`（引 Unity.Entities / Collections / Burst / Mathematics + Game.Outpost.Sim），**AOT、永不入热更列表** | Burst 产物是 AOT 原生码，热更域无法承载；`Game.Outpost`（热更）引用它属「热更调 AOT」合法方向，`CreateSim()` 加一个分支即接入 |
| 驱动形态 | `EcsBattleSim` 自建独立 `World`，**不进 player loop、不用 SystemGroup**：所有 job 在 `Tick` 内当帧 `Complete`，事件以记录缓冲带回主线程按序重放 | 接缝契约是「外部逐帧 Tick + 事件同步返回」——嵌 player loop 反而破坏契约；托管委托不能进 Burst，缓冲重放是标准解 |
| 数据建模 | 实体只有敌人（`EnemyPos`/`EnemyHp`/`EnemyMeta` 三组件，chunk SoA）；玩家是单例托管状态，进出 job 打包成 `PlayerCombatState`；原型表进 `NativeArray<EnemyArchetype>`（本就 blittable） | 单例状态做成实体是仪式感；海量同构数据（敌人）才是 chunk 的用武之地 |
| 职责切分 | 移动+抵达判定 = 并行 `IJobChunk` 原地写 chunk；开火循环 = **单线程** Burst `IJob` 在每帧收集的快照数组上跑（swap-remove 后即当帧终态）；chunk 保持权威（血量稀疏写回、击杀批量销毁） | 开火循环是顺序语义（下一发的目标取决于上一发的击杀），并行化会改规则——提速全靠 Burst 编译 + 连续内存；快照数组同时充当 `GetEnemy` 的 O(1) 读源（渲染层每帧全量遍历的消费模式不变） |
| 三角函数留托管 | 回转 / 炮口方向用 `System.Math`（与参考实现同一路径），job 内只有 IEEE 定义严格的加乘除/开方 | 规避 Burst libm 与 .NET 在超越函数上的实现差异直接进对拍 |
| 规格常量收口 | 判定阈值抽到 `BattleSimTuning`、角度数学抽到 `SimMath`（Sim 程序集，两后端同源引用） | 对拍的前提是双方跑同一套规格；各自定义迟早漂移 |
| Burst 编译 | 三个 job 均 `FloatMode.Strict + CompileSynchronously = true` | Strict 禁 FMA/重结合（对拍尽量贴近）；编辑器 Burst 默认异步编译会**静默回退托管执行**、污染性能度量（玩家包全 AOT 无此差别） |
| 默认后端 | 战斗场景 `_backend` 切 `Ecs`；`Reference` 保留为规格基线，Inspector 枚举一键切换 | 参考实现是规则的可执行规格与对拍锚点，不删 |

## 验证结果

### 对拍（同 Setup + 同 seed + 同 Tick 序列）

- **逻辑级（关 Burst，job 走 Mono＝与参考实现同一 JIT）**：12 波 5127 tick 贪心升级全程，**逐 tick 全等**（击杀/得分/敌数/血量/波次/阶段/炮塔角全部逐位相等）——移植零逻辑偏差。
- **规格级（开 Burst）**：前 21 波每波击杀/得分**完全相等**；22 波起（数千同屏 × 高射速）浮点 ulp 差异被混沌放大——击杀/自爆归属漂移 0.4~0.9%、清波时刻漂移 ≤ 1 tick、波内最低血漂移 <5% 血量。两个后端是「同一个游戏」，但**不是逐位同一局**。
- ⭐ **发现（跨编译域浮点边界）**：即便 `FloatMode.Strict`，Burst 原生码与 Mono JIT 仍存在 ulp 级浮点差异（首个分叉出现在纯加乘除/开方的移动数学上，疑 Mono 中间值精度提升）。**跨编译域的逐位确定性不可承诺**——需要 lockstep/回放级确定性的系统，必须把演算收口在单一编译域内（全 Burst 或全托管），而不是指望两边算得一样。

### 性能（Windows 编辑器，同机同负载）

| 负载 | Reference | Ecs（编辑器安检开） | Ecs（安检关，近玩家包形态） |
|---|---|---|---|
| 真实平台期（~1900 同屏，w31-33） | 0.52 ms/tick | **0.38 ms** | — |
| 合成压力 1.2 万敌 | 1.44 ms | 0.79 ms | 0.54 ms |
| 合成压力 2.2 万敌 | 2.68 ms | 1.03 ms | 0.77 ms |
| 合成压力 4.2 万敌 | 5.84 ms | **1.66 ms（3.5×）** | **1.19 ms（4.9×）** |

结论：真实游戏规模下两后端都远离帧预算（这正是 M1「先 OOP、接缝留后路」决策的事后印证——**不该第一天上 DOTS**）；规模再推一个数量级后 Reference 线性逼近帧预算，Ecs 曲线平缓、留出约一个数量级余量。编辑器 job 安全检查对 ECS 路径抽税 ~30%，玩家包数字以「安检关」列为准。

### Play 冒烟（编辑器，Ecs 后端）

启动 → 标题 → 战斗（托管自动 10 波、166fps、残骸烘焙正常、HUD 性能行直读 `Ecs·模拟 0.16ms`）→ 撤离 → 结算 → 回标题 → 停 Play：全程零错误；战斗场景卸载时 `World` 随 `IBattleSim.Dispose` 干净销毁（`World.All` 复查无残留）、无 Native 容器泄漏告警。框架侧（PlayMode 测试套）回归全绿。

## 边界与待议记录

1. **Burst × HybridCLR 边界**（计划内验证项）：ECS/Burst 程序集必须 AOT、永不入热更列表；热更程序集调用其公共 API 是普通「热更调 AOT」。玩家包下次构建需照常重跑 HybridCLR Generate All（AOT 引用 link.xml 会把 Sim.Ecs 保进包）；本里程碑为编辑器侧验证，玩家包回归搭下次构建顺带做。
2. **Entities 包的常驻开销**：包装上后 Play 模式自动创建 `Default World` + `LoadingWorld × 5`（子场景流送系统），本项目不用 subscenes、它们全程闲置。可用 `UNITY_DISABLE_AUTOMATIC_SYSTEM_BOOTSTRAP_RUNTIME_WORLD` 关闭；暂保留默认（闲置成本小，避免与编辑器工具交互出意外），有实测负担再关。
3. **框架零改动**：本里程碑没有触发任何框架接缝修改——「System 驱动 + Model 推送 + 事件翻译层」的既有原语原样接住了 ECS 后端。**框架暂不需要 DOTS 专用模块**；若未来出现第二个消费方、可复用样板成形（World-per-Context 生命周期助手、ECS↔R3 桥接），按五件套节奏另立 ADR 做成可选包（asmdef versionDefines 门控 Entities 依赖），当前做了就是过度设计。
4. **尸堆减速场**（此前留给 M6 的候选压测负载）：会引入 N 敌 × M 残骸交互——对「验证接缝置换」无增益，纯为压测加规则不值当，M6 放弃（合成压力负载已覆盖规模问题）。**（M7 反转：ADR-0031 把它扶正成规则本体——不为压测，而为让残骸有防御地形意义 + 把真实玩法推进"OOP 会掉帧"的量级；用均匀密度网格避开了当初担心的空间分区，两后端 O(1) 同实现。）**

## Consequences

- 垂直切片 **M0–M6 全部完成**：13 模块整合验收 + 消费边界验证 + DOTS 融合验证三重目标闭环，UPM 抽包（ADR-0010）前置验收完毕。
- `BattleSimBackend` 枚举成为活的教学样本：同一接缝后 OOP 与 DOTS 两个后端并存、Inspector 一键切换、HUD 实时读数对比。
- 对拍 harness 姿势（关 Burst 锁逻辑、开 Burst 验规格）沉淀于 outpost-tech-notes，供后续任何「同题双实现」复用。
