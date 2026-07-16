# Outpost 技术实现笔记 —— 关键机制怎么实现、为什么这么实现

> 给谁看：**想在 Outpost 上继续加功能、或想搞清"炮塔到底怎么打中敌人的"的人。**
> 这份文档是 [outpost-guide.md](outpost-guide.md) 的补充：guide 讲"看得见的现象 ↔ 框架能力 ↔ 源码位置"，本文讲**关键实现的技术方案与取舍**——命中怎么判、目标怎么切、开火模型怎么算、为什么不用碰撞检测。
> **约定：后续每加一个非平凡功能，就在文末「功能技术方案档案」追加一节**（现象 / 方案 / 为什么这么选 / 落点），让这份文档长成 demo 的实现教程。

代码位置：模拟内核 [`Assets/Game/Outpost/Sim/`](../Sim/)（零引擎依赖纯 C#）、表现层 [`Assets/Game/Outpost/Scripts/Battle/`](../Scripts/Battle/)。

---

## 1. 一句话心智模型

**炮塔开火是 hitscan（瞬时命中），不是发射会飞的子弹。** 场上没有"子弹"这种实体——伤害在开火的那一 tick 直接结算给"炮口方向锥内的最近敌人"，曳光只是**开火之后同帧才画的纯装饰**。理解这一点，下面所有机制都顺了。

为什么是 hitscan 而不是真子弹：见 [§5 为什么不做子弹碰撞检测](#5-为什么不做子弹碰撞检测关键取舍)。

---

## 2. 命中判定：怎么算"打中了谁"

全在 [`ReferenceBattleSim.TickPlayer`](../Sim/ReferenceBattleSim.cs) 里，没有物理、没有碰撞体、没有射线投射：

1. **锁定主威胁（决定往哪转）**：`FindNearestInRange()` 线性扫存活敌人列表，取"射程内、离基地最近"的那只——它是炮塔想咬住的目标，炮口按回转速度逐帧转向它。
2. **逐发结算命中（决定打中谁）**：每开一发，`FindNearestInCone(炮口角, ±容差)` 取"射程内、且与炮口夹角 ≤ 容差(6°)的最近敌人"。**炮管指着谁就打谁**——不必是全局最近，回转途中炮口扫过的其他敌人照样被这一发命中。
3. **扣血 + 判死**：命中即 `DamageEnemy(index, 攻击力)` 直接减血。`Hp <= 0` 判死，从存活列表 **swap-remove**（末位补位保持列表紧凑，索引顺序因此会变——跨帧跟踪敌人用实例 `Id` 不用索引），`Kills++`、按原型加分，发 `EnemyHit(killed:true)`。

> 关键点：**"锁定往哪转"和"这发打中谁"是两件事**。炮塔转向全局最近的主威胁，但每一发打的是炮口锥内的最近者。于是"边转边扫"是涌现的：炮口扫过的敌人顺带被清掉，而不是傻等转到主威胁头上才开火。

### 目标切换是"涌现"的，没有"当前目标"这个状态

内核**不保存**"我正在打的那只敌人"。每一发都重新 `FindNearestInCone` 查一次。所以某敌人一死（被移出列表），下一发自然指向新的最近者——"切换目标"不是一段显式逻辑，是"每发重查"的副产品。这让代码没有"目标已死但还在打空气"的悬空引用问题。

---

## 3. 开火模型：无上限射速、边转边扫

> ⚠ **本节含历史模型，读作概念背景**：下面「火墙 vs 点射」射速分档、"空放不结算伤害"、`FindNearestInCone` 锥选敌都是 **M7 之前的 hitscan 语义**（M7 改真弹道后命中由弹丸扫掠碰撞决定，见 [§7 M7 档案节](#2026-07--m7-真弹道碰撞--残骸减速泥地--推挤adr-0031)）；**2026-07-12 起**又取消了点射/火墙区分——不分射速一律边转边打（见 [§7 开火统一档案节](#2026-07--开火统一不分射速一律边转边打)）。

近防炮手感的两段，全在内核（表现层只读 `TurretAngle` 画炮管指向）：

- **射速无上限**：攻速升级 `攻击间隔 ×0.85` 可无限叠，间隔只留极小下限（`0.0008s`，防除零/单帧病态循环）。后期能飙到每分钟数万发。单帧可多发（`MaxShotsPerTick=64` 兜底）。
- **边转边扫（火墙）**：有效射速间隔低于 `FirehoseFireInterval(0.06s)` 即进"火墙"模式——炮口在转向途中**也持续击发**：
  - 炮口锥内有敌 → 正常结算命中（发 `TurretFired(hit:true)` + `EnemyHit`）。
  - 炮口锥内为空但射程内还有敌 → **空放**：射向炮口方向、**不结算伤害**，发 `TurretFired(hit:false)`，画出扫掠的火舌。
  - 低射速（间隔 ≥ 0.06s）则回到"瞄准后才发"的点射：锥内为空就本 tick 停火、下 tick 把炮口转过去。两种手感在射速升上来时自然衔接。

> **内核不含"点火渐变"**：早期内核有一个射速预热系数 `SpinUp`(0..1) 做"咬上目标缓升"。但无限模式里目标常年在射程内、它几乎恒为 1、早已不是平衡杠杆，遂从内核移除——"点射收拢 → 火墙涨亮铺开"的辉光/散射渐变改由表现层按 `TurretFired` 击发节奏**自算火力热度**（见 [§4](#4-事件--表现的翻译两条独立事件线)）。内核回归纯逻辑、零表现状态，也少一处横切耦合（有效射速计算 / 火墙阈值 / 散射 / 辉光原本都串着这个恒为 1 的值）。

**空放为什么不结算伤害**：空放那一发炮口方向本就没有敌人（有的话就是命中分支了），所以它对数值平衡近乎无影响，纯粹让火力连续、让回转升级更有价值（回转越快，炮口越快扫到下一群，空放占比越低）。

### 封顶与"无限模式如何收尾"

攻击 / 射程 / 回转都有上限（到顶后业务侧把对应升级移出三选一），**唯独射速不封**：

- **攻击封顶** → 敌人不再被秒杀，得多发命中，火力压力交给射速。
- **回转封顶** → 这是无限模式能"越来越难直到失守"的关键机制。射速无上限意味着单方向火力无限，但**回转有限意味着炮口扫不过 360° 的密集来袭**——远端方向必然漏怪，击杀率随敌人数量渐降，最终被数量压垮。若回转也无上限，炮塔后期横扫无限快、永远 100% 拦截、根本死不了（这是"近 100% 击杀 + 回血"的双稳态陷阱）。**用回转上限把收尾变成"靠数量"而非"靠血量"，是这套数值的核心 insight。**

---

## 4. 事件 → 表现的翻译（两条独立事件线）

内核只发领域事件，[`BattleDirectorSystem`](../Scripts/Battle/BattleDirectorSystem.cs) 翻成 Unity 表现。**开火与命中刻意拆成两条事件**：

> ⚠ **M7（ADR-0031）修订**：改真弹道后 `TurretFired` 只带 `Direction`（删 `aim`/`hit` 字段——命中由物理决定），`EnemyHit` 改在**弹着帧**触发。下表是 M7 前的 hitscan 语义，读作历史；现状见 [§7 M7 档案节](#2026-07--m7-真弹道碰撞--残骸减速泥地--推挤adr-0031)。

| 事件 | 语义 | 驱动的表现 |
|---|---|---|
| `TurretFired(aim, hit)` | 炮管吐了一发（命中或空放都发） | 炮口闪光、后坐、曳光（含火墙里转向途中的空放火舌） |
| `EnemyHit(enemy, killed, splash)` | 某敌人挨了打 | 敌人白闪 / 击杀爆炸 / 近距拦截溅射警示 |
| `EnemyDetonated` | 敌人抵达基地自爆 | 基地震屏 + 掉血飘字 + 来袭弹爆炸 |

一发命中会**同时**触发 `TurretFired(hit:true)` 和 `EnemyHit`——前者画枪口/曳光，后者画敌人反应。拆开的好处：转向途中的空放只有 `TurretFired(hit:false)`、没有 `EnemyHit`，于是"边转边扫"的火舌能画出来，而敌人反应逻辑不受污染。

**三个表现细节**：
- **火力热度（辉光）**：表现层从 `TurretFired` 的击发间隔推断当前射速——密集(火墙)→热度趋 1、稀疏(点射)→趋 0、停火 `FireIdleTimeout` 后→归零，平滑趋近（`UpdateFireHeat`）后驱动炮塔核心亮度与下面的曳光散射。这是内核 `SpinUp` 移除后"点火感"的新归宿：内核只发"击发了"的事实，渐变留表现层插值——同一份击发事件既画枪口、又当射速表用。
- **曳光散射**：`FireBurst` 给曳光终点加了随**火力热度**增大的随机抖动（`TracerScatter`）——高射速下密集连发才不会叠成一条直线，读成一片弹雨；点射时热度低几乎不散、单发看着准；命中冲击仍落在真实弹着点、不随散射抖。纯表现，不碰内核命中。
- **每帧特效预算**：`HitFxPerFrame` / `KillFxPerFrame` 限量，超预算的发次只结算伤害不出特效（防对象池爆 + 刷屏）；但击发节奏仍逐发记录，热度不受预算降级影响。这也是 OOP 后端在弹幕级吞吐下"看得出压力"的地方——**这是 M6 换 ECS 想在同场景压出差异的看点，不是要藏起来的缺陷**。

---

## 5. 为什么不做子弹碰撞检测（关键取舍）

> ⚠ **本节结论已被 M7 修订（2026-07-12，见 [§7 的「M7 真弹道」档案节](#2026-07--m7-真弹道碰撞--残骸减速泥地--推挤adr-0031) 与 ADR-0031）**：hitscan 已改为**飞行弹 + 扫掠碰撞**。保留本节是因为它诚实记录了当时的取舍——而且它结尾"什么时候才该重新考虑真子弹"精准预言了 M7 的动机（把规模压力推向另一个数量级、让后端差异肉眼可见）。当时的判断在**当时的目标（真实规模两后端都够快）下成立**；M7 改变的是目标（刻意把真实玩法推进 Reference 会掉帧的量级），不是当时推理有错。下面读作历史。

> 这一节回答一个自然会冒出来的问题：既然曳光在"飞"，飞行途中新出现 / 移入弹道的敌人要不要算命中？是不是得上碰撞检测？

**（M7 前的结论）不做碰撞检测，也不做会飞的子弹实体。当前的"逐 tick 炮口锥内 hitscan"就是更好的办法。** 理由：

1. **敌人不会凭空出现在弹道中间**。敌人只在竞技场边缘（半径 13）出生、径直向内走；炮塔只打进了射程（≤11）的敌人。所谓"飞行途中新出现的敌人"，其实是从射程外慢慢走进来的——等它进了炮口锥，炮塔的**连续逐 tick 开火**下一发（高射速时几毫秒后）就打中它了。曳光那 0.2s 的视觉飞行与伤害无关。
2. **连续重算 = 天然覆盖新来者**。伤害每 tick 对"当前锥内最近敌人"重新结算。高射速下炮口锥每帧重新求解，等于对锥内做"连续光束"——任何进入锥内的敌人都在 ~1 帧内被打中，无需追踪某一发飞行中的子弹。
3. **没有提前量（设计已定）**。真子弹飞行时间唯一的玩法价值是"打提前量 / 快敌人能闪弹"——而这套明确不要提前量。上了真子弹反而会**引入"快敌人穿过弹道没被打中"的诡异现象**，是负优化。
4. **接缝一致性 & 成本**。本 demo 的压力轴是**海量敌人**（成千上万只），不是子弹——炮塔只有一门。hitscan 让子弹实体数恒为 0，压力诚实地压在敌人上（列表扫描 / 特效池），也让未来的 ECS 后端能"同题对比"（同样 hitscan 规则、不同数据布局）。若一个后端有飞行子弹、一个没有，接缝的对比意义就废了。

**什么时候才该重新考虑真子弹**：如果哪天要做"抛物线炮弹 / 可被拦截的敌方子弹 / 打提前量的狙击"这类**把飞行时间当成玩法机制**的设计——那时才值得引入弹道实体 + 空间划分碰撞。在那之前，hitscan 是正解。

---

## 6. 确定性与"跑数"验证（无需进 Play）

内核是确定性纯 C#（唯一随机源是种子化的出生角度），所以**平衡可以在编辑器里跑数调**，不用进 Play 肉眼看。这也是 AI 能自验的方式。

从磁盘 `.bytes` 直接构造配置表 + 手搓一个"自动玩家"跑到失守，就能量出"死在第几波 / 击杀多少 / 峰值同屏 / 终局射速"。经 Unity MCP `unity_execute_code` 跑（execute_code 会把代码包进方法体，**顶部不能写 `using`，类型全限定**）：

```csharp
var dir = "Assets/Game/Outpost/Res/Configs/";
System.Func<string, Luban.ByteBuf> loader = n => new Luban.ByteBuf(System.IO.File.ReadAllBytes(dir + n + ".bytes"));
var cfg = new OutpostCfg.Tables(loader);
var setup = Game.Outpost.Battle.BattleSetupFactory.Build(cfg, 777);
var sim = new Game.Outpost.Sim.ReferenceBattleSim();

int rounds = 0, hits = 0;
sim.TurretFired += e => { rounds++; if (e.Hit) hits++; }; // 命中 vs 空放占比

sim.Start(setup);
// …逐 tick sim.Tick(1f/60f)；WaveCleared 时按策略 ApplyModifier + BeginNextWave；
//   直到 sim.Phase == BattlePhase.Defeat，读 sim.WaveIndex / sim.Kills 出报告。
```

> 注意：**"自动玩家"的选牌策略直接决定结论**。同一套数值，只堆生存不堆攻速 → 早早 under-DPS 死在 W24 从没进火墙；自适应地追攻速/回转 → 能进火墙、每分钟数万发、靠数量在 W130+ 收尾。所以跑数是"给某种打法的手感预判"，最终仍以真人 Play 手感为准。

---

## 7. 功能技术方案档案（按时间追加）

> 后续每加一个非平凡功能，在此追加一节：**现象 / 方案 / 为什么这么选 / 落点**。

### 2026-07 · 近防炮火墙：预热 + 无上限射速 + 边转边扫 + 曳光散射

- **现象**：炮塔像近防炮——咬住目标后射速缓升，后期每分钟数万发；炮口边转边喷、扫过怪群拉出火舌；高射速时曳光是一片弹雨而非一条线。
- **方案**：射速 = 基础 × `SpinUp` 预热系数、无上限；命中取"炮口锥内最近敌人"（指哪打哪）；火墙模式下炮口未对准也持续空放画火舌；曳光终点按预热强度随机散射（纯表现）。开火(`TurretFired`)与命中(`EnemyHit`)拆成两条事件。（`SpinUp` 后被移出内核、下放表现层为"火力热度"，见下一条。）
- **为什么**：见 [§3](#3-开火模型无上限射速边转边扫)（射速不封 + 回转封顶 = 无限模式靠数量收尾）与 [§5](#5-为什么不做子弹碰撞检测关键取舍)（hitscan 而非真子弹）。散射与空放火舌都是为了让"海量子弹"在视觉上读得出来，同时不动内核确定性。
- **落点**：[`Sim/ReferenceBattleSim.cs`](../Sim/ReferenceBattleSim.cs)（`FindNearestInCone` / 火墙循环 / `BarrelPoint`）、[`Sim/IBattleSim.cs`](../Sim/IBattleSim.cs)（`TurretFiredEvent`）、[`Scripts/Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（`OnTurretFired` / `FireBurst` 散射）。

### 2026-07 · 托管模式 + 预热下放表现层

- **现象**：HUD 右下角一个"托管：开/关"按钮，点开后波间三选一的卡片亮相约 0.6s 便自动选定、玩家纯观战；再点即回手动。同时炮塔"点火缓升"的辉光/散射照旧，但内核里已不再有预热系数。
- **方案**：① 托管——`UpgradeModel.AutoManaged`(RP<bool>) 单写（`SetAutoManageCommand` → 导演），导演在等待抉择时按优先级 **攻速>转速>探测范围>血上限>回血>攻击** 自动选牌；`AutoManageToggleView` 只读订阅回显、点击发命令，读写分离。② 预热移除——内核删掉 `SpinUp`/`SpinUpTime`/`SpinDownTime`，改由表现层 `UpdateFireHeat` 从 `TurretFired` 击发间隔自算"火力热度"驱动辉光与散射。
- **为什么**：托管把"自动玩家"从跑数工具变成可玩的观战开关，直观演示"卡片策略决定成败"；优先级取自 [§6](#6-确定性与跑数验证) 的结论（纯攻速流自养血、生存卡可不选）。预热下放见 [§3](#3-开火模型无上限射速边转边扫) 的注——它在无限模式恒为 1、已非平衡杠杆，留在内核只是横切耦合；移到表现层后内核零表现状态、确定性更干净，手感一点不丢。
- **落点**：[`Scripts/Battle/UpgradeModel.cs`](../Scripts/Battle/UpgradeModel.cs) / [`UpgradeCommands.cs`](../Scripts/Battle/UpgradeCommands.cs)（`AutoManaged` + `SetAutoManageCommand`）、[`BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（`SetAutoManaged` / `AutoPickUpgrade` / `AutoPriority` / `UpdateFireHeat`）、[`AutoManageToggleView.cs`](../Scripts/Battle/AutoManageToggleView.cs)、场景 `OutpostBattle` 的 HUD 按钮；预热移除动了 [`Sim/IBattleSim.cs`](../Sim/IBattleSim.cs) / [`ReferenceBattleSim.cs`](../Sim/ReferenceBattleSim.cs) / [`BattleSetup.cs`](../Sim/BattleSetup.cs) / [`BattleSetupFactory.cs`](../Scripts/Battle/BattleSetupFactory.cs)。

### 2026-07 · 难度模型三机制 / 敌人海实例化渲染 / 残骸层 / 撤离 / uxml 窗口

这批"基础打磨"改动的方案详述已收进导读的[三个最值得看的设计](outpost-guide.md#三个最值得看的设计)（难度模型 = 波间维修 + 规模平台期 + 全成长封顶；表现 = 实例化渲染 × 对象池分工 + 残骸烘焙；撤离 = 稳态下一局的常规收束），此处只登记不重复：三个 Toolkit 窗口同批迁到 `Res/UI/` 的 uxml + uss（`[UIWindow(Asset)]` 按名加载——框架 uxml 窗口分支的首次实战），战斗开局支持 `_startWave` 无头快进。

### 2026-07 · M3 收尾：音频 + 本地化 + 设置窗（§27/§29/§30 消费落点）

- **现象**：标题/战斗双 BGM 交叉切换；拦截爆炸/基地受创/波间维修/新波警报/选卡确认各有其声、火墙轰鸣随射速涨落；标题页「设置」弹窗三条音量滑条即改即生效、中英文一键切换且**下层标题页文案同帧跟变**；全部游戏 UI 双语，设置跨会话保留。
- **方案**：① BGM——`OutpostAudioSystem`（根 Context）订 `FlowChangedEvent` 一个事件按宏观阶段 `PlayMusic`（单通道交叉淡变 + 同曲幂等，不侵入任何 FlowState）。② 战斗音效——全部接在导演的事件翻译层：爆炸类**按时间限流** + 随机音高（千级击杀不糊成噪声墙；起初按"每帧 1 发"限流，后因高帧率下失控改按时间，见下一节的截断修复），受创重音复用既有的 0.25s 伤害聚合窗口天然节流。③ 火墙循环音——**不走框架音效池**：炮塔运行时挂 `AudioSource` 逐帧调制音量/音高（热度驱动），组音量 × 主音量手动乘回接上设置滑条。④ 本地化——`TbL10N` 表（一行一 key 一列一语言）+ 十行 `OutpostTextSource` adapter；Toolkit 侧 `Bag.BindLocalizedText`、TMP 侧 `CombineLatest(数据, Locale)`；upgrade 表 name/desc 存本地化 key；字体链 `MonoLocaleFonts` 挂根 Context。⑤ 设置——音量/语言的**运行时真源就是两个 Utility 自身状态**，`SettingsWindow` 只是遥控器；关窗一次 `SaveSettingsCommand` 落盘 `outpost/settings`，`BootState` 启动回灌；不设 SettingsModel（不做第二份内存状态）。全部音频为程序化合成 wav（[`Tools/gen_outpost_audio.py`](../../../../Tools/gen_outpost_audio.py)，固定 seed 可复现），BGM 走全小调进行 + 低音 drone/心跳，刻意退到氛围层不抢音效。
- **开火音的分层设计（射速跨三个数量级 2→250 发/秒）**：低射速段逐发单响 `sfx_shot`（每一炮听得清、随机音高防机械感）；高射速段循环轰鸣（上面的火墙层）。物理依据是人耳对 >~15Hz 的重复事件听成连续音——单发层设**最小重触发间隔** 0.08s（≈12 发/秒以上开始丢发，丢发不丢听感、也不打爆 voice），并随火力热度让位（热度近满归零）；循环层音量走**热度平方**（低速段收敛、高速段全量），两层在中段交叉过渡。这是 minigun 类武器音效的标准做法（transient 层 × loop 层 crossfade）。
- **为什么**：`AudioHandle` 刻意不提供播放中调制——"跟随对象的持续音源用引擎组件"是框架划的界（ADR-0022），火墙音正好踩在界上，成为这条分界的实战注脚；音效限流复用表现层已有的"每帧演出预算"心智，声音和特效同一套海量纪律。**⚠ 时序坑（接缝观察）**：`BindLocalizedText` 的刷新信号只有 `Locale`，文本源（配置表）异步后到**不会**触发重绑——绑定先于配置就绪 = 裸 key 定格在屏上。业务解法 = `BootState` 进标题前 `await` 配置 Ready（Failed 也放行：裸 key 上屏是可见的缺失报告，好过卡启动）。
- **落点**：[`Scripts/Systems/OutpostAudioSystem.cs`](../Scripts/Systems/OutpostAudioSystem.cs)（BGM 导演）、[`Scripts/Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（`PlayBoomSfx` 限流 + 各事件音）、[`Scripts/Battle/TurretView.cs`](../Scripts/Battle/TurretView.cs)（`InitFireLoop`/`SetFireLoopLevel`）、[`Scripts/Config/OutpostTextSource.cs`](../Scripts/Config/OutpostTextSource.cs)、[`Scripts/OutpostLocales.cs`](../Scripts/OutpostLocales.cs)、[`Scripts/Windows/SettingsWindow.cs`](../Scripts/Windows/SettingsWindow.cs) + [`Res/UI/SettingsWindow.uxml`](../Res/UI/SettingsWindow.uxml)、[`Scripts/Save/OutpostSettings.cs`](../Scripts/Save/OutpostSettings.cs) / [`SettingsCommands.cs`](../Scripts/Save/SettingsCommands.cs)、[`Scripts/Flow/BootState.cs`](../Scripts/Flow/BootState.cs)（配置就绪门 + 回灌）、[`Configs~/Datas/l10n.json`](../Configs~/Datas/l10n.json)、场景 `OutpostGame`（Systems 节点 `OutpostAudioSystem` + Fonts 节点 `MonoLocaleFonts`）。

### 2026-07 · 战斗音效二轮：弹着对拍 / 击中·击毁分音 / 回转伺服音 / 截断修复

- **现象**：低射速下能听清完整因果链——炮响（炮口）→ 曳光飞行 → 弹着（命中"叮"或击毁"轰"，两种声音不同）；炮塔大幅调头时有电机甩转的伺服音（追踪微调无声），甩得越快音越尖；音效不再有"播一半被掐"的截断感。
- **方案**：① 弹着对拍——内核是 hitscan（伤害击发帧已结算），但曳光按 55 单位/秒定速飞行、弹着晚 0.07~0.15s 可感知：命中/击毁音按「炮口到弹着点距离 ÷ 曳光速度」进**待播队列**（`ScheduleSfx`/`FlushPendingSfx`），到点才响；视觉爆点仍同帧（消隐/残骸由 sim 状态直驱无从延迟，音频滞后 0.1s 在影音容差内、音频先行才穿帮）。② 分音——击中未击毁新增 `sfx_impact`（高频金属短鸣，是"弹药落在装甲上"的质感层），与击毁 `sfx_explosion`（低频轰）频段错开；同样弹着对拍 + 最小重触发限流。③ 伺服音——`TurretView` 挂第二个循环 `AudioSource`，从 pivot **实际角度变化自测角速度**（导演只管摆角度，"摆多快"表现层自己量），30 度/秒以下静音、320 度/秒拉满，音量/音高随速度调制——与升级项「回转伺服」互为注脚。
- **截断修复（两个病根）**：**真截断**——爆炸音原按"每帧限 1 发"，编辑器实测 165fps 即每秒最多 165 发、0.6s 尾巴叠几十个并发 voice，冲破 Unity **32 实声道上限**后引擎虚化最安静的 voice（单发/弹着播一半静音）；改按时间限流（0.08s 间隔，峰值并发实测 6）。**听感截断**——打击类音色原用多项式包络 `(1-t)^p`（带斜率撞零、总长 0.11~0.3s），人耳对打击/爆炸期待**指数衰减余韵**，硬着陆读成被门限器掐断；三个音色重做为指数长尾（击毁 0.6s / 自爆 0.9s / 单发 0.26s），变频下扫同步改相位累积（直接 `sin(2πf(t)·t)` 会出啁啾伪音）。
- **为什么**：框架音效池本身**保证播完**（回收条件是 `isPlaying == false`，无 voice 抢占）——截断发生在引擎声道预算层，业务限流是唯一解，且**按时间限流对帧率鲁棒、按帧限流不是**（这坑对所有"每帧预算"式演出纪律是个警示：视觉预算超了丢的是当帧演出，音频 voice 超了掐的是**已在播的旧声音**，代价模型完全不同）。弹着对拍延迟音频而非视觉，是因为三方（sim 状态直驱的消隐/残骸、池化特效、音频）里只有音频能独立改时序。
- **落点**：[`Scripts/Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（`ScheduleSfx`/`FlushPendingSfx`/`FlightDelay`/`TryPlayImpactSfx` + `PlayBoomSfx` 时间限流）、[`Scripts/Battle/TurretView.cs`](../Scripts/Battle/TurretView.cs)（`InitServoLoop`/`UpdateServoLoop`，与火墙层共用 `CreateLoopSource` 和外部音量系数）、[`Tools/gen_outpost_audio.py`](../../../../Tools/gen_outpost_audio.py)（`make_impact`/`make_servo_loop` 新增，`make_shot`/`make_explosion`/`make_detonate` 指数长尾重做）。

### 2026-07 · M4 网络排行：Protobuf 全程对讲 + WS 二进制推送 + 排行榜（§32 消费落点）

- **现象**：标题/结算页可开「全服排行」弹窗（Top 10、加载中/失败重试/空榜三态、自己的署名行高亮）；结算页在本地存档并入后上传本局成绩、显示「全服排名 第 N 名」（离线/失败只是没有这一行，结算照常）；任何人刷新全服纪录时所有在线客户端收到 Toast（含自己——即时正反馈）。仅 Editor / Development Build 可见（对端是进程内 dev server，正式包整体藏入口）。
- **方案**：① 消息契约——请求/响应/推送五个消息类型 + 字段号注释即 `.proto` 契约（[`OutpostNetMessages.cs`](../Scripts/Net/OutpostNetMessages.cs)），客户端与 dev server 各 `OutpostNet.CreateSerializer()` 一份、等价共享同一 `.proto`。② 序列化——框架内核新增的 `ProtobufNetworkSerializer`（真 protobuf wire 格式、per-message 显式编解码注册、零依赖零反射），HTTP 体 `application/x-protobuf`、WS envelope 走 proto 消息 `{string type=1; bytes payload=2}` + 二进制帧。③ dev server——`HttpListener`（HTTP）+ `TcpListener` 手写 RFC6455（WS，二进制帧 opcode 0x2），照 demo 服务器结构（ADR-0028 §8 的结论直接复用）；榜单内存态、每玩家只留最好成绩、预置五条"驻军"成绩给玩家追赶目标；新纪录（榜首被刷新）向所有连接广播。④ 客户端接线——HTTP/WS 挂根 Context（推送要跨阶段可见）；`OutpostNetSystem` 维持长连接：`RegisterPush` 映射推送为事件、订 `WebSocketClosedEvent` 过滤 `!ByUser` 退避重连（guide §25 样板的首次真实消费，单飞门闩防并发两条维持循环）；上传/拉榜是命令直达 `IHttpUtility`（消息建模双轨的另一轨）。⑤ 身份——存档新增 `Callsign`（`OP-XXXX`，首次启动生成一次随档持久），排行榜按它合并成绩、高亮自己。
- **框架缺口发现与修复（M4 的核心目的）**：默认 WS envelope 是「JSON `{type, payload}` + payload **文本二次编码** + 文本帧」——Protobuf 字节过 `UTF8.GetString` 不保真，二进制格式在旧接缝下**结构性走不通**。修法 = 新增可选接缝 `IWebSocketEnvelopeSerializer`（序列化器实现它即接管 envelope 编解码与帧类型，payload 全程 `byte[]`）+ `IWebSocketProvider.SendAsync` 加 `binary` 参数；JSON 路径字节不变、零迁移。同时把手写 protobuf wire 原语（`ProtoWriter`/`ProtoReader`）上移框架——**选手写不选引库**：消息就几个，省掉 protoc 工具链 / NuGet DLL 的 AOT 与热更边界问题，字节与标准 protobuf 互通、换真库字段号对上即可（ADR-0028 2026-07 修订）。
- **为什么**：排行榜是「请求-响应 = UniTask 返回值、推送 = 框架事件」双轨建模的最小完整消费场景；dev server 进程内跑是因为**被验证的是客户端栈**，对端在哪个进程不影响验证效力（换真后端 = 改 baseUrl + 删 dev server）。验证姿势也值得记：无头测试覆盖 wire 往返/坏帧/协议演进，Play 冒烟用 `curl` 直击 dev server 的 HTTP 端点（protobuf 请求体外部构造）触发广播，事件到达用运行时探针确证——Toast 只显示 3.5s，隔着工具往返截不到图不代表没弹。
- **落点**：框架侧 [`Core/Network/IWebSocketEnvelopeSerializer.cs`](../../Framework/Core/Network/IWebSocketEnvelopeSerializer.cs) / [`ProtoWire.cs`](../../Framework/Core/Network/ProtoWire.cs) / [`ProtobufNetworkSerializer.cs`](../../Framework/Core/Network/ProtobufNetworkSerializer.cs) + `WebSocketUtility`/`ClientWebSocketProvider` 改造 + `ProtoWireTests`/`WebSocketTests` 新用例；Outpost 侧 [`Scripts/Net/`](../Scripts/Net/) 四文件、[`Scripts/Systems/OutpostNetSystem.cs`](../Scripts/Systems/OutpostNetSystem.cs)、[`Scripts/Windows/LeaderboardWindow.cs`](../Scripts/Windows/LeaderboardWindow.cs) + [`Res/UI/LeaderboardWindow.uxml`](../Res/UI/LeaderboardWindow.uxml)、`ResultState`/`ResultWindow`/`TitleWindow` 接入、存档 `Callsign`（`OutpostRecord`/`PlayerRecordModel`/`LoadPlayerRecordCommand`）、场景 `OutpostGame`（Systems 节点加 `OutpostNetSystem`）。

### 2026-07 · M5 构建收口：GameEntry 代码引导 + 热更档位 + 扩展分包 + 玩家包端到端（ADR-0029 收官）

- **现象**：Windows IL2CPP 玩家包从 BootScene 冷启动：下载/加载 9 个热更 DLL → 入口拉起 OutpostGame → 完整可玩（标题/设置/战斗/升级全在）；正式包（非 Development Build）下「全服排行」入口自动消失；设置窗多了「增援电台」扩展区——点下载真从 CDN 拉 bundle（带进度条），装完战斗 BGM 换用变体、跨会话保持；改一行入口代码只重打代码包，玩家包重启即跑新版（增量 6 文件，安装包不重出）。
- **方案**：① 启动编排——Boot 场景是 AOT 世界挂不了热更组件（框架组件也是热更的），`GameEntry.Enter` 用代码搭最小引导资源栈：`MonoGameContextBase` + `AssetUtility` 双 `AddComponent`（DDOL，Context 在前）→ `Configure`（编辑器 EditorSimulate / 玩家包 Host + CDN）→ `Initialize` → `LoadScene`（Single，卸掉 Boot 场景）→ `Destroy` 交棒；首场景三件套照常初始化，provider 对已初始化的包按名复用不重复拉清单。② 热更档位——`Game.Outpost` 入热更列表（9 个，自动拓扑序最后加载）；`Game.Outpost.Sim` 刻意留 AOT（M6 的 ECS 程序集只引它，不碰热更边界），「热更引用 AOT」方向合法、Generate 的 link.xml 保住仅被热更侧引用的 AOT 类型。③ 扩展分包——`OutpostExpansionPackage` 走「清单内置 + 内容 CDN」（构建 profile：ByTags + 空 tags = 只出内置清单；纯音频包必须关"内置 shader 包"开关，SBP obsolete 任务对零 shader 包会崩）；运行时配置 = 不自动初始化 + 关按需下载（Load 未缓存直接失败，强制显式下载器）；设置窗一键流程 `Initialize → CreateAllDownloader → 订 Progress → Download`，成功即落盘 `ExpansionInstalled` 标记，启动回灌时后台补 `Initialize` 复原会话（音频侧按包状态懒加载变体、载不到静默回落默认曲）。④ 数据形态——变体 BGM 独立生成脚本 [`Tools/gen_outpost_expansion_audio.py`](../../../../Tools/gen_outpost_expansion_audio.py)（主脚本 import 即生成且共享 RNG 序，扩展内容必须独立脚本独立 seed）。
- **接缝发现三连（M5 的核心目的，全部发现即修）**：**a. EditorSimulate 进玩家包炸**——模拟模式分支是 `#if UNITY_EDITOR` 编译的，单一运行模式字段没法表达「编辑器模拟 + 玩家包 Host」→ 框架给 `AssetSystemConfigModel` 拆「编辑器 / 玩家包」两模式字段 + 启动校验。**b. `AssetUtility.Configure` 是 internal**——入口在首场景前没有代码化资源初始化路径 → 提升 public（guide §15 补引导栈样板）。**c. uxml 从 bundle 加载失败**（`Should not occur! Internal logic error`）——UI Toolkit 各元素嵌套的 `UxmlSerializedData` 只被反序列化引用，IL2CPP 裁剪掉（Unity 6 已知问题）；随包场景无任何 Toolkit 组件的项目（热更档位必然）必命中 → 框架 `UI.Toolkit/link.xml` 整体 preserve `UnityEngine.UIElementsModule`。此外 CI 脚本 `run-tests.ps1` 的 `[xml](Get-Content ...)` 在 PS 5.1 下对无 BOM UTF-8 按 ANSI 读、中文 CDATA 解析炸 → 改 `File.ReadAllText`。
- **为什么**：a/c 的共性是「demo 覆盖不到的部署路径」——bundle 化场景、玩家包运行模式、bundle 化 uxml 全是切片首次真实走通，这正是垂直切片的存在理由（ADR-0029 的清单即由此攒成）。扩展包选「关按需下载」而非默认按需，是刻意演示大型 DLC 的推荐形态：误 Load 不会偷偷拖下整包，下载时机/进度完全归业务 UI。玩家包验证技巧：Player.log（UTF-8 读）看引导链与错误；`SetProcessDPIAware` 后 `GetWindowRect`+`CopyFromScreen` 截玩家窗口、`SetCursorPos`+`mouse_event` 驱动真实点击——正式包没有编辑器桥，这套 Win32 姿势是玩家包 UI 流程的自动化验证底线。
- **落点**：`Assets/Game/Main/GameEntry.cs`（启动编排 + `EntryVersion` 热更标记）、框架 [`Core/Asset/AssetSystemConfigModel.cs`](../../Framework/Core/Asset/AssetSystemConfigModel.cs)（`_playerPlayMode`）/ [`AssetUtility.cs`](../../Framework/Core/Asset/AssetUtility.cs)（`Configure` public）/ [`UI.Toolkit/link.xml`](../../Framework/UI.Toolkit/link.xml)、[`Scripts/Windows/SettingsWindow.cs`](../Scripts/Windows/SettingsWindow.cs)（扩展区）+ [`Res/UI/SettingsWindow.uxml`](../Res/UI/SettingsWindow.uxml)、[`Scripts/Systems/OutpostAudioSystem.cs`](../Scripts/Systems/OutpostAudioSystem.cs)（变体懒加载）、[`Scripts/Save/OutpostSettings.cs`](../Scripts/Save/OutpostSettings.cs) / [`SettingsCommands.cs`](../Scripts/Save/SettingsCommands.cs)（安装标记 + 启动复原）、[`ResExpansion/`](../ResExpansion/)（扩展内容）、收集器/构建 profile/热更 profile/场景 `OutpostGame`（配置四件）、[`Tools/run-tests.ps1`](../../../../Tools/run-tests.ps1)（解析修复）；全景与清单见 [ADR-0029](../../../../docs/adr/0029-outpost-vertical-slice.md)。

### 2026-07 · M6 DOTS 后端置换：EcsBattleSim + 两级对拍 + 规模基准（ADR-0030，切片收官）

- **现象**：战斗场景默认跑 `Ecs` 后端（HUD 性能行左端直读 `Ecs`），玩法与 `Reference` 完全一致；Inspector 把 `_backend` 切回 `Reference` 即回 OOP 基线，消费方零改动。同机同负载下 4.2 万敌人 Ecs 1.66ms/tick（编辑器）vs Reference 5.84ms。
- **方案**：新程序集 `Game.Outpost.Sim.Ecs`（AOT、永不入热更；引 Entities/Collections/Burst/Mathematics + Sim）。`EcsBattleSim` 自建独立 `World`、不进 player loop：移动+抵达 = 并行 `IJobChunk` 原地写 chunk、抵达者入 `NativeQueue` 主线程按 id 序重放自爆事件；开火循环 = **单线程** Burst `IJob`（顺序语义：下一发目标取决于上一发击杀）跑在每帧从 chunk 收集的快照数组上，swap-remove 后即当帧终态——快照同时充当 `GetEnemy` 的 O(1) 读源，`SwarmRenderer` 每帧全量遍历的消费模式原样成立；血量稀疏写回 + 击杀批量销毁保 chunk 权威。判定阈值抽 [`BattleSimTuning`](../Sim/BattleSimTuning.cs)、角度数学抽 [`SimMath`](../Sim/SimMath.cs)（两后端同源，防规格漂移）；回转/炮口三角函数刻意留托管（`System.Math` 与参考实现同路径）。
- **对拍姿势（同题双实现的验证方法论，可复用）**：**第一级锁逻辑**——关 Burst（`BurstCompiler.Options.EnableBurstCompilation = false`），job 走 Mono＝与参考实现同一 JIT 浮点语义，同 Setup 同 seed 同 Tick 序列逐 tick 断言全部聚合值（含炮塔角逐位）：12 波 5127 tick 全等 ⇒ 移植零逻辑偏差。**第二级验规格**——开 Burst 各自独立跑 25 波比每波聚合：前 21 波击杀/得分完全相等，22 波起（数千同屏×高射速）分叉放大为 <1% 击杀/自爆归属漂移、清波时刻漂移 ≤1 tick ⇒「同一个游戏、不是逐位同一局」。
- **⭐ 跨编译域浮点边界（M6 的关键发现）**：即便 `FloatMode.Strict`（禁 FMA/重结合），Burst 原生码与 Mono JIT 在纯加乘除/开方上仍有 ulp 级差异（疑 Mono 中间值精度提升），混沌系统必然放大——**跨编译域的逐位确定性不可承诺**，lockstep/回放级需求必须把演算收口在单一编译域。另两条纪律：编辑器 Burst 默认异步编译会**静默回退托管执行**（性能度量被污染，job 加 `CompileSynchronously = true`；与 shader 异步编译坑同款「静默降级」）；编辑器 job 安全检查对 ECS 路径抽税 ~30%（对比数字要标注安检开关）。
- **为什么**：接缝从 M1 起就为这一天准备——「先 OOP 后 DOTS」被数字反向印证：真实平台期（~1900 同屏）两后端都远离帧预算（0.38 vs 0.52ms），第一天上 DOTS 属过度设计；规模推到万级后 Reference 线性逼近帧预算、Ecs 曲线平缓出一个数量级余量。开火循环不并行化是规则保真优先（并行会改「逐发择目标」语义）；「尸堆减速场」压测候选放弃（为压测加规则不值当）。框架侧零改动——既有原语（System 驱动/Model 推送/事件翻译层）原样接住 ECS 后端，DOTS 专用模块留待可复用样板成形再立项。
- **落点**：[`Sim.Ecs/EcsBattleSim.cs`](../Sim.Ecs/EcsBattleSim.cs)（组件/三 job/后端本体单文件）+ [`Game.Outpost.Sim.Ecs.asmdef`](../Sim.Ecs/Game.Outpost.Sim.Ecs.asmdef)、[`Sim/BattleSimTuning.cs`](../Sim/BattleSimTuning.cs) / [`SimMath.cs`](../Sim/SimMath.cs)（规格共享抽取，`ReferenceBattleSim` 同步改引）、[`BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（枚举 `Ecs` 分支）、场景 `OutpostBattle`（`_backend` = Ecs）、`Packages/manifest.json`（com.unity.entities 1.4.7）；全景见 [ADR-0030](../../../../docs/adr/0030-outpost-ecs-battle-backend.md)。
- **后端切换入口（随后补）**：设置窗「战斗后端」二选一——真源 [`BattlePrefsModel`](../Scripts/Battle/BattlePrefsModel.cs)（根 Context 跨局常驻），窗口经 `SetBattleBackendCommand` 写 / `GetBattleBackendCommand` 只读订阅高亮（View 不碰 Model 的标准姿势）；导演每局开局采样一次＝**下一局生效**（模拟是一次性实例、不做局中热切——状态迁移含 RNG 内部态，不值得为演示开关加接缝）；持久化进 `OutpostSettings.BattleBackend`（-1=未选过，兼容老存档缺字段）。同批把托管优先级改为**增程雷达最优先**（射程仅两级却决定拦截窗口物理上限；无头 60 波重验证：稳态与旧顺序相同，仅改变成长期节奏）。

### 2026-07 · M7 真弹道碰撞 + 残骸减速泥地 + 推挤（ADR-0031）

- **现象**：hitscan 改为**飞行弹**——炮口吐出的青色弹丸真的在飞（HUD 性能行多了「弹」计数），未命中的飞出场外才消散、途中仍可命中射程外敌人；密集平台期弹一出膛就撞上目标（在飞数≈0），稀疏时同屏上百。残骸首次有了模拟意义：**尸堆越厚敌人走得越慢**（减速泥地），设置窗可开「泥地热力图」直读密度格（越亮减速越狠）；敌人还会把身旁残骸**拱到一边**（纯表现）。**核心收益**：设置窗切 OOP，真实平台期后期肉眼掉帧（p95 14ms 破帧预算）；切回 Ecs 满帧——后端置换的收益从"数字"变成"手感"。
- **方案**：① 真弹道——击发逻辑不变，每发生成弹丸沿炮口方向直飞（火墙 ±2° 确定性散布，消耗 RNG 双后端同序）；命中 = **扫掠线段 vs 圆**（`SimMath.SegmentCircleHitT`，弹速 0.8u/tick 远大于炮灰半径、逐点判定会隧穿），取位移段上最早交点。`FindNearestInCone` 退役、`FindNearestInRange`（回转目标 + 停火判定）保留。② 泥地——**均匀密度网格**（不是四叉树/AABB：查询模式固定是"点采样密度"，均匀格最便宜且无浮点比较分支、两后端可逐位一致），击杀/自爆令所在 cell +1、环形上限复写，敌移速 ×= `max(SlowFloor,1−SlowPer×cell计数)`。规则本身，不是优化——两后端 O(1) 同实现。③ 事件时序——`TurretFired` 删 `Aim`/`Hit` 只留 `Direction`，`EnemyHit` 改在**弹着帧**触发、位置=弹着点；M6 那套「音频弹着对拍」人工延迟（`ScheduleSfx`/`FlushPendingSfx`/`FlightDelay`/`TracerFlightSpeed`）**整套删除**——真弹道让音画天然同帧，为对拍加的延迟随规则演进变多余。④ 表现——`SwarmRenderer` 加弹丸绘制（拖尾菱形按方向定向、1023 分批）+ 推挤通道（表现残骸网格邻格查询、原位重写已烘焙矩阵、单具漂移上限 0.8u < 格边长保证不动摇模拟记账格位）+ 泥地热力图（读 `IBattleSim.WreckGrid`/`GetWreckCellCount`）。⑤ 战斗 HUD 两个演示旋钮（都是独立 `MonoViewBase` + 命令，同托管/撤离按钮姿势）：「泥地图」开关（`HeatmapToggleView`，热力图从设置窗搬到战斗界面——它是战斗内的观察工具、不是全局配置）+「速度」循环按钮（`GameSpeedButtonView`，0.25×→0.5×→1×→2×→4× 写 `BattlePrefsModel.SimSpeed`，导演订阅它写 `Time.timeScale` 实时缩放整场、OnDestroy 还原 1×；慢放看清扫掠命中、快进看规模；不落盘＝会话内保持、重启回 1×）。
- **对拍两级（含新维度）**：关 Burst 12 波 5437 tick **逐 tick 逐位全等**，且**逐格比对泥地密度网格全等**——两条新规则移植零偏差。开 Burst 前 5 波完全相等、w6 起一次溅射击杀归属在 ulp 分叉处易主（击杀数仍逐波相等、得分固定差 5~10、清波 tick 漂移 ≤1），比 M6 的 w22 提前（弹道对浮点更敏感），但归属漂移量级不变——仍是「同一个游戏、不是逐位同一局」，复用并加了「密度网格逐格比对」维度。
- **性能对照（编辑器，Ecs 开 Burst vs Reference 纯托管）**：真实平台期（w23-24，~2920 敌 + 290 弹）Reference **p95 14.2ms** vs Ecs **5.27ms**（~2.6×，Reference 叠加渲染即破 60fps）；合成压力（慢弹拉到千级在飞：~1500 敌 + **1623 弹**）Reference **avg 39ms**（崩到 15-25fps）vs Ecs **12ms**（~3.3×）。两后端 O(P×N) 同算法，差距纯来自 Burst + 连续内存 + 并行移动。**验收目标"切 OOP 后期肉眼掉帧、切回 Ecs 满帧"用真实玩法达成，而非合成基准。**
- **平衡**：真弹道引入 DPS 交付延迟（弹飞 ~0.3s）与穿排收益（弹打穿先死目标继续命中同线后敌）；无头托管 60 波长跑仍不死、平台期单波最低血 63~77%（消耗约 3~4 成），只动 `waverole.json`（炮灰 `maxCount` 3800→4500、突袭机 120→150）补漏怪。
- **踩坑（Play 冒烟发现）**：推挤通道原按「实际拱动数」扣预算——成熟战场残骸多已到漂移上限（只被判定不被推、`continue` 不扣预算）→ 最坏扫遍全部敌人×邻格残骸（~80 万次/帧）无界。改按「**检视残骸数**」扣预算（`PushScanBudgetPerFrame=8000` 次距离判定/帧封顶）：成本单位在逐具距离判定、不在实际拱动，这样才真正封顶最坏扫描。教训与 M3「每帧预算 vs 按时间限流」同源——**预算要扣在真正的成本单位上，不是扣在"成功事件"上**。
- **落点**：[`Sim/IBattleSim.cs`](../Sim/IBattleSim.cs)（`ProjectileSnapshot`/`WreckGridInfo` + `TurretFiredEvent` 删字段 + 弹着帧 `EnemyHit`）、[`Sim/BattleSetup.cs`](../Sim/BattleSetup.cs)（`PlayerSetup` +3 弹丸参数 + `WreckFieldSetup`）、[`Sim/SimMath.cs`](../Sim/SimMath.cs)（`SegmentCircleHitT`/`WreckCellIndex`）、[`Sim/ReferenceBattleSim.cs`](../Sim/ReferenceBattleSim.cs) / [`Sim.Ecs/EcsBattleSim.cs`](../Sim.Ecs/EcsBattleSim.cs)（弹丸 `List`/`NativeList` + `ProjectileJob` + `MoveJob` 泥地采样 + 密度环形记账）、[`Scripts/Battle/SwarmRenderer.cs`](../Scripts/Battle/SwarmRenderer.cs)（`DrawProjectiles`/推挤通道/热力图）、[`OutpostMeshes.cs`](../Scripts/Battle/OutpostMeshes.cs)（弹丸/quad mesh）、[`BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（删曳光/弹着延迟、热力图订阅、弹着帧音效）、[`BattlePrefsModel`](../Scripts/Battle/BattlePrefsModel.cs)/[`BattleCommands.cs`](../Scripts/Battle/BattleCommands.cs)（热力图开关）、`BattleModel`/`BattleQueries`/`BattleHudView`（弹丸数）、`SettingsWindow`/`OutpostSettings`/`SettingsCommands`（热力图设置项）、`Configs~`（BattleGlobal +7 字段 + waverole 重标定）；全景见 [ADR-0031](../../../../docs/adr/0031-outpost-real-projectiles-wreck-interaction.md)。

### 2026-07 · M8 残骸实体化 + 推挤入模拟（让后端差距随战局拉大，ADR-0032）

- **现象（起因是用户反馈）**：切 Ecs/Reference 帧率差不够明显。病根＝平台期把敌人/弹数封顶后 Reference 耗时钉住不再恶化，差距是"恒定一段"。**残骸是全场唯一累计增长的量**——把 M7 的表现层推挤扶正为模拟规则，负载 `O(残骸×邻域敌人)` 随残骸爬到上限而增长：w12 两后端几乎持平（都 ~0.25ms），w24 残骸满 2 万时 Reference **13.8ms**（叠渲染破 60fps）vs Ecs **3.25ms**。**"越往后打切 OOP 越卡"——后端差距第一次有了时间维度。** 玩法上：敌人海犁开残骸、密度记账跟着位置走，**车辙被真的踩穿、路边堆垄**（热力图直读），减速随进攻路径自我削弱＝自平衡张力。
- **方案**：① 残骸从"只存格号的环形缓冲"升级为**逐实体 SoA**（`Pos`/`ArchIndex`/`Drift`/`Seq`/`Cell`）。② 落点＝事件点 + `SimMath.WreckRestOffset`（沿远离哨站径向滑出 + 侧向抖动，系数由 `SimMath.Hash01(seq)` **整数哈希**取）——**零三角函数、零 RNG 消耗**（M7 表现层的随机滑出上移为规则时必须去随机源，整数哈希两后端逐位一致又不动 RNG 消耗顺序契约）。③ 推挤＝每 tick 相位：每具残骸找**重叠的最近敌人**（距离²最小、平票取小实例 id＝**顺序无关归约**，故 ECS 可逐槽并行且两后端可对拍），沿"敌→残骸"推开、跨密度格时旧格 −1 新格 +1（**记账跟随位置＝车辙被踩穿**）。④ 车辙回淤（`DriftRecoverPerSecond`）：漂移预算随时间恢复，车流停就重新淤积，也让推挤负载在成熟战场持续存在（不回淤则饱和后回落、差距又变恒定）。⑤ 敌人占位网格每 tick 用 **CSR 三段式**（计数→前缀和→填充）重建；ECS 侧＝`BuildEnemyGridJob`（单线程 Burst）+ `WreckPushJob`（`IJobParallelFor` 逐槽并行，跨格记账经 `NativeQueue.ParallelWriter` 带回主线程回放，加减可交换）。⑥ **表现层净简化**：删 M7 整套表现层推挤通道（`_pushGrid`/`PushWrecksByEnemies`/扫描预算/游标），残骸层改 `SwarmRenderer.SyncWrecks` **模拟槽位镜像**（Seq 变＝换血、Pos 变＝被犁动），`SpawnWreck`/`BakeWreckInstant`/快进烘焙钩子全删——战场历史由镜像自动发现。
- **对拍两级（加"逐槽残骸位置"维度）**：关 Burst 12 波 5447 tick **逐 tick 逐位全等**——聚合值 + 炮塔角 + **每一具残骸的 Seq/位置逐位** + 泥地密度格逐格全等。开 Burst seed 777 贪心 18 波两后端**每波聚合完全相等**（"最近+id 平票"归约对浮点比 M7 溅射归属更稳，本 run 18 波内未分叉；更深波次预期仍会 ulp 分叉、量级同 M7）。
- **性能对照（编辑器，Ecs 开 Burst vs Reference 纯托管，同 seed）**：成长期 w12（~560 残骸）Reference 0.23ms ≈ Ecs 0.25ms（**持平**——推挤负载尚未累积）；平台期 w24（~20450 残骸、~2250 敌）Reference **avg 13.8 / p95 25.8ms** vs Ecs **avg 3.25 / p95 5.65ms**（**~4.3×**）。隔离实验（Reference 满 2 万残骸）：关推挤 6.6ms、开推挤 9.7ms——推挤相位单独贡献 ~3ms（单线程），正是被 ECS 并行摊平的那部分。**与 M7 的关键差别：差距从"恒定 2.6×"变成"随残骸累积拉大"。**
- **平衡**：ECS 托管 40 波不死，成长期 100% 血通过、平台期单波最低血 72~83%（消耗 ~17~28%）。残骸减速对防御是净帮助（拖慢进攻者），故消耗略低于 M7、未激进重调；只把 `wreckSimCap` 2 万→3 万（战场更密、推挤负载更足）、`WreckBodyScale` 0.85→1.0（含碎片带、蹭到边缘即拱）。
- **反转记录**：ADR-0031 §5 明写"推挤进模拟：成本高一个数量级、对规则无益——推挤是纯表现"。M8 反转它（同 M7 反转 ADR-0030 §4 的模式）——**一个决策"不做"的理由（成本高），在下一里程碑正是"要做"的理由（那成本就是要演示的后端差距放大器）**；且"记账跟随位置"给了推挤规则意义（车辙），不再"对规则无益"。判断随目标演进，反转链值得留痕。
- **落点**：[`Sim/IBattleSim.cs`](../Sim/IBattleSim.cs)（`WreckSnapshot` + `WreckSlotCount`/`GetWreckSlot`）、[`Sim/BattleSetup.cs`](../Sim/BattleSetup.cs)（`WreckFieldSetup` +3 字段）、[`Sim/BattleSimTuning.cs`](../Sim/BattleSimTuning.cs)（`WreckBodyScale` + 散布幅度常量）、[`Sim/SimMath.cs`](../Sim/SimMath.cs)（`Hash01`/`WreckRestOffset`）、[`Sim/ReferenceBattleSim.cs`](../Sim/ReferenceBattleSim.cs)（残骸 SoA + `TickWreckPush` + `RebuildEnemyGrid`）、[`Sim.Ecs/EcsBattleSim.cs`](../Sim.Ecs/EcsBattleSim.cs)（残骸 `NativeArray` + `BuildEnemyGridJob` + `WreckPushJob`）、[`Scripts/Battle/SwarmRenderer.cs`](../Scripts/Battle/SwarmRenderer.cs)（删推挤通道、`SyncWrecks` 槽位镜像）、[`BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（删烘焙钩子、`WreckCount` 改读 `sim.WreckSlotCount`）、[`BattleSetupFactory.cs`](../Scripts/Battle/BattleSetupFactory.cs)（映射 3 字段）、`Configs~`（BattleGlobal +3 字段 + simCap 提高）；全景见 [ADR-0032](../../../../docs/adr/0032-outpost-wreck-entities-sim-push.md)。

### 2026-07 · proto 生产化后续：Google.Protobuf 提炼为框架增强模块（ADR-0028 §6 再修订）

- **现象**：Outpost 的 protobuf 支持从「业务侧一次性适配器」升级为**框架默认提供的可选模块** `Game.Framework.Network.Proto`（+`.Editor`）。网络序列化现在是明确的三档：默认 JSON（零依赖）/ 内核手写 `ProtobufNetworkSerializer`（ProtoWire，零依赖零反射）/ 官方 Google.Protobuf（`.proto` 契约共享 / map / oneof / 有符号 / 浮点）。Outpost 只是这套框架能力的**样例消费方**（asmdef 加一行引用 + 一套 `ProtoConfigProfile`），换个项目要用直接搬模块。
- **方案**：① **序列化器上移** `GoogleProtobufNetworkSerializer`（Outpost 业务侧 → 框架模块 `Network.Proto`，命名空间归 `Game.Framework.Network`，走 `AssetDatabase.MoveAsset` 保 GUID，ADR-0011）——Google.Protobuf 依赖收口于模块、内核仍第三方零依赖（同 `Asset.Yoo` 的 ports & adapters 收口姿势，模块可整块删 / 抽 UPM）。② **生成管线对齐其它模块的「配置 Profile 约定」三件套**（此前是 Outpost `Editor/OutpostProtoMenu` 硬编码单一 .proto 路径）：`ProtoConfigProfile`（多套按目录配置：.proto 源目录 + 输出目录 + protoc 路径）+ `ProtoCodeGenerator`（封装 protoc CLI + **差量同步**：产出到临时目录比对，内容未变不落盘避免无谓重编译、陈旧 `*.g.cs` 连 .meta 清理免重命名后类型重复定义）+ 菜单 `SSFramework/Protobuf/*` + `ProtoConfigOverviewWindow` 专用总览 + 登记进框架配置总览 hub。生成文件统一 `.g.cs` 后缀（`--csharp_opt=file_extension`），既是产物标记也是差量同步的认领边界。③ **`RegisterFile` 整文件注册**：递归登记一个 .proto 的全部消息（含嵌套、跳过 map entry 合成类型、**递归 `import` 的依赖文件**——多 .proto 拆分 + import 是常规用法，只给顶层 file、传递闭包自动带上，共享依赖幂等跳过），替代逐消息 `Register(T.Parser)`——加消息 / import 新文件重新生成即自动纳入，无「加了忘注册」的缝。Outpost `CreateSerializer` 从 5 行逐消息注册收敛成一行 `RegisterFile(OutpostNetReflection.Descriptor)`。④ **envelope 编码零冗余分配**：`ComputeSize` 预算 + 单次精确分配 + `UnsafeByteOperations.UnsafeWrap` 免 payload 二次拷贝（此前 `MemoryStream` + `ByteString.CopyFrom` 两次分配）。
- **为什么**：这正是切片的目的——「在真实游戏开发中发现框架能力的缺口并补全」。proto 支持最初为跑通 M4 落在业务侧是对的（先验证接缝接得住真库）；验证通过后，把它提炼成框架层能力、对齐既有模块的配置/总览/差量生成惯例，才让它成为「其他开发者可直接复用的框架增强」而非 Outpost 私货。评审顺手修的实现细节（RegisterFile 补生成后加消息的缝、envelope 省两次分配、差量同步防重命名类型冲突、`Serialize(null)` 明确抛 ArgumentNullException）都属「小到实现细节」的完善。
- **落点**：框架 [`Network.Proto/GoogleProtobufNetworkSerializer.cs`](../../Framework/Network.Proto/GoogleProtobufNetworkSerializer.cs) + `link.xml` + asmdef、[`Network.Proto/Editor/`](../../Framework/Network.Proto/Editor/)（`ProtoConfigProfile`/`ProtoCodeGenerator`/`ProtoBuildMenu`/`ProtoConfigOverviewWindow`）、[`Editor/FrameworkConfigOverviewWindow.cs`](../../Framework/Editor/FrameworkConfigOverviewWindow.cs)（hub 加节）、内核 `IWebSocketUtility`/`WebSocketUtility`（`RegisterPush` 约束 struct→IEvent，proto 生产化时已改）、[`Test/`](../../Framework/Test/)（`Proto~/framework_net_test.proto` + `framework_net_common.proto`（验证 import 递归）+ `TestProtoProfile` + `GoogleProtobufNetworkSerializerTests` 8 用例）；Outpost 侧 [`Scripts/Net/`](../Scripts/Net/)（`GoogleProtobufNetworkSerializer` 迁出、`OutpostNetMessages` 改 `RegisterFile`、删旧 `Editor/OutpostProtoMenu` 与 `Scripts/Net/link.xml`）、[`Editor/OutpostProtoProfile.asset`](../Editor/OutpostProtoProfile.asset)（Outpost 那套生成配置）、`Game.Outpost.asmdef`（引用 `Game.Framework.Network.Proto`）。验证：编译 0 错 + PlayMode 333/333（新增 8 用例，零回归）。

### 2026-07 · 开火统一：不分射速一律边转边打

- **现象（起因是用户反馈）**：低射速下炮塔转向途中也持续开火了——不再"瞄准后才发"，甩枪那几发沿炮口划过战场（后期怪海里顺带耙到路径上的敌人，早期稀疏时飞向空处）。高射速手感不变。
- **方案**：删掉内核开火循环里的射速分档——原本 `AttackInterval < FirehoseFireInterval(0.06s)` 才进"火墙、转向途中也打"，否则"点射、未对准(角差 > `AimToleranceDeg` 6°)本 tick 憋火"。现改为无条件按有效射速沿当前炮口方向吐弹（`aligned`/`firehose` 判定与 `!firehose && !aligned` 门一并删除）。顺带删掉火墙 ±2° 散布——高射速扇面本就主要来自炮口在转、散布只是额外纹理；去掉后**击发不再消耗 RNG**，`_rng` 仅剩出生角度一个消费点，确定性契约更干净。两后端（`ReferenceBattleSim`/`EcsBattleSim`）逐行同改，对拍前提不变。
- **为什么**：低射速转向时憋火读成"炮塔在犹豫"，边转边打更有近防炮的连续感、也和"扫射从一开始就有"的直觉一致。统一后内核开火段塌成一个直筒循环，`BattleSimTuning` 删掉 4 个常量（`AimToleranceDeg` / `AimToleranceCosSq`（后者早随锥选敌退役、本就是死代码）/ `FirehoseFireInterval` / `FirehoseSpreadDeg`）——改动本身即简化。**取代 M7（[档案节](#2026-07--m7-真弹道碰撞--残骸减速泥地--推挤adr-0031)）引入的点射/火墙分档与 ±2° 散布**（同 M8 反转 ADR-0031 §5 的记法，判断随目标演进、留痕）。
- **平衡（待观察）**：`RotationSpeed` 升级的 DPS 权重会略降——原本"回转慢=憋火久=少打"让它是实打实的输出属性，现在转向也在打，它退向纯覆盖/命中率价值（转得快=甩枪那几发更快对上目标、飞空更少，仍非无用）。托管优先级 `AutoPriority` 里回转排第 4，暂不动、留待真人手感与无头长跑复验后再定。
- **落点**：[`Sim/ReferenceBattleSim.cs`](../Sim/ReferenceBattleSim.cs) / [`Sim.Ecs/EcsBattleSim.cs`](../Sim.Ecs/EcsBattleSim.cs)（`TickPlayer` 开火循环）、[`Sim/BattleSimTuning.cs`](../Sim/BattleSimTuning.cs)（删 4 常量）；文档 [ADR-0031](../../../../docs/adr/0031-outpost-real-projectiles-wreck-interaction.md)（规则表加修订注）。

### 2026-07 · 音频全面重写：numpy/scipy DSP 引擎 + BGM 长结构编排（"嘀嘀嘀太单调"反馈）

- **现象（起因是用户反馈）**：BGM"只有各种嘀嘀嘀的声音、太单调"。病根两个：① 旧合成器是纯标准库逐样本 for 循环，只负担得起裸正弦/三角直出 + 线性包络——无滤波、无失谐、无混响、无立体声，什么和弦进行都是蜂鸣器质感；② 循环太短——战斗曲 8s、标题 16s，几十秒就听穿。
- **方案**：新建共享 DSP 库 [`Tools/outpost_audio_dsp.py`](../../../../Tools/outpost_audio_dsp.py)（numpy/scipy 向量化，速度余量支撑真实效果链）：带限锯齿 wavetable + 失谐堆叠、Butterworth 低/高/带通 + 分块时变扫频、Schroeder 混响（反馈梳"块长=延迟"精确向量化）、乒乓回声、合唱、立体声；循环无缝从"段边界包络归零"升级为**尾部回绕**（混响/长释放尾巴叠回循环头，接缝两侧是同一段声音的延续）+ 噪声层环形淡接。BGM 重编排：战斗 48s（100BPM·20 小节 A/B/收束三段、和声每 4 小节一换、末 2 小节噪声上升器引回循环头）、标题 40s（8 和弦两轮 + 钟音动机乒乓回声 + 风底）、扩展包变体同骨架换"军用电台"皮（失谐载波/行军刷点/莫尔斯呼叫/静电底）；SFX 全部分层重做（瞬态+体腔+余韵，爆炸类加碎裂噼啪与轻过载胶合）。**每资产独立 RNG seed**——旧版共享全局 RNG 的"新音色只能末尾追加"约束作废，两生成脚本也因此可共享 DSP 库。
- **为什么**：保留的既有决策原样继承——全小调（Am/Em/Dm）、战斗紧张感靠低频压迫+心跳律动不靠快节奏亮色音型、打击类指数长尾防"截断感"；**逐资产响度对齐旧 RMS 基准（±0.6dB）**，游戏内几十处音量常数零重调。三个踩坑：① 周期信号过滤波器有启动瞬态（首尾滤波状态不同=接缝跳变 68×平均步长），修法 tile 两圈取第二圈=全程稳态；② "非谐部分音"频率必须取整数 Hz（92×2.13=195.96Hz 在 1s 循环里差 0.04 周期=接缝跳变），改 92/196/365Hz 保非谐观感；③ 无耳验证靠两个环路——脚本内 seam-report（接缝跳变/平均步长比）+ 频谱图渲染 PNG 目检（编排结构/滤波形态肉眼可辨），最终听感由用户验收。
- **落点**：[`Tools/outpost_audio_dsp.py`](../../../../Tools/outpost_audio_dsp.py)（新）、[`Tools/gen_outpost_audio.py`](../../../../Tools/gen_outpost_audio.py) / [`Tools/gen_outpost_expansion_audio.py`](../../../../Tools/gen_outpost_expansion_audio.py)（重写）、`Res/Audio/*.wav` 14 个 + `ResExpansion/Audio/bgm_battle_alt.wav` 重生成（BGM 变立体声）。运行时代码零改动（文件名/时长量级/循环契约/响度全部兼容）。

### 2026-07 · 音频二轮：战斗曲改驱动型编排 + 响度统一 + 电台战斗曲开关

- **现象（起因是用户试听反馈）**：一轮重写后战斗 BGM「和主界面一样很慢很舒缓、和战斗不搭」；要求各 BGM 与音效响度一致；「增援电台」变体希望设置里可选开关。
- **方案**：① **战斗曲反转"不靠节奏音型"决策**（2026-07-10 定，当时被嫌"吵/欢快"的是高音区快速琶音——病根是音区与音色，不是节奏本身）：120BPM 鼓组（底鼓/军鼓反拍/踩镲）+ 八分贝斯 riff（逐音低通"咬字"包络）+ 离拍和弦戳 + C 段暗色主题旋律（LP 1100 失谐锯齿、上限 E5），24 小节 A/B/break/C 四段 48s；小调、低中频为主，战斗感来自驱动力不来自亮色。构建器 `build_battle_track(rng, radio=)` 两皮共用——扩展包变体同一能量骨架换电台皮（莫尔斯呼叫替主题旋律、窄带戳、高八度载波 drone、静电噼啪底），主/变体切换能量不断档。标题曲保持舒缓但给真正的主题旋律（分句正弦揉音 + 慢琶音心率），与战斗曲拉开气质差。② **响度统一为资产级契约**：三 BGM 同 RMS（-23dBFS）、全部 SFX 同 RMS（-18dBFS）——游戏内相对混音只由播放侧音量参数负责；`OutpostAudioSystem` 的 per-曲补偿常数（Title 0.8/Battle 0.9）随之合并为单一 `MusicVolume 0.85`（那本就是给旧文件响度不齐打的补丁）。③ **电台战斗曲开关**：`BattlePrefsModel.ExpansionBgm`（默认开 = 旧"下载即启用"行为）+ `SetExpansionBgmCommand`/`GetExpansionBgmCommand` + 设置窗扩展区开关行（仅已安装时显示）+ `OutpostSettings.ExpansionBgm` 落盘快照（老存档缺字段 JsonUtility 保留初始值 true）；`OutpostAudioSystem` 订阅偏好，战斗中切换即时交叉淡变换曲。
- **为什么**：反转链留痕（同 M7/M8 记法）——"不靠节奏"在氛围曲阶段是对的，在"战斗要有战斗感"的诉求下节奏就是主角，约束真正要守的是**音区与音色的暗色**；响度从"逐资产对齐旧基准"升级为"统一契约"后，混音责任边界清晰（资产平、参数混），后续加音色不再需要逐个对响度。
- **落点**：`Tools/gen_outpost_audio.py`（战斗曲构建器 + 标题曲重编排 + 统一 RMS 常量）、`Tools/gen_outpost_expansion_audio.py`（薄壳，复用 radio 皮构建器）、`Res/Audio/*.wav` + `ResExpansion/Audio/bgm_battle_alt.wav` 重生成；[`Battle/BattlePrefsModel.cs`](../Scripts/Battle/BattlePrefsModel.cs) / [`Battle/BattleCommands.cs`](../Scripts/Battle/BattleCommands.cs)（偏好 + 命令对）、[`Save/OutpostSettings.cs`](../Scripts/Save/OutpostSettings.cs) / [`Save/SettingsCommands.cs`](../Scripts/Save/SettingsCommands.cs)（快照回灌）、[`Systems/OutpostAudioSystem.cs`](../Scripts/Systems/OutpostAudioSystem.cs)（订阅即时换曲 + 单一 MusicVolume）、[`Windows/SettingsWindow.cs`](../Scripts/Windows/SettingsWindow.cs) + `Res/UI/SettingsWindow.uxml`（开关行）、l10n 表 +3 key（`settings/expansion-bgm`、`common/on`、`common/off`）。验证：编译 0 错 + Play 端到端（设置窗行显示、战斗中 开→关→开 三段换曲、零报错）。

### 2026-07 · 音频三轮：SFX 高频层 + 真立体声摆位 + 战场方位音效 + 电台皮辨识度

- **现象（用户试听二轮反馈）**：① 射击/爆炸「太沉闷、缺高频，咚咚的像敲桌子」；② 电台战斗曲设为关后「听着好像没变化」；③ 炮塔回转声「有点低」；④ 希望 BGM 升级双声道更有层次；⑤ 希望击中/爆炸有方位感。
- **诊断**：① 打击类合成只有低频体腔层像样，crack 层增益低且被 softclip 压扁——频谱质心 sfx_shot 仅 332Hz（沉闷的数值化）；同 RMS 契约下低频占优的声音按等响度曲线听感更小更闷，双重吃亏。② **开关机制本身一直正常**（Play 里实测用户存档 `ExpansionBgm=false` 已正确持久化并生效播默认曲）——病根是两皮刻意共用能量骨架后，差异件（莫尔斯呼叫）只在 32s 后的 C 段出现、静电底 0.012 增益近不可闻，前半首两皮几乎无差别；「一致的能量」和「可辨识的身份」需要分开设计。③ 伺服 92Hz 基波在等响度曲线上天然显小声 + 音量上限 0.30。④ BGM 文件虽是双声道但主体为"双单声道"（L/R 相关 0.991≈单声道）：踩镲/戳的能量占比撼不动整体，事后摆位不如声部内展开。⑤ `PlaySfxAt` 已有但引擎默认 `minDistance=1` 是第一人称尺度，监听器（主相机 (0,0,-10)）到场心距离 10，直接用全场声音被压到 1/10 以下。
- **方案**：① shot/explosion/detonate 加宽带 crack（HP 2000~2500、提早提亮）+ 3.5~9.5k sizzle/碎裂层，fire_loop 加火焰嘶声层——质心 shot 332→1555Hz、explosion 124→482Hz；② 莫尔斯呼叫提前到 bar 0/2/13（开场数秒即可辨）+ 静电底/噼啪增益 0.012/0.04→0.028/0.08；③ 伺服加 736/1472Hz 齿轮啮合啸声层（整数 Hz 保循环无缝；随游戏侧 pitch 0.8~1.4 扫出变速感）+ 音量上限 0.30→0.38；④ 立体声分层混音：和弦戳/主题旋律**声部内展开**（和弦音/失谐声部按 pan 摆开，超锯齿宽度手法）、踩镲强弱拍左右摆 ±0.55、标题琶音随音高画弧/钟声交替落点、鼓+贝斯过短房间混响（mix 0.14/rt 0.9 保打点）——高频段（1k-8k）L/R 相关战斗 0.795/标题 0.455，低频守中 0.98（低频守中是混音惯例：相位问题+能量分散）；⑤ 框架 `PlaySfxAt` 加 `minDistance/maxDistance` 可选参数（默认=引擎默认，零行为变化；归还时复位），战斗爆炸/弹着改 3D 位置播放（min=11≈监听器到场心距离：场内基本全音量、只留方位与轻微远近差）。
- **为什么**：「响度一致」约束的正确实现是**每个音效内部做频谱平衡**，而不是全员低频化——等响度曲线决定了同 RMS 下亮的听感响、暗的听感闷；立体声宽度的正确来源是**声部摆位**（能量级差异），事后 to_stereo+微 pan 撼不动被低频主导的相关系数；变体设计要同时满足「能量一致」（切换不断档）与「身份可辨」（差异件必须在头几秒出现），二轮只做了前者。数值自检手段沉淀：频谱质心/高频占比（闷亮）、分频段 L/R 相关（宽度）、seam-jump（循环）。
- **落点**：`Tools/gen_outpost_audio.py`（SFX 高频层 + 立体声摆位 + 莫尔斯提前）、`Res/Audio/*.wav` + `ResExpansion/Audio/bgm_battle_alt.wav` 重生成；框架 [`IAudioUtility`](../../Framework/Core/Audio/IAudioUtility.cs) / `AudioUtility` / `MonoAudioUtility`（距离参数）+ ADR-0022/guide §19 同步；[`Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（Boom/Impact 走 `PlaySfxAt`，`SfxMinDistance=11`）、[`Battle/TurretView.cs`](../Scripts/Battle/TurretView.cs)（伺服音量上限）。验证：编译 0 错 + Play（三 BGM ch=2、电台开关即时换曲复验、爆炸 voice `blend=1 min=11` 位于弹着世界坐标、零报错）。

### 2026-07 · 音频四轮：爆炸性格三分 + 火墙连发脉冲串 + 开火交叉曲线修谷 + BGM 低频回收

- **现象（用户试听三轮反馈）**：① 敌人被击毁「像砰的一声打在铁皮上，没有爆炸感」（背景是近防炮拦截导弹，应有导弹爆炸感）；② 机炮还是偏小，单发「砰」缺出膛厚重感；③ **连发明显比单发小很多**；④ 受创音与击毁音分不清；⑤ BGM 低频偏高。
- **诊断**：① 三轮为治「闷」给爆炸加的 1.5~7.5k 金属 snap 层正是「铁皮感」元凶，且 0.75s 长度缺「轰隆」余韵——爆炸与敲击的听感区别一半在衰减尾巴；② 单发只有低频体腔+短瞬态，缺 200~900Hz 报告层（等响度敏感区，「胸腔感」所在）与余韵；③ 双重病根：**稳态噪声 vs 瞬态串**（同 RMS 下人耳对瞬态敏感得多，火墙循环是纯"炉膛轰鸣"天然显小声）+ **交叉曲线谷**（单发 0.87 热度就归零、循环层平方淡入且上限仅 0.55，交叉点两层合计塌陷）；④ `sfx_detonate` 语义是「哨站受创」主观反馈（受创聚合窗口播、跟随镜头），但音色与拦截爆炸同为"boom 族"难分；⑤ 二轮驱动型编排的鼓+贝斯+drone 全在低频段，低频占比 0.87。
- **方案**：① 拦截爆炸重做「导弹空爆」：去金属 snap、加低通噪声慢衰减轰隆尾（tail 能量占比 0.013→0.049）+ 尾段短混响并回 mono（3D 位置播放要求单声道），质心 482→267Hz——**这轮的"暗"是性格修正不是回退**（三轮治闷靠 crack 起爆保留，铁皮感的 snap 才是删除对象）；② 单发加 200~900Hz 报告层 + LP500 轰鸣尾，0.25→0.5s，质心落 ~900Hz（三轮 1555 太脆 / 一版试配 541 回闷，两头都收过反馈）；③ 火墙循环重做为 **25 发/秒连发脉冲串**（每发=微型炮响，整 50 发/圈无缝，pitch 0.85~1.3 扫出 21~32 发/秒转速感；逐发增益微随机防缝纫机）+ 交叉曲线配对修复（单发 0.62×(1-heat) 线性退、循环 heat²×0.9 平方进，中段合计不塌）+ 爆炸限流 0.08→0.11s（配 1.15s 新尾巴，峰值并发 voice 持平）；④ 受创重做「装甲应力」性格：深冲击 + 82/147/233/341Hz 非谐金属呻吟簇（慢音高晃动）+ 40Hz 震腔，质心 104Hz vs 空爆 267Hz——受击方=沉/暗/金属应力，击毁方=亮劈裂/散/轰隆尾，闭眼可分；⑤ BGM 总线一阶低架 `x-g·LP1(x)`（战斗 0.35@130、标题 0.28@110，一阶相移小无梳状感），RMS 归一自动把削掉的能量还给中高频——低频占比战斗 0.87→0.82、质心 129→164Hz，总响度不变编曲不动。
- **为什么**：「音效家族」的性格轴要按**游戏语义**分配而不是按响度分配——同为 boom 的三个音（单发/空爆/受创）各自锚定不同频谱质心与包络性格（900/267/104Hz），玩家闭眼靠音色辨事件；循环底噪的响度感来自**瞬态密度**而非 RMS（机炮连发本来就是密集炮串，合成时把"它是什么"做对，听感响度自然对）；交叉淡变两条曲线要**配对设计**（一进一退的和不能塌），单独调任何一条都可能挖谷。
- **落点**：`Tools/gen_outpost_audio.py`（四个音效重写 + 双 BGM 低架，构建器共用故 alt 皮自动获益）、`Res/Audio/*.wav` + `ResExpansion/Audio/bgm_battle_alt.wav` 重生成；[`Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（单发音量曲线 + Boom 限流间隔）、[`Battle/TurretView.cs`](../Scripts/Battle/TurretView.cs)（循环层上限 0.9）。验证：编译 0 错 + 频谱自检（质心三分 898/267/104、fire_loop punch 占比 0.116→0.229、BGM lo130 全降）+ Play（新 clip 时长就位、托管跑高热度段循环层 0.9×vol 生效、零报错）。

### 2026-07 · 音频五轮：火墙多档同源烘焙——低速到高速连发的物理连续体

- **现象（用户四轮后追问）**：能否让单发与连发听感一致（提议同一波形 + 超 25 发/秒才换连发音频 + 变速匹配射速）？进一步明确诉求：**尽量物理拟真低速到高速的连发声音**。试听中追加两条：高射速后音量反而变小；射速上限干脆去掉、让机器卡顿当自然上限，声音同步跟上。
- **诊断**：本作射速跨 3.3→250 发/秒（攻击间隔 0.3→0.004s）两个数量级。单循环 + 宽域变速做不到拟真：重采样把音色随重复率一起搬走（×10 变速=升 3 个八度）；四轮的 25 发/秒脉冲串与单发是两套配方（质心 898 vs 379Hz），确实"像两把炮"。物理事实：速射炮音色不随射速变、变的只是重复率——<15 发/秒离散炮响、越过 20~30 发/秒融合成蜂鸣（**基频=射速**，密集阵 75 发/秒的"BRRRT"即 75Hz）。
- **方案**：赛车引擎音按 RPM 分档采样的同一范式。① gen 侧抽共享 `shot_transient`（sfx_shot 逐字节不变），烘 **16/32/64/128/256 发/秒五档原生循环**（`sfx_fire_016..256`，2 的幂=log 域等距）；② 运行时 `TurretView.SetFireWall(rate)`：log 域三角权重选相邻两档、开方等功率交叉淡变、档内 pitch=rate/native 精确对齐射速（权重窗 ±1 档 → pitch 天然限 [0.5,2]），权重零档 Pause 省 voice；③ 导演实测射速：帧内多发用 dt/发数还原间隔（高射速下事件时间戳同帧全相等，1/interval 会失真）、指数平滑上快下缓；单发层接棒改按射速（`HandoverBlend` 6~18 发/秒带，曲线归 TurretView、两侧共用）；④ 收火余韵：高速骤停补一发全长单响（最后一发不再被掩蔽的轰鸣尾物理上就该突然可闻）；⑤ 响度增长**分摊两级**：资产级各档 RMS 递增 +1.25dB/档（-18→-13dBFS，刻意偏离"全 SFX 同 RMS"契约——运行时 `AudioSource.volume` 上限 1.0、火墙常年顶 0.9 播，增长顶不上去；且融合档调制变浅、同 RMS 听感更小，热 RMS 是感知补偿）+ 运行时 0.8→0.9 缓升。首版全放运行时（0.35→0.9 曲线）被用户当场听出"高射速反而变小"的中段音量谷，返工分摊；顶档在原生射速以上保持满权重（右侧无更高档接三角衰减的另一半），饱和段"顶住"不"滑落"。⑥ **射速去上限**（用户追加）：配置 `playerMinAttackInterval` 0.004→0——xml 既有语义"≤0=仅防除零"，内核双保险（0.0008s 兜底 + `MaxShotsPerTick=64`）保证不挂死，机器负载成为自然上限（这正是 tech-notes 早期"射速无上限"的原始设计，0.004 是后来配置化时的平衡取值；伤害/回血/血量仍封顶，射速保持唯一无界成长轴）；导演射速追踪分母下限放宽到 0.25ms（4000 发/秒）；音频表达在 **384 发/秒饱和**（顶档 256 原生 × 1.5 变速）——再往上蜂鸣基频进入哨音区、资产与响度已顶满，交给视觉密度与掉帧表达"更快"。
- **烘焙期两轮物理纠错（数值自检抓的）**：❶ 首版"压尾模拟掩蔽"——错：掩蔽是感知现象，能量物理上仍在；单发 90% 能量住在 200Hz 以下的长尾里，压尾=删低频体量，质心飙到 6.7kHz。改全长尾巴直接求和（脉冲串平均频谱=单发谱×梳状采样）。❷ 逐发全新白噪在融合档堆成嘶声地毯——宽带 crack 层占白噪功率 91%，不相干叠加功率×发数线性堆积、抹掉谐波梳；真炮每发压力波近乎相同（相干重复才有蜂鸣）。改融合档（≥64）**冻结波形**+零定时抖动，离散档（≤32）保留逐发变化防缝纫机。❸ 纯相干梳还缺低频：周期脉冲串频谱**不存在基频以下能量**（无低梳齿），256 档只剩 crack 高频梳齿——真实连发的低频来自**不锁相**成分（枪口燃气湍流怒吼 + 每发路径各异的环境混响），补随射速 ∝√发数 增长的怒吼底床 + 最高档一阶高架削。终态质心族 919/945/684/530/1214（vs 单发 898），256Hz 梳齿 5~143× 于齿间底。
- **为什么**：物理拟真的正确分解是**「相干梳（蜂鸣身份）+ 不锁相怒吼（低频体量）+ 离散档逐发变化（生命感）」三成分**，各有物理对应物，缺谁都露馅；「同一个波形」的正确实现是**同源配方 + 分档原生烘焙**，不是字面复用一个 wav（全长单发塞不进高射速周期，宽域变速搬走音色）；用户提议中"变速匹配射速"被保留为档内细调（±1 档带宽内音色形变可忽略）——采纳直觉、修正机制。
- **落点**：`Tools/gen_outpost_audio.py`（`shot_transient` 共享 + `make_fire_gears` 五档 RMS 递增，删 `make_fire_loop`）、`Res/Audio/sfx_fire_016..256.wav` 新增 + `sfx_fire_loop.wav` 删除；[`Battle/TurretView.cs`](../Scripts/Battle/TurretView.cs)（`InitFireGears`/`SetFireWall`/`HandoverBlend` 替换单循环三件套）、[`Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（`_fireRate` 平滑追踪 + 帧内多发还原 + 单发接棒按射速 + 收火余韵）；`Configs~/Datas/battleglobal.json` + `Defines/outpost.xml`（去上限）+ Luban 重生成。验证：编译 0 错 + 频谱自检（各档基频=射速、调制深度 0.78→0.24 单调降=离散→融合、质心同族 919/945/684/530/1214、256Hz 梳齿 5~143×）+ Play 实测（低速全档 Pause 纯单发；19 发/秒 16/32 档交叉淡变数值逐位吻合手算；饱和段 256→0.90/p1.00、384+ 顶住 0.90/p1.50 单调不塌；min=0 生效）。

### 2026-07 · 音频六轮：击杀爆炸方位聚合 + 战场轰鸣底床——海量摧毁的可听化

- **现象（用户反馈）**：大量敌人被摧毁，爆炸音效却很少。追问点明本质差异：敌人声音**有方位、且不均匀**——与炮台（单点、均匀）不是同一类问题。
- **诊断**：爆炸音全局限流 `BoomSfxMinInterval=0.11s`（防 32 实声道虚化，2026-07-10 引入），超出的击杀**整个静默丢弃**——千级屠杀听起来与每秒 9 杀完全一样。限流本身是对的（voice 预算是硬约束），错在"丢弃"：能量消失了，而物理上 N 记爆炸的功率是相加的。
- **方案**：**限流不丢弃，改方位聚合 + 底床分层**。① 击杀先按方位扇区记账（8 扇区 × 45°，能量=单爆音量²进功率域），冷却一过放行能量最大的扇区一声"合爆"：音量=√能量（不相干声源功率相加）、位置=扇区能量声心、聚合越大音高越沉（音量 ~4 只炮灰/窗口触顶后 pitch 继续下沉延伸表达）。**单杀数值上完全退化为逐发直播**（音量/音高/时机与旧公式一致，音画同帧不变）；未放行能量指数衰减（τ=0.35s）——猝发止息后尾焰 boom 渐弱渐停。放行速率与 voice 预算与纯丢弃版完全相同。② 新增**战场轰鸣底床 `sfx_rumble`**：与 `sfx_explosion` 同源（配方抽出 `explosion_transient`，同 `shot_transient` 之于火墙）的 4s 循环，每秒 ~22 记全长空爆瞬态**随机时刻不相干叠加** + 一阶低通 1.4k；运行时音量跟随平滑击杀率（log 域 8→240 杀/秒映射再开方，低段先给存在感）、3D 位置逐帧滑向击杀能量声心——屠杀集中在哪边，轰鸣就从哪边来。
- **为什么这是火墙的镜像**：同一物理故事的正反面。炮口串**相干**（同一门炮锁相重复→能量聚在射速的谐波梳上、有音高）→ 必须分档烘焙；战场爆炸**不相干**（各自独立的时刻/位置/反射路径→无音高的连续怒吼、密度只影响调制深度）→ 一条循环 × 音量跟随就够，火墙融合档的"冻结波形+脉冲网格"在这里反而是错的。高频回收同五轮教训：几十层宽带 crack 不相干堆积成嘶声地毯，且"远方的战场"经空气吸收本就没高频。底床 RMS 刻意压 -2dB：它垫在合爆之下，瞬态才是"炸"。
- **落点**：`Tools/gen_outpost_audio.py`（`explosion_transient` 抽出——`sfx_explosion` 逐字节不变 + `make_kill_rumble`）、`Res/Audio/sfx_rumble.wav` 新增；[`Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（`PlayBoomSfx` → `QueueBoom`/`SectorOf`/`TryFlushBoom` 三件套 + `UpdateKillAudio` 每帧推进 + 底床 AudioSource 子节点；顺带 `Update` 加 `_audio==null` 短路——编辑器工具脚本化打开战斗场景时 MCP 桥泵播放循环、未注入的服务引用会刷 NRE）。验证：`sfx_rumble` 自检（99% 能量 <400Hz、高频 0%=无嘶声地毯、包络谱无速率峰=确认无音高、接缝 0.9× 平均步长）+ Play 实测（25 波压测击杀率 531/秒：底床 0.44 音量在播、位置偏向屠杀侧、扇区能量 4 方向不均、boom 顶冷却满速放行、voices=13 远离 32 虚化线；第 1 波稀疏击杀：底床不播、扇区即时清零、单杀即时直播；全程 0 错误）。
- **试听两轮返工（"20 波后还是稀疏咚咚咚"→"依然听到清晰砰砰声"）**：实测 20 波击杀率已 450+/秒、底床增益早已饱和——问题不是没够到区间，而是两处：**底床在混音里不可闻** + **合爆层从不让位**。修法：❶ 底床可闻化：资产 99% 能量 <400Hz、RMS -20，被 -13.5dBFS 资产、2D 满音量的火墙盖 ~10dB——补 3× 中频带（700~3500，等响度敏感区的"远处炸点"纹理，>4k 仍严切）+ RMS 提到 -18.5（终态 <400Hz 84% / 可闻中频 ~15%）；spatialBlend 1→0.6（轰鸣是全场爆炸的统计和=扩散场，2D 成分保存在感、3D 成分保方位偏置），满量点 240→120 杀/秒、顶格 1.0。❷ **击杀侧接棒 `KillFusionBlend`**（与单发→火墙的 `HandoverBlend` 同构，用户第二轮反馈点破的设计不一致）：合爆不管击杀率多高都顶着 0.95 上限按固定节拍打鼓——而物理上 450 杀/秒时单爆间隔 2ms，人耳只该听到怒吼主体 + 偶发近旁重响。25→100 杀/秒沿带衰减合爆至 25% 音量（融合区退居重音）+ 放行间隔 ±(0.85~1.45)× 随机抖动防节拍器；离散区（<25 杀/秒）分毫不动。中途曾试"放行冷却随击杀率加密 0.11→0.07s"——方向反了：用户要的是融合不是更密的鼓点，已撤销。教训：**频段占比要用功率算——低频源上小增益带通等于没加**（0.5× 实测占比纹丝不动，3× 才到 ~10%）；"物理上正确的暗"不等于"混音里可闻"；**离散事件层在融合尺度必须让位**——射击侧早有接棒带，击杀侧漏了同一课。复测：478 杀/秒 blend=1.00、合爆顶格 0.24、底床 0.47 满量、间隔抖动生效、voices=14；25 杀/秒 blend=0 合爆 0.95 原样、0 错误。
- **第三轮微调（试听："合爆太小、机炮高频偏大、弹着叮叮也该合并"→"咚咚又回来了"）**：❶ 合爆接棒衰减 75%→55%→**68%**（融合区顶格 0.24→0.43→0.30）——75% 版"太小"、55% 版"咚咚又回来"（该版同时调低火墙+撤弹着叮，遮蔽变薄合爆被双重暴露），取两版听感之间；配套把"聚合体量→音高下沉"随接棒退坡；❷ 火墙响度上限回收 ~1dB（0.8/0.9→0.72/0.82）+ 256 档资产再暗化（0.32/0.68+1.8k，质心 3119→2577Hz、>4k 功率 8%→4.6%）——饱和段 ×1.5 变速把频谱再抬 50%，顶档资产得预留暗度；❸ 弹着叮沿射击侧 `HandoverBlend` 渐隐（与单发层同曲线）：高射速下弹着能量已由火墙连续表达，离散叮=纯噪音，聚合成更响的叮反而双重计账——**三个离散层（单发/合爆/弹着叮）如今各有接棒去向（火墙/底床/火墙），融合尺度上无一漏网**。试听工作流备注：用户直接在 AI 的 Play 验证会话里旁听——每轮反馈对应当时在跑的中间版本，调参时要按"上一轮验证时的状态"对表。
- **第四轮：合成模型换血（试听"合爆音量不足、且仍能清晰听到咚咚——也许大量爆炸的合并本就不该有明显咚咚"，用户点破了物理）**。前三轮在"部分让位 vs 全额让位""底床提亮 vs 压暗"之间反复调参，都没根治，因为**病根不在音量分配，在合成路径**。两处定案：❶ **合爆融合区全额让位**（`volume *= 1 - blend`，与单发层→火墙在高射速归零同构）：人耳对瞬态的探测**不看音量**（"声音不大但清晰可闻"是瞬态可探测性的定义特征），离散炸点压多小声都能被追踪——高射速下唯一正确的做法是让它彻底消失，不是压小。门控在扇区清账之前 `return`，能量自然衰减，击杀率回落 blend 松开时残余合爆立即复现。❷ **底床从"瞬态求和"换成"噪声塑形"**：实测把逐记空爆瞬态密度堆到 300 记/秒、逐记起音抹平 60ms，低频带（<300Hz）起音陡度仍卡在 0.63（稳态噪声地板 0.35）——30~165Hz 冲击体是瞬时起音，低通滤不掉时域起音，逐记叠加的路径**从根上生"咚"**。改用中心极限定理的正解：大量随机冲击的极限 = 服从冲击频谱包络的高斯噪声，连续炮火的"怒吼"物理本质就是爆炸频谱塑形的稳态噪声。新底床 = 低频体（sub<160 + <700 二阶，~52%）+ 可闻中频（700~3500，~41%）+ 慢起伏 LFO（0.4~1.1Hz 三正弦，战场呼吸而非离散事件）+ 极少纯高频远处碎片（2~6k，无低频体故不落"咚"区）；低频起音陡度降到 0.27（**低于稳态地板=构造上无离散起音**）。与 `sfx_explosion` 的同源关系从"复用逐字节配方"松绑为"频谱包络一致"。教训：**当一个感知问题在音量/密度/包络参数上反复调不好时，先质疑合成路径本身**——瞬态叠加与稳态噪声是两类信号，"很多离散爆炸"在高密度极限下该建模为后者，这也正是用户物理直觉指向的。复测 176 杀/秒：blend=1.00 合爆 0.00 全静、底床满量 0.47、voices=7、0 错误。
