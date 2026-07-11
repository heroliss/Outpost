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
- **方案**：① 真弹道——击发逻辑不变，每发生成弹丸沿炮口方向直飞（火墙 ±2° 确定性散布，消耗 RNG 双后端同序）；命中 = **扫掠线段 vs 圆**（`SimMath.SegmentCircleHitT`，弹速 0.8u/tick 远大于炮灰半径、逐点判定会隧穿），取位移段上最早交点。`FindNearestInCone` 退役、`FindNearestInRange`（回转目标 + 停火判定）保留。② 泥地——**均匀密度网格**（不是四叉树/AABB：查询模式固定是"点采样密度"，均匀格最便宜且无浮点比较分支、两后端可逐位一致），击杀/自爆令所在 cell +1、环形上限复写，敌移速 ×= `max(SlowFloor,1−SlowPer×cell计数)`。规则本身，不是优化——两后端 O(1) 同实现。③ 事件时序——`TurretFired` 删 `Aim`/`Hit` 只留 `Direction`，`EnemyHit` 改在**弹着帧**触发、位置=弹着点；M6 那套「音频弹着对拍」人工延迟（`ScheduleSfx`/`FlushPendingSfx`/`FlightDelay`/`TracerFlightSpeed`）**整套删除**——真弹道让音画天然同帧，为对拍加的延迟随规则演进变多余。④ 表现——`SwarmRenderer` 加弹丸绘制（拖尾菱形按方向定向、1023 分批）+ 推挤通道（表现残骸网格邻格查询、原位重写已烘焙矩阵、单具漂移上限 0.8u < 格边长保证不动摇模拟记账格位）+ 泥地热力图（读 `IBattleSim.WreckGrid`/`GetWreckCellCount`）。
- **对拍两级（含新维度）**：关 Burst 12 波 5437 tick **逐 tick 逐位全等**，且**逐格比对泥地密度网格全等**——两条新规则移植零偏差。开 Burst 前 5 波完全相等、w6 起一次溅射击杀归属在 ulp 分叉处易主（击杀数仍逐波相等、得分固定差 5~10、清波 tick 漂移 ≤1），比 M6 的 w22 提前（弹道对浮点更敏感），但归属漂移量级不变——仍是「同一个游戏、不是逐位同一局」，复用并加了「密度网格逐格比对」维度。
- **性能对照（编辑器，Ecs 开 Burst vs Reference 纯托管）**：真实平台期（w23-24，~2920 敌 + 290 弹）Reference **p95 14.2ms** vs Ecs **5.27ms**（~2.6×，Reference 叠加渲染即破 60fps）；合成压力（慢弹拉到千级在飞：~1500 敌 + **1623 弹**）Reference **avg 39ms**（崩到 15-25fps）vs Ecs **12ms**（~3.3×）。两后端 O(P×N) 同算法，差距纯来自 Burst + 连续内存 + 并行移动。**验收目标"切 OOP 后期肉眼掉帧、切回 Ecs 满帧"用真实玩法达成，而非合成基准。**
- **平衡**：真弹道引入 DPS 交付延迟（弹飞 ~0.3s）与穿排收益（弹打穿先死目标继续命中同线后敌）；无头托管 60 波长跑仍不死、平台期单波最低血 63~77%（消耗约 3~4 成），只动 `waverole.json`（炮灰 `maxCount` 3800→4500、突袭机 120→150）补漏怪。
- **踩坑（Play 冒烟发现）**：推挤通道原按「实际拱动数」扣预算——成熟战场残骸多已到漂移上限（只被判定不被推、`continue` 不扣预算）→ 最坏扫遍全部敌人×邻格残骸（~80 万次/帧）无界。改按「**检视残骸数**」扣预算（`PushScanBudgetPerFrame=8000` 次距离判定/帧封顶）：成本单位在逐具距离判定、不在实际拱动，这样才真正封顶最坏扫描。教训与 M3「每帧预算 vs 按时间限流」同源——**预算要扣在真正的成本单位上，不是扣在"成功事件"上**。
- **落点**：[`Sim/IBattleSim.cs`](../Sim/IBattleSim.cs)（`ProjectileSnapshot`/`WreckGridInfo` + `TurretFiredEvent` 删字段 + 弹着帧 `EnemyHit`）、[`Sim/BattleSetup.cs`](../Sim/BattleSetup.cs)（`PlayerSetup` +3 弹丸参数 + `WreckFieldSetup`）、[`Sim/SimMath.cs`](../Sim/SimMath.cs)（`SegmentCircleHitT`/`WreckCellIndex`）、[`Sim/ReferenceBattleSim.cs`](../Sim/ReferenceBattleSim.cs) / [`Sim.Ecs/EcsBattleSim.cs`](../Sim.Ecs/EcsBattleSim.cs)（弹丸 `List`/`NativeList` + `ProjectileJob` + `MoveJob` 泥地采样 + 密度环形记账）、[`Scripts/Battle/SwarmRenderer.cs`](../Scripts/Battle/SwarmRenderer.cs)（`DrawProjectiles`/推挤通道/热力图）、[`OutpostMeshes.cs`](../Scripts/Battle/OutpostMeshes.cs)（弹丸/quad mesh）、[`BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（删曳光/弹着延迟、热力图订阅、弹着帧音效）、[`BattlePrefsModel`](../Scripts/Battle/BattlePrefsModel.cs)/[`BattleCommands.cs`](../Scripts/Battle/BattleCommands.cs)（热力图开关）、`BattleModel`/`BattleQueries`/`BattleHudView`（弹丸数）、`SettingsWindow`/`OutpostSettings`/`SettingsCommands`（热力图设置项）、`Configs~`（BattleGlobal +7 字段 + waverole 重标定）；全景见 [ADR-0031](../../../../docs/adr/0031-outpost-real-projectiles-wreck-interaction.md)。
