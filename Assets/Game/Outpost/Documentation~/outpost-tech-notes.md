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

> 这一节回答一个自然会冒出来的问题：既然曳光在"飞"，飞行途中新出现 / 移入弹道的敌人要不要算命中？是不是得上碰撞检测？

**结论：不做碰撞检测，也不做会飞的子弹实体。当前的"逐 tick 炮口锥内 hitscan"就是更好的办法。** 理由：

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
- **方案**：① BGM——`OutpostAudioSystem`（根 Context）订 `FlowChangedEvent` 一个事件按宏观阶段 `PlayMusic`（单通道交叉淡变 + 同曲幂等，不侵入任何 FlowState）。② 战斗音效——全部接在导演的事件翻译层：爆炸类**每帧限 1 发** + 随机音高（千级击杀不糊成噪声墙），受创重音复用既有的 0.25s 伤害聚合窗口天然节流。③ 火墙循环音——**不走框架音效池**：炮塔运行时挂 `AudioSource` 逐帧调制音量/音高（热度驱动），组音量 × 主音量手动乘回接上设置滑条。④ 本地化——`TbL10N` 表（一行一 key 一列一语言）+ 十行 `OutpostTextSource` adapter；Toolkit 侧 `Bag.BindLocalizedText`、TMP 侧 `CombineLatest(数据, Locale)`；upgrade 表 name/desc 存本地化 key；字体链 `MonoLocaleFonts` 挂根 Context。⑤ 设置——音量/语言的**运行时真源就是两个 Utility 自身状态**，`SettingsWindow` 只是遥控器；关窗一次 `SaveSettingsCommand` 落盘 `outpost/settings`，`BootState` 启动回灌；不设 SettingsModel（不做第二份内存状态）。全部音频为程序化合成 wav（[`Tools/gen_outpost_audio.py`](../../../../Tools/gen_outpost_audio.py)，固定 seed 可复现），BGM 走全小调进行 + 低音 drone/心跳，刻意退到氛围层不抢音效。
- **开火音的分层设计（射速跨三个数量级 2→250 发/秒）**：低射速段逐发单响 `sfx_shot`（每一炮听得清、随机音高防机械感）；高射速段循环轰鸣（上面的火墙层）。物理依据是人耳对 >~15Hz 的重复事件听成连续音——单发层设**最小重触发间隔** 0.08s（≈12 发/秒以上开始丢发，丢发不丢听感、也不打爆 voice），并随火力热度让位（热度近满归零）；循环层音量走**热度平方**（低速段收敛、高速段全量），两层在中段交叉过渡。这是 minigun 类武器音效的标准做法（transient 层 × loop 层 crossfade）。
- **为什么**：`AudioHandle` 刻意不提供播放中调制——"跟随对象的持续音源用引擎组件"是框架划的界（ADR-0022），火墙音正好踩在界上，成为这条分界的实战注脚；音效限流复用表现层已有的"每帧演出预算"心智，声音和特效同一套海量纪律。**⚠ 时序坑（接缝观察）**：`BindLocalizedText` 的刷新信号只有 `Locale`，文本源（配置表）异步后到**不会**触发重绑——绑定先于配置就绪 = 裸 key 定格在屏上。业务解法 = `BootState` 进标题前 `await` 配置 Ready（Failed 也放行：裸 key 上屏是可见的缺失报告，好过卡启动）。
- **落点**：[`Scripts/Systems/OutpostAudioSystem.cs`](../Scripts/Systems/OutpostAudioSystem.cs)（BGM 导演）、[`Scripts/Battle/BattleDirectorSystem.cs`](../Scripts/Battle/BattleDirectorSystem.cs)（`PlayBoomSfx` 限流 + 各事件音）、[`Scripts/Battle/TurretView.cs`](../Scripts/Battle/TurretView.cs)（`InitFireLoop`/`SetFireLoopLevel`）、[`Scripts/Config/OutpostTextSource.cs`](../Scripts/Config/OutpostTextSource.cs)、[`Scripts/OutpostLocales.cs`](../Scripts/OutpostLocales.cs)、[`Scripts/Windows/SettingsWindow.cs`](../Scripts/Windows/SettingsWindow.cs) + [`Res/UI/SettingsWindow.uxml`](../Res/UI/SettingsWindow.uxml)、[`Scripts/Save/OutpostSettings.cs`](../Scripts/Save/OutpostSettings.cs) / [`SettingsCommands.cs`](../Scripts/Save/SettingsCommands.cs)、[`Scripts/Flow/BootState.cs`](../Scripts/Flow/BootState.cs)（配置就绪门 + 回灌）、[`Configs~/Datas/l10n.json`](../Configs~/Datas/l10n.json)、场景 `OutpostGame`（Systems 节点 `OutpostAudioSystem` + Fonts 节点 `MonoLocaleFonts`）。
