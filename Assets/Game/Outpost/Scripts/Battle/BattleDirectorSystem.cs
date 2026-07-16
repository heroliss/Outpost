using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Audio;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Outpost.Flow;
using Game.Outpost.Sim;
using OutpostCfg;
using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 战斗模拟后端选择。<see cref="IBattleSim"/> 是纯 C# 接缝：换后端只改 <c>BattleDirectorSystem.CreateSim</c>
    /// 的一个分支，事件→表现翻译层与 Model/HUD 全部原样复用——这正是接缝存在的意义（同题对比性能时才公平）。
    /// </summary>
    public enum BattleSimBackend
    {
        /// <summary>面向对象参考实现（列表 + 逐帧线性扫描，规则的可执行规格）。</summary>
        Reference,

        /// <summary>DOTS 后端（Entities chunk 存储 + Burst job 热路径；ADR-0030）。与 Reference 同一规格、可对拍。</summary>
        Ecs,
    }

    /// <summary>
    /// 战斗导演：把纯 C# 模拟内核（<see cref="IBattleSim"/>）接到 Unity 表现与框架数据流上——
    /// 每帧 <c>Tick</c> 模拟、把聚合值（血量/波次/击杀/得分/敌人数/模拟耗时）写进 <see cref="BattleModel"/>（HUD 只读订阅）、
    /// 把逐事件（刷怪/击发/命中/自爆）翻成表现（敌人海实例化渲染 / 池化特效 / 相机震动 / 炮塔朝向），终局把战绩交给 <see cref="IGameFlow"/> 进结算。
    /// <para><b>表现层双轨</b>：海量常驻实体（敌人海 / 在飞弹丸 / 残骸 / 泥地热力图）走 <see cref="SwarmRenderer"/>
    /// 实例化绘制（无 GameObject）；少量瞬时特效（脉冲/飘字/烟/碎片）走 <c>Bag.Spawn</c> 对象池。
    /// 模拟内核对两者一无所知，置换 ECS 后端（<see cref="BattleSimBackend"/>）时本类的翻译层原样保留。</para>
    /// <para><b>海量下的演出纪律</b>：命中/击杀/出生特效各有每帧预算（超出只结算数值不演出），
    /// 击杀的保底反馈是残骸——每具都留存进 <see cref="SwarmRenderer"/> 的残骸层、不占预算；
    /// 玩家受创（漏怪自爆 + 拦截溅射）在短窗口内聚合成一次震屏/红闪/汇总飘字——每秒上百次受创时逐条演出会刷屏。</para>
    /// <para>System 层不能 ExecuteCommand（防环），故终局直接 <c>GetUtility&lt;IGameFlow&gt;()</c> 驱动流程。</para>
    /// </summary>
    public sealed class BattleDirectorSystem : MonoSystemBase
    {
        [SerializeField, Tooltip("战斗模拟后端的场景默认值。运行时以 BattlePrefsModel（设置窗可改，下一局生效）为准，开局采样一次覆盖本值。")]
        private BattleSimBackend _backend = BattleSimBackend.Reference;

        [SerializeField, Tooltip("敌人 / 特效的挂载根（世界空间，XY 平面）。")]
        private Transform _arenaRoot;

        [SerializeField, Tooltip("敌人海实例化渲染器（海量常驻单位不走 GameObject）。")]
        private SwarmRenderer _swarm;

        [SerializeField, Tooltip("伤害飘字 prefab（含 DamageFloater；玩家受创聚合展示）。")]
        private GameObject _floaterPrefab;

        [SerializeField, Tooltip("发光短线 prefab（含 ProjectileTracer；击毁碎片流用——在飞弹丸是模拟实体、由 SwarmRenderer 实例化绘制）。")]
        private GameObject _tracerPrefab;

        [SerializeField, Tooltip("脉冲圈 prefab（含 PulseEffect；命中 / 死亡 / 出生 / 炮口闪光 / 烟雾共用）。")]
        private GameObject _pulsePrefab;

        [SerializeField, Tooltip("玩家炮塔表现（朝向由内核角度驱动、后坐由 director 触发）。")]
        private TurretView _turret;

        [SerializeField, Tooltip("竞技场地面装饰（射程圈 / 出生环）。")]
        private ArenaDecor _decor;

        [SerializeField, Tooltip("相机震动（玩家受击反馈）。场景相机不在 Context 子树内，直接场景引用。")]
        private CameraShaker _shaker;

        [SerializeField, Tooltip("随机种子；0 = 用启动时间（每局不同的出生角度）。战斗结果几乎与种子无关——竞技场旋转对称。")]
        private int _seed;

        [SerializeField, Tooltip("起始波次（≤1 = 正常从第 1 波开局）。>1 时开局无头快进静默跑过前面的波次：升级按托管优先级自动拿、" +
            "击杀直接铺成战场残骸。纯 C# 模拟不依赖渲染帧率，快进 20 波约一两秒——压测 / 演示的直达入口。")]
        private int _startWave = 1;

        private IBattleSim _sim;
        private BattleModel _model;
        private UpgradeModel _upgradeModel;
        private Tables _cfg;
        private Dictionary<int, EnemyVisual> _visuals;
        private bool _ready;

        // 终局 / 波间的定时推进
        private float _resultDelay = 1.2f;
        private float _interWaveDelay = 1.5f;
        private bool _ending;
        private bool _retreated;   // 主动撤离收束（与失守走同一条结算路径，只影响文案）
        private float _endTimer;
        private bool _betweenWaves;
        private float _waveTimer;
        private bool _awaitingChoice; // 波清空后停在此、等玩家三选一（IsChoosing 面板弹出中）
        private float _lastRange = -1f;
        private float _autoPickTimer; // 托管：卡片亮相后到自动选定的倒计时（等待抉择期间生效）

        // 升级封顶参数（读自 TbBattleGlobal）：到顶的升级移出三选一池；全部到顶后不再弹面板、直接续波。
        private float _maxRange;
        private float _maxAttack;
        private float _maxRotation;
        private float _minAttackInterval;
        private float _maxRegen;
        private float _maxHpCap;

        // 托管自动选卡前的亮相延迟：让三选一卡片先露个脸再被自动选掉，观战时看得清导演选了什么。
        private const float AutoPickRevealDelay = 0.6f;

        // 波间三选一：从（当前可用的）升级里随机取 3 个不同的候选。随机是纯展示（玩家的选择才是真输入），用领域 List 免每次分配。
        private const int ChoiceCount = 3;
        private readonly List<Upgrade> _upgradePool = new();

        // 所有一次性特效（曳光 / 脉冲 / 飘字 / 碎片）的统一回收列表——都实现 ITimedEffect。
        private readonly List<MonoBehaviour> _effects = new();

        // 海量吞吐下的每帧演出预算——超出即降级（不再生成特效，数值仍每发结算），防池爆 + 刷屏。
        private const int HitFxPerFrame = 12;
        private const int KillFxPerFrame = 6;
        private const int SpawnFxPerFrame = 6;
        private int _hitFxBudget;
        private int _killFxBudget;
        private int _spawnFxBudget;

        // 玩家受创聚合：漏怪自爆 + 拦截溅射的伤害先累积，窗口到期一次性演出（震屏/红闪/汇总飘字，强度随总量）。
        private const float DamageFlushWindow = 0.25f;
        private float _pendingPlayerDamage;
        private float _damageFlushTimer;

        // 战斗音效（clip 经资源系统在 SetupAsync 加载；播放走框架音效池 PlaySfx，随手丢弃返回值）。
        // 爆炸类按时间限流（千级击杀下逐发播只会糊成噪声墙、耗尽 voice）；受创重音天然由伤害聚合窗口节流。
        [Inject] private IAudioUtility _audio;

        // 战场事件音效走 3D 位置播放（PlaySfxAt）：监听器在主相机 (0,0,-10)，声像随弹着点左右分布。
        // minDistance 取监听器到场心距离量级——场内基本全音量、只留方位与轻微远近差；引擎默认 min=1
        // 在这个机位下会把一切压到近乎无声（第一人称尺度的衰减曲线）。
        private const float SfxMinDistance = 11f;
        private const float SfxMaxDistance = 60f;
        private AudioClip _sfxExplosion;
        private AudioClip _sfxDetonate;
        private AudioClip _sfxRepair;
        private AudioClip _sfxWave;
        private AudioClip _sfxUpgrade;
        private AudioClip _sfxDefeat;
        private AudioClip _sfxRetreat;
        private AudioClip _sfxShot;
        private AudioClip _sfxImpact;

        // 爆炸音限流按时间、不按帧："每帧 1 发"在编辑器高帧率（100~300fps）下就是每秒上百发，
        // 1.15s 的爆炸尾巴叠出几十个并发 voice、冲破 Unity 32 实声道上限——超出的被引擎虚化（静音），
        // 最安静的单发炮响 / 弹着叮播到一半被掐（2026-07-10 用户反馈"音效像被截断"的真凶）。
        // ~9 发/秒 × 1.15s 尾巴 ≈ 峰值 10 个并发爆炸 voice，全场加总仍离虚化线有余量；
        // 密集击杀时更少但每发更有体量的爆炸，也比"机关枪式 boom"更像战场。
        private const float BoomSfxMinInterval = 0.11f;
        private float _lastBoomSfxTime = -1f;

        // 弹着「叮」（击中未击毁）的重触发限流：高射速下弹着逐帧都有，限到 ~14 发/秒当质感纹理、不当逐发汇报。
        // 弹着音效直接在 EnemyHit 帧播放——事件本就在弹着帧触发（真弹道），音画天然同拍，无需任何延迟机制。
        private const float ImpactSfxMinInterval = 0.07f;
        private float _lastImpactSfxTime = -1f;

        // 开火音分层（射速跨三个数量级 2→250 发/秒的物理连续体表达）：
        //   低射速 = 逐发单响 sfx_shot（物理上就是离散炮响，听得清每一炮）；高射速 = 火墙档位组
        //   （TurretView 的 AudioSource 层：同一出膛瞬态在 16~256 发/秒五档原生射速下烘焙，按实测
        //   射速选相邻两档交叉淡变 + 档内变速对齐——离散炮响→融合蜂鸣的演化连续可听，蜂鸣基频=射速）。
        //   单发层设最小重触发间隔（超过就丢发不丢听感），随射速沿接棒带线性让位给档位组
        //   （带定义在 TurretView.HandoverBlend，两侧共用一条曲线，中段合计不塌陷）。
        private const float ShotSfxMinInterval = 0.08f;  // 单发层重触发下限（≈12 发/秒以上开始合并）
        private float _lastShotSfxTime = -1f;

        // 火墙档位组的原生射速（发/秒）：与 Tools/gen_outpost_audio.py 的 FIRE_GEAR_RATES 一一对应
        // （资产名 sfx_fire_{rate:000}）。2 的幂间隔 = log 域等距，任意射速恰落相邻两档之内。
        private static readonly float[] FireGearRates = { 16f, 32f, 64f, 128f, 256f };

        // 表现色板（敌人本体色来自配置表的表现列，这里只留玩家侧的固定色）。
        private static readonly Color PlayerHitColor = new(1f, 0.30f, 0.24f);
        private static readonly Color RepairColor = new(0.4f, 1.8f, 0.7f, 0.8f);       // 波间维修的回满提示（HDR 绿）
        private static readonly Color ImpactColor = new(2.0f, 2.4f, 2.4f, 0.85f);
        private static readonly Color MuzzleColor = new(1.2f, 2.8f, 2.6f, 0.7f);
        private static readonly Color SmokeColor = new(0.42f, 0.42f, 0.46f, 0.55f); // 非 HDR 灰烟（不发光，读成烟）
        private static readonly Color DebrisColor = new(2.6f, 1.5f, 0.5f, 1f);       // HDR 暖橙火花碎片

        // 表现层自算的「火力热度」(0..1)：从 TurretFired 击发节奏推断当前射速，驱动炮塔辉光与循环轰鸣。
        // 内核只管开火逻辑，"点射收拢 → 火墙涨亮铺开"的渐变属于表现层、不进确定性内核。
        private const float HeatIntervalHot = 0.06f;    // 击发间隔 ≤ 此值算火墙 → 热度趋 1
        private const float HeatIntervalCold = 0.5f;    // 击发间隔 ≥ 此值算点射 → 热度趋 0
        private const float HeatLerpUp = 8f;            // 热度上升速度（火力拉起快）
        private const float HeatLerpDown = 4f;          // 热度回落速度（停火冷却稍缓）
        private const float FireIdleTimeout = 0.2f;     // 超过此时长没击发 = 停火，热度归 0
        private float _fireHeat;                          // 当前火力热度
        private float _lastFireTime = -1f;                // 上次 TurretFired 的时刻（Time.time；-1 = 从未击发）
        private float _fireInterval = 1f;                 // 最近两发的间隔（秒）——越小 = 射速越高
        private float _fireRate;                          // 平滑后的实测射速（发/秒）——驱动火墙档位组与单发层接棒
        private int _shotsThisFrame;                      // 本帧击发数（帧内多发时事件时间戳同帧全相等，见 UpdateFireHeat）
        private bool _wasFiring;                          // 上帧是否在持续开火（收火余韵的边沿检测）

        // 模拟耗时采样（性能 HUD 用）：Stopwatch 计每帧 Tick 耗时、指数平滑防抖。
        private float _simTickMs;

        // 表现层的 Z 分层（相机 -10 朝 +Z 看）：地板 0.5 > 热力图 0.42 > 地面环 0.3 > 残骸 ~0.23 > 单位 0 > 脉冲 -0.2 > 弹丸/碎片 -0.3 > 飘字 -0.8。
        private const float PulseZ = -0.2f;
        private const float DebrisZ = -0.3f; // 碎片流 / 炮口闪光层（与 SwarmRenderer 的弹丸层同深度）
        private const float FloaterZ = -0.8f;

        private void Start() => SetupAsync().Forget();

        private async UniTaskVoid SetupAsync()
        {
            var config = this.GetUtility<IConfigUtility<Tables>>();
            // 配置在根 Context 启动即异步预载，进战斗时通常已就绪；仍等一手，避免竞态。
            await UniTask.WaitUntil(
                () => config.State.CurrentValue is ConfigInitState.Ready or ConfigInitState.Failed,
                cancellationToken: this.GetCancellationTokenOnDestroy());
            if (config.State.CurrentValue != ConfigInitState.Ready)
            {
                Debug.LogError("[BattleDirectorSystem] 配置未就绪，无法开始战斗。");
                return;
            }

            _cfg = config.Tables;
            var g = _cfg.TbBattleGlobal.Data;
            _resultDelay = g.ResultDelay;
            _interWaveDelay = g.InterWaveDelay;
            _maxRange = g.PlayerMaxRange;
            _maxAttack = g.PlayerMaxAttack;
            _maxRotation = g.PlayerMaxRotationSpeed;
            _minAttackInterval = g.PlayerMinAttackInterval;
            _maxRegen = g.PlayerMaxRegen;
            _maxHpCap = g.PlayerMaxHpCap;

            _model = this.GetModel<BattleModel>();
            _upgradeModel = this.GetModel<UpgradeModel>();
            _visuals = EnemyVisuals.Build(_cfg);
            // 热力图亮度饱和点 = 减速到下限所需的每格残骸数——热力图刻度与减速规则同源。
            _swarm.Init(_visuals, (1f - g.WreckSlowFloor) / Mathf.Max(0.0001f, g.WreckSlowPerCount));

            // 泥地热力图开关（纯表现）：订阅即时生效，HUD 按钮随时切换。
            var prefs = this.GetModel<BattlePrefsModel>();
            Bag.Subscribe(prefs.ShowWreckHeatmap, on => _swarm.WreckHeatmapVisible = on);

            // 游戏速度（纯表现）：订阅写全局 Time.timeScale——弹丸/敌人/特效随之整场变速（HUD 速度按钮实时改）。
            // 订阅即得当前值先设一次；OnDestroy 还原 1×，避免慢/快放污染标题 / 结算。
            Bag.Subscribe(prefs.SimSpeed, s => Time.timeScale = s);

            // 战斗音效 clip 经资源系统加载（句柄进 Bag 随战斗场景释放）；火墙循环底噪交给炮塔挂 AudioSource 逐帧调制。
            _sfxExplosion = await Bag.Load<AudioClip>("sfx_explosion");
            _sfxDetonate = await Bag.Load<AudioClip>("sfx_detonate");
            _sfxRepair = await Bag.Load<AudioClip>("sfx_repair");
            _sfxWave = await Bag.Load<AudioClip>("sfx_wave");
            _sfxUpgrade = await Bag.Load<AudioClip>("sfx_upgrade");
            _sfxDefeat = await Bag.Load<AudioClip>("sfx_defeat");
            _sfxRetreat = await Bag.Load<AudioClip>("sfx_retreat");
            _sfxShot = await Bag.Load<AudioClip>("sfx_shot");
            _sfxImpact = await Bag.Load<AudioClip>("sfx_impact");
            var gearClips = new AudioClip[FireGearRates.Length];
            for (int i = 0; i < FireGearRates.Length; i++)
                gearClips[i] = await Bag.Load<AudioClip>($"sfx_fire_{(int)FireGearRates[i]:000}");
            _turret.InitFireGears(gearClips, FireGearRates);
            _turret.InitServoLoop(await Bag.Load<AudioClip>("sfx_servo_loop"));
            var setup = BattleSetupFactory.Build(_cfg, _seed != 0 ? _seed : System.Environment.TickCount);

            // 后端偏好开局采样一次（设置窗改动下一局生效）：模拟是一次性实例，不做局中热切（状态迁移不值得，ADR-0030）。
            _backend = prefs.Backend.CurrentValue;
            _sim = CreateSim();
            if (_startWave > 1)
            {
                // 快进期间不接表现翻译层（避免海量事件灌进特效/演出），跑完再挂——见 FastForwardTo。
                _sim.Start(setup);
                await FastForwardTo(_startWave);
                SubscribeSimEvents();
            }
            else
            {
                SubscribeSimEvents();
                _sim.Start(setup);
            }

            _decor.Init(setup.ArenaRadius);
            _model.Backend.Value = _backend.ToString();
            _model.PlayerMaxHp.Value = _sim.PlayerMaxHp;
            WriteModel();
            _ready = true;
        }

        // 接缝的后端工厂：ECS 后端就是多出来的这一个分支（ADR-0030），事件→表现翻译层、Model、HUD 全部零改动。
        private IBattleSim CreateSim() => _backend switch
        {
            BattleSimBackend.Ecs => new Game.Outpost.Sim.Ecs.EcsBattleSim(),
            _ => new ReferenceBattleSim(),
        };

        // 模拟事件 → 表现翻译层的接线（正常开局在 Start 前挂；无头快进则跑完快进再挂）。
        private void SubscribeSimEvents()
        {
            _sim.EnemySpawned += OnEnemySpawned;
            _sim.EnemyHit += OnEnemyHit;
            _sim.TurretFired += OnTurretFired;
            _sim.EnemyDetonated += OnEnemyDetonated;
            _sim.WaveCleared += OnWaveCleared;
        }

        // 无头快进到目标波：纯 C# 模拟不依赖渲染帧率，可在加载期把前面的波次静默跑完（IBattleSim 接缝的直接红利，
        // 与平衡标定用的无头 harness 是同一姿势）。升级走与托管相同的贪心优先级，数值与正常托管打法一致；
        // 表现事件全程不挂——残骸是模拟状态，快进积累的战场历史由 SwarmRenderer 的槽位镜像在首帧自动发现。
        private async UniTask FastForwardTo(int targetWave)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const float dt = 1f / 30f;
            int guard = 2_000_000; // 防呆：配置异常导致波次推不动时不至于死循环（约 18 小时模拟时间）
            int slice = 0;
            while (_sim.WaveIndex < targetWave && _sim.Phase != BattlePhase.Defeat && guard-- > 0)
            {
                if (_sim.Phase == BattlePhase.WaveCleared)
                {
                    ApplyBestUpgrade();
                    _sim.BeginNextWave();
                }
                else
                {
                    _sim.Tick(dt);
                }

                if (++slice >= 3000) // 深波次快进耗时可达数秒：分片让出主线程，画面不冻结
                {
                    slice = 0;
                    await UniTask.Yield(this.GetCancellationTokenOnDestroy());
                }
            }

            Debug.Log($"[BattleDirectorSystem] 无头快进至第 {_sim.WaveIndex} 波（击杀 {_sim.Kills}、残骸 {_sim.WreckSlotCount}），耗时 {sw.ElapsedMilliseconds}ms。");
        }

        // 快进版选卡：没有面板与随机三选一，直接从全部未到顶的升级里按托管优先级拿最优——与长跑标定的贪心策略一致。
        private void ApplyBestUpgrade()
        {
            Upgrade best = null;
            int bestRank = int.MaxValue;
            foreach (var u in _cfg.TbUpgrade.DataList)
            {
                if (IsUpgradeCapped(u.Kind)) continue;
                int rank = AutoPriority(u.Kind);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = u;
                }
            }
            if (best != null) _sim.ApplyModifier(BattleSetupFactory.ToModifier(best));
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _hitFxBudget = HitFxPerFrame;   // 每帧刷新演出预算（超出即降级，见各 On* 事件）
            _killFxBudget = KillFxPerFrame;
            _spawnFxBudget = SpawnFxPerFrame;
            AdvanceEffects();
            UpdateFireHeat(dt); // 表现层火力热度：驱动炮塔辉光（与 sim 解耦、纯 cosmetic，停火即冷却）
            if (!_ready || _sim == null) return;

            if (_ending)
            {
                _swarm.Render(_sim); // 终局定格：残余敌人海保持可见，随场景卸载消失
                _endTimer -= dt;
                if (_endTimer <= 0f) GoToResult();
                return;
            }

            // 波清空后停在此、等玩家三选一——面板由 UpgradeModel.IsChoosing 驱动，抉择前不推进。
            if (_awaitingChoice)
            {
                _swarm.Render(_sim);
                _turret.Face(_sim.TurretAngle);
                // 托管中：卡片亮相片刻后按优先级自动选定（纯观战）。中途关掉托管即停止倒计时、回到手动。
                if (_upgradeModel.AutoManaged.Value)
                {
                    _autoPickTimer -= dt;
                    if (_autoPickTimer <= 0f) AutoPickUpgrade();
                }
                return;
            }

            if (_betweenWaves)
            {
                _waveTimer -= dt;
                if (_waveTimer <= 0f)
                {
                    _betweenWaves = false;
                    _sim.BeginNextWave();
                    _audio.PlaySfx(_sfxWave, volume: 0.55f); // 新一波开场警报（与 HUD 横幅同拍）
                }
            }
            else
            {
                // 模拟耗时采样：这是两个后端同题对比的度量点（HUD 实时展示）。
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _sim.Tick(dt);
                float ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency;
                _simTickMs = Mathf.Lerp(_simTickMs, ms, 0.08f);
            }

            FlushPlayerDamage(dt);
            _swarm.Render(_sim);
            SyncRangeRing();
            _turret.Face(_sim.TurretAngle); // 内核已按回转速度算好朝向，表现层照画
            WriteModel();

            if (_sim.Phase == BattlePhase.Defeat)
            {
                _ending = true;
                _endTimer = _resultDelay;
                // 终局强调：战败红色大脉冲，从哨站位置扩散 + 下行失守音。
                SpawnPulse(WithZ(_turret.transform.position, PulseZ), new Color(2.4f, 0.5f, 0.4f, 0.9f), 1f, 16f, 0.9f);
                _audio.PlaySfx(_sfxDefeat);
            }
        }

        // ── 模拟事件 → 演出 ──────────────────────────────────────────────────

        private void OnEnemySpawned(EnemySpawnedEvent e)
        {
            _swarm.OnSpawned(e.EnemyId);

            // 出生提示圈（限量：海量刷怪时超预算只出兵不演出）。
            if (_spawnFxBudget <= 0) return;
            _spawnFxBudget--;
            var v = EnemyVisuals.Get(_visuals, e.ArchetypeId);
            var c = v.Color * 1.2f;
            c.a = 0.5f;
            SpawnPulse(ToWorld(e.Position, PulseZ), c, 2.6f * v.ExplosionScale, 0.4f, 0.35f);
        }

        // 炮塔击发一发：后坐 + 炮口闪光 + 单发音（弹丸本体是模拟实体、由 SwarmRenderer 逐帧绘制；
        // 弹着反应在 OnEnemyHit——真弹道下击发与弹着是两个时刻）。高射速下闪光每帧限量，超预算只记节奏。
        private void OnTurretFired(TurretFiredEvent e)
        {
            // 记录击发节奏（超 FX 预算也照记，让热度/射速反映真实击发）：与上一发的间隔喂给火力热度。
            // 帧内多发时同帧时间戳全相等（间隔记成 0）——由 UpdateFireHeat 用 dt/发数还原真实间隔。
            float now = Time.time;
            if (_lastFireTime >= 0f) _fireInterval = now - _lastFireTime;
            _lastFireTime = now;
            _shotsThisFrame++;

            TryPlayShotSfx(now); // 单发层不占视觉 FX 预算（有自己的重触发限流）

            if (_hitFxBudget <= 0) return;
            _hitFxBudget--;
            _turret.Fire();
            SpawnPulse(WithZ(_turret.MuzzleWorldPos, DebrisZ), MuzzleColor, 0.15f, 0.55f, 0.12f);
        }

        // 开火音单发层：低射速逐发清脆单响（物理上就是离散炮响），射速升高后沿接棒带线性让位给
        // 火墙档位组——带定义在 TurretView.HandoverBlend，两侧共用一条曲线，中段合计不塌陷。
        // 最小重触发间隔丢掉超密击发——12 发/秒以上人耳已听成连串，丢发不丢听感、也不打爆 voice。
        private void TryPlayShotSfx(float now)
        {
            if (_lastShotSfxTime >= 0f && now - _lastShotSfxTime < ShotSfxMinInterval) return;
            float volume = 0.62f * (1f - TurretView.HandoverBlend(_fireRate));
            if (volume <= 0.01f) return;
            _lastShotSfxTime = now;
            _audio.PlaySfx(_sfxShot, volume: volume, pitch: Random.Range(0.94f, 1.08f));
        }

        // 表现层自算「火力热度」(0..1) 与「实测射速」(发/秒)：都从 TurretFired 击发节奏推断。
        // 热度只剩视觉职责（核心辉光）；声音（火墙档位组 + 单发接棒）走真实射速——物理连续体的驱动量。
        private void UpdateFireHeat(float dt)
        {
            // 帧内多发还原：同帧击发的事件时间戳全相等（间隔记成 0），真实间隔 = dt / 发数。
            // 射速超过帧率后这是唯一正确的间隔来源；单发/低射速时保留事件间实测值。
            if (_shotsThisFrame >= 2 && dt > 0f) _fireInterval = dt / _shotsThisFrame;
            _shotsThisFrame = 0;

            bool firing = _lastFireTime >= 0f && Time.time - _lastFireTime <= FireIdleTimeout;
            float target = firing
                ? Mathf.Clamp01(Mathf.InverseLerp(HeatIntervalCold, HeatIntervalHot, _fireInterval)) // 间隔越小越热
                : 0f; // 停火：热度冷却归零
            float speed = target > _fireHeat ? HeatLerpUp : HeatLerpDown;
            _fireHeat = Mathf.MoveTowards(_fireHeat, target, speed * dt);
            _turret.SetSpin(_fireHeat); // 核心亮度：读作"炮管火力拉满/收拢"

            // 平滑射速：上升快下降稍缓（与热度同性格）；指数趋近让档间选档/变速连续滑移无跳变。
            // 分母下限 0.25ms（4000 发/秒）≈ 内核结构上限（MaxShotsPerTick=64 × 帧率）再留余量——
            // 射速升级无封顶（配置 minAttackInterval≤0），追踪要跟到底，音频侧的表达饱和在 TurretView。
            float rateTarget = firing ? 1f / Mathf.Max(_fireInterval, 0.00025f) : 0f;
            float k = rateTarget > _fireRate ? 12f : 5f;
            _fireRate = Mathf.Lerp(_fireRate, rateTarget, 1f - Mathf.Exp(-k * dt));

            // 收火余韵：高速连发骤停时补一发全长单响——火墙是"收住"不是"掐断"（真实连发骤停时，
            // 最后一发不再被后续弹掩蔽的完整轰鸣尾本来就会突然可闻）。
            if (_wasFiring && !firing && _fireRate > 25f)
                _audio.PlaySfx(_sfxShot, volume: 0.42f, pitch: 0.95f);
            _wasFiring = firing;

            // 火墙档位组随实测射速调制。挂对象的 AudioSource 不归框架分组音量管——主 × 音效组在这里手动乘回，
            // 设置页滑条照样管得住它（引擎组件跨层 + 框架分组音量的桥接就这一行）。
            _turret.SetFireWall(_fireRate, _audio.MasterVolume * _audio.GetGroupVolume(AudioGroups.Sfx));
        }

        // 爆炸类音效（拦截击毁 / 抵达自爆共用）：按时间限流（见 BoomSfxMinInterval）+ 随机音高防"机关枪同音"；
        // 体量大的原型更低沉更响。击毁事件在弹着帧触发，直接播即与视觉爆点同帧；
        // 3D 位置播放让爆点方位可听（屏幕左侧炸响在左耳），与视觉爆光同点。
        private void PlayBoomSfx(int archetypeId, Vector3 worldPos)
        {
            float now = Time.time;
            if (_lastBoomSfxTime >= 0f && now - _lastBoomSfxTime < BoomSfxMinInterval) return;
            _lastBoomSfxTime = now;
            float scale = EnemyVisuals.Get(_visuals, archetypeId).ExplosionScale;
            float pitch = Random.Range(0.92f, 1.12f) * (scale >= 0.8f ? 0.85f : 1.05f);
            _audio.PlaySfxAt(_sfxExplosion, worldPos, volume: Mathf.Clamp(0.35f + 0.3f * scale, 0.35f, 0.8f), pitch: pitch,
                minDistance: SfxMinDistance, maxDistance: SfxMaxDistance);
        }

        // 弹着「叮」（击中未击毁）：与击毁 boom 分开的轻量金属短鸣，在弹着帧直接播。
        // 音量恒定偏轻——它是"弹药落在装甲上"的质感层，不是逐发战果汇报；限流见 ImpactSfxMinInterval。
        private void TryPlayImpactSfx(Vector3 worldPos)
        {
            float now = Time.time;
            if (_lastImpactSfxTime >= 0f && now - _lastImpactSfxTime < ImpactSfxMinInterval) return;
            _lastImpactSfxTime = now;
            _audio.PlaySfxAt(_sfxImpact, worldPos, volume: 0.22f, pitch: Random.Range(0.9f, 1.15f),
                minDistance: SfxMinDistance, maxDistance: SfxMaxDistance);
        }

        // 弹着帧的敌人反应（事件位置 = 弹着点）：音效 / 爆点 / 白闪全部同帧同点——真弹道下音画对拍是事件时序的天然结果。
        private void OnEnemyHit(EnemyHitEvent e)
        {
            // 拦截溅射并入受创聚合（不再逐条飘字——平台期贴基地击杀每秒发生多次）。
            if (e.SplashDamage > 0f) AccumulatePlayerDamage(e.SplashDamage);

            if (e.Killed)
            {
                _swarm.OnRemoved(e.EnemyId); // 残骸是模拟状态、由槽位镜像自动落定——不占预算的保底反馈 + 战场历史
                if (_killFxBudget > 0)
                {
                    _killFxBudget--;
                    SpawnKillExplosion(ToWorld(e.Position), e.ArchetypeId);
                }
                PlayBoomSfx(e.ArchetypeId, ToWorld(e.Position));
            }
            else
            {
                _swarm.OnFlash(e.EnemyId);
                TryPlayImpactSfx(ToWorld(e.Position));
                if (_hitFxBudget > 0)
                {
                    _hitFxBudget--;
                    SpawnPulse(ToWorld(e.Position, PulseZ), ImpactColor, 0.2f, 0.9f, 0.16f); // 弹着冲击闪
                }
            }
        }

        // 敌人抵达基地自爆：伤害并入受创聚合，来袭弹自身的爆炸限量演出，回收其实例。
        private void OnEnemyDetonated(EnemyDetonatedEvent e)
        {
            _swarm.OnRemoved(e.EnemyId); // 自爆也留残骸（模拟状态）——基地周界的积尸环即是"漏怪都从哪来"的可视化
            AccumulatePlayerDamage(e.Damage);
            var pos = ToWorld(e.Position);
            PlayBoomSfx(e.ArchetypeId, pos);

            if (_killFxBudget <= 0) return;
            _killFxBudget--;
            var v = EnemyVisuals.Get(_visuals, e.ArchetypeId);
            var boom = v.Color * 2.7f;
            boom.a = 1f;
            SpawnPulse(WithZ(pos, PulseZ), boom, 0.5f, 4.1f * v.ExplosionScale, 0.56f);
            SpawnPulse(WithZ(pos, PulseZ), new Color(2.9f, 2.9f, 3.1f, 0.95f), 0.2f, 2.1f * v.ExplosionScale, 0.28f);
            // 大个体才加外扩冲击波 + 碎片 + 烟；炮灰只留脉冲，防海量自爆刷屏。
            if (v.ExplosionScale >= 0.8f)
            {
                SpawnPulse(WithZ(pos, PulseZ), boom, 1.7f * v.ExplosionScale, 5.2f * v.ExplosionScale, 0.34f);
                SpawnDebris(pos, 6, 2.0f * v.ExplosionScale, 0.32f);
                SpawnSmoke(pos, 0.4f, 2.2f * v.ExplosionScale, 0.7f);
            }
        }

        // ── 玩家受创聚合 ─────────────────────────────────────────────────────

        // 伤害先累积（血量在内核已实时扣，这里只管演出），窗口计时在 FlushPlayerDamage。
        private void AccumulatePlayerDamage(float damage)
        {
            if (_pendingPlayerDamage <= 0f) _damageFlushTimer = DamageFlushWindow; // 首笔伤害起表
            _pendingPlayerDamage += damage;
        }

        // 窗口到期把累积伤害演成一次反馈：震屏 / 基地红色冲击 / 汇总飘字，强度随伤害占血量上限的比例。
        private void FlushPlayerDamage(float dt)
        {
            if (_pendingPlayerDamage <= 0f) return;
            _damageFlushTimer -= dt;
            if (_damageFlushTimer > 0f) return;

            float dmg = _pendingPlayerDamage;
            _pendingPlayerDamage = 0f;

            float severity = Mathf.Clamp01(dmg / Mathf.Max(1f, _sim.PlayerMaxHp)); // 0..1：本窗口掉了多大比例的血
            var playerPos = _turret.transform.position;
            _shaker.Shake(0.1f + severity * 0.5f, 0.12f + severity * 0.5f);
            _audio.PlaySfx(_sfxDetonate, volume: 0.5f + severity * 0.5f); // 受创重音：聚合窗口天然节流，响度随本窗掉血比例

            var warn = PlayerHitColor * (1.4f + severity * 1.2f);
            warn.a = 0.6f + severity * 0.35f;
            SpawnPulse(WithZ(playerPos, PulseZ), warn, 0.5f, 2.4f + severity * 3f, 0.3f + severity * 0.2f);
            if (severity > 0.06f) SpawnSmoke(playerPos, 0.5f, 2.4f, 0.8f); // 掉血明显才冒烟（结构受损感）

            SpawnFloater($"-{Mathf.CeilToInt(dmg)}", PlayerHitColor,
                WithZ(playerPos + new Vector3(0f, 0.9f, 0f), FloaterZ));
        }

        // 拦截击毁的爆炸：按原型色扩散大圈 + 亮白核，大个体加冲击波 + 碎片 + 烟。
        private void SpawnKillExplosion(Vector3 hitPos, int archId)
        {
            var v = EnemyVisuals.Get(_visuals, archId);
            var c = v.Color * 2.6f;
            c.a = 0.95f;
            SpawnPulse(WithZ(hitPos, PulseZ), c, 0.4f, 3.7f * v.ExplosionScale, 0.52f);
            SpawnPulse(WithZ(hitPos, PulseZ), new Color(2.7f, 2.8f, 3.0f, 0.92f), 0.15f, 1.8f * v.ExplosionScale, 0.24f);
            if (v.ExplosionScale >= 0.8f)
            {
                SpawnPulse(WithZ(hitPos, PulseZ), c, 1.4f * v.ExplosionScale, 4.6f * v.ExplosionScale, 0.3f); // 外扩冲击波
                SpawnDebris(hitPos, 5, 1.7f * v.ExplosionScale, 0.28f);
                SpawnSmoke(hitPos, 0.3f, 1.6f * v.ExplosionScale, 0.6f);
            }
        }

        private void OnWaveCleared(int wave)
        {
            // 波间维修（内核规则：撑过一波血量回满）的可见化：哨站绿色修复脉冲 + 上行修复音，HUD 血条同帧回满。
            SpawnPulse(WithZ(_turret.transform.position, PulseZ), RepairColor, 0.6f, 5f, 0.55f);
            _audio.PlaySfx(_sfxRepair, volume: 0.65f);

            // 每波清空：停下推进、弹三选一升级面板，等玩家抉择（ChooseUpgrade 才继续）。
            OfferUpgrades();
        }

        // ── 波间三选一升级 ─────────────────────────────────────────────────

        // 从当前可用的升级里随机取 3 个不同的候选，填进 UpgradeModel 并置 IsChoosing=true（面板据此弹出）。
        // 已到顶的升级移出候选池（六种全部有顶，见 TbBattleGlobal 的 max/min 字段——玩家全成长封顶是平台期稳态的前提）；
        // 全部到顶后不再弹面板，短拍后直接续下一波（成长期结束、进入纯守成稳态）。
        private void OfferUpgrades()
        {
            _upgradePool.Clear();
            foreach (var u in _cfg.TbUpgrade.DataList)
            {
                if (IsUpgradeCapped(u.Kind)) continue;
                _upgradePool.Add(u);
            }

            if (_upgradePool.Count == 0)
            {
                // 全部到顶：无可选，直接短拍续下一波（不弹空面板）。
                _betweenWaves = true;
                _waveTimer = Mathf.Min(_interWaveDelay, 0.8f);
                return;
            }

            // 部分 Fisher–Yates：把随机选中的项换到前 ChoiceCount 个位置。
            int take = Mathf.Min(ChoiceCount, _upgradePool.Count);
            for (int i = 0; i < take; i++)
            {
                int j = Random.Range(i, _upgradePool.Count);
                (_upgradePool[i], _upgradePool[j]) = (_upgradePool[j], _upgradePool[i]);
            }

            _upgradeModel.Choices.Clear();
            for (int i = 0; i < take; i++)
            {
                var u = _upgradePool[i];
                _upgradeModel.Choices.Add(new UpgradeOption(u.Id, u.Name, u.Desc));
            }

            _awaitingChoice = true;
            _upgradeModel.IsChoosing.Value = true;
            _autoPickTimer = AutoPickRevealDelay; // 托管时用：卡片先亮相再自动选
        }

        /// <summary>
        /// 玩家选定一个升级（由 <see cref="ChooseUpgradeCommand"/> 经命令中转调入）：把配置行映射成
        /// <c>PlayerModifier</c> 应用到模拟，收起面板，短暂"强化定格"后进下一波。仅在等待抉择时有效（防重复点击）。
        /// </summary>
        public void ChooseUpgrade(int upgradeId)
        {
            if (!_awaitingChoice || _sim == null) return;

            var up = _cfg.TbUpgrade.GetOrDefault(upgradeId);
            if (up != null) _sim.ApplyModifier(BattleSetupFactory.ToModifier(up));

            _upgradeModel.IsChoosing.Value = false;
            _upgradeModel.Choices.Clear();
            _awaitingChoice = false;

            // 强化反馈：玩家位置金色脉冲 + 上行确认音；射程等即时生效，SyncRangeRing 下帧会外扩射程圈。
            SpawnPulse(WithZ(_turret.transform.position, PulseZ), new Color(1.5f, 1.15f, 0.4f, 0.9f), 0.5f, 4f, 0.5f);
            _audio.PlaySfx(_sfxUpgrade, volume: 0.7f);

            _betweenWaves = true;
            _waveTimer = Mathf.Min(_interWaveDelay, 0.6f); // 抉择已占足停顿，进下一波只留一个短拍
        }

        /// <summary>
        /// 切换托管模式（由 <see cref="SetAutoManageCommand"/> 经命令中转调入）：记录状态到 <see cref="UpgradeModel"/>。
        /// 若正等待抉择时开启，重置亮相延迟——让卡片露个脸再自动选、而非瞬选；关掉即回手动。可随时开关。
        /// </summary>
        public void SetAutoManaged(bool on)
        {
            if (_upgradeModel == null) return;
            _upgradeModel.AutoManaged.Value = on;
            if (on && _awaitingChoice) _autoPickTimer = AutoPickRevealDelay;
        }

        /// <summary>
        /// 主动撤离（由 <see cref="RetreatCommand"/> 经命令中转调入）：立即收束本局、按当前战绩进结算。
        /// 全成长封顶 + 波间维修的稳态下失守可能永不发生，撤离是一局的<b>常规结束方式</b>（把分数落袋）。
        /// </summary>
        public void Retreat()
        {
            if (!_ready || _ending || _sim == null) return;
            _retreated = true;
            _ending = true;
            _endTimer = 0.6f;

            // 撤离时可能正停在三选一面板——收起，避免结算过渡期面板残留。
            _awaitingChoice = false;
            _upgradeModel.IsChoosing.Value = false;
            _upgradeModel.Choices.Clear();

            // 撤离提示：青色收束脉冲（与战败红色区分）+ 明亮下行收束音（不是失败）。
            SpawnPulse(WithZ(_turret.transform.position, PulseZ), new Color(0.4f, 2.2f, 2.4f, 0.85f), 6f, 0.5f, 0.5f);
            _audio.PlaySfx(_sfxRetreat, volume: 0.8f);
        }

        // 托管自动选卡：从当前候选里挑优先级最高的一张，走与手动同一条 ChooseUpgrade 路径（应用 + 推进下一波）。
        private void AutoPickUpgrade()
        {
            int bestId = -1;
            int bestRank = int.MaxValue;
            foreach (var opt in _upgradeModel.Choices)
            {
                var up = _cfg.TbUpgrade.GetOrDefault(opt.Id);
                if (up == null) continue;
                int rank = AutoPriority(up.Kind);
                if (rank < bestRank) { bestRank = rank; bestId = opt.Id; }
            }
            if (bestId >= 0) ChooseUpgrade(bestId);
        }

        // 六种升级的封顶判定（上限读自 TbBattleGlobal，开局缓存；0 = 不设顶）。三选一候选池与快进贪心共用。
        private bool IsUpgradeCapped(UpgradeKind kind) => kind switch
        {
            UpgradeKind.Range => _maxRange > 0f && _sim.PlayerRange >= _maxRange - 0.001f,
            UpgradeKind.Attack => _maxAttack > 0f && _sim.PlayerAttack >= _maxAttack - 0.001f,
            UpgradeKind.RotationSpeed => _maxRotation > 0f && _sim.PlayerRotationSpeed >= _maxRotation - 0.001f,
            UpgradeKind.AttackSpeed => _minAttackInterval > 0f && _sim.PlayerAttackInterval <= _minAttackInterval + 0.0001f,
            UpgradeKind.Regen => _maxRegen > 0f && _sim.PlayerRegen >= _maxRegen - 0.001f,
            UpgradeKind.MaxHp => _maxHpCap > 0f && _sim.PlayerMaxHp >= _maxHpCap - 0.001f,
            _ => false,
        };

        // 托管选卡优先级（数字越小越优先）：增程雷达最优先——射程只有两级（7→9→11 封顶）却决定拦截窗口的物理上限，
        // 越早拿每一波的受益越多；攻速是吞吐之本；攻击跨断点（2 炮变 1 炮）收益巨大；回转决定 360° 覆盖；
        // 血量 / 回血是纯保底。此顺序经无头 harness 长跑验证（雷达前置后托管 60 波不死、平台期每波消耗约五成，
        // 与旧顺序稳态相同——平台期升级全封顶后顺序无差，前置只改变成长期的体验节奏）。
        private static int AutoPriority(UpgradeKind kind) => kind switch
        {
            UpgradeKind.Range => 0,
            UpgradeKind.AttackSpeed => 1,
            UpgradeKind.Attack => 2,
            UpgradeKind.RotationSpeed => 3,
            UpgradeKind.MaxHp => 4,
            UpgradeKind.Regen => 5,
            _ => 6,
        };

        private void SyncRangeRing()
        {
            if (Mathf.Approximately(_sim.PlayerRange, _lastRange)) return;
            _lastRange = _sim.PlayerRange;
            _decor.SetRange(_lastRange);
        }

        // 击毁 / 自爆的碎片飞溅：从爆点向随机方向抛出若干短促发光碎片流（ProjectileTracer 定速直飞，指定飞行时长拉出可见弧）。
        private void SpawnDebris(Vector3 center, int count, float spread, float duration)
        {
            for (int i = 0; i < count; i++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float len = spread * (0.55f + 0.6f * Random.value);
                var to = center + new Vector3(Mathf.Cos(ang) * len, Mathf.Sin(ang) * len, 0f);
                var shard = Bag.Spawn(_tracerPrefab, _arenaRoot).GetComponent<ProjectileTracer>();
                shard.Play(WithZ(center, DebrisZ), WithZ(to, DebrisZ), DebrisColor, duration);
                _effects.Add(shard);
            }
        }

        // 冒烟：非 HDR 灰色脉冲缓慢扩散淡出（不发光，读成烟而非爆闪）。
        private void SpawnSmoke(Vector3 center, float fromDiameter, float toDiameter, float lifetime)
            => SpawnPulse(WithZ(center, PulseZ), SmokeColor, fromDiameter, toDiameter, lifetime);

        private void SpawnFloater(string content, Color color, Vector3 worldPos)
        {
            var f = Bag.Spawn(_floaterPrefab, _arenaRoot).GetComponent<DamageFloater>();
            f.Play(content, color, worldPos);
            _effects.Add(f);
        }

        private void SpawnPulse(Vector3 worldPos, Color color, float fromDiameter, float toDiameter, float lifetime)
        {
            var p = Bag.Spawn(_pulsePrefab, _arenaRoot).GetComponent<PulseEffect>();
            p.Play(worldPos, color, fromDiameter, toDiameter, lifetime);
            _effects.Add(p);
        }

        private void AdvanceEffects()
        {
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                var e = _effects[i];
                if (e == null) { _effects.RemoveAt(i); continue; }
                if (((ITimedEffect)e).IsDone)
                {
                    _effects.RemoveAt(i);
                    Bag.Despawn(e.gameObject);
                }
            }
        }

        // ── 模拟聚合值 → Model（HUD 只读订阅）─────────────────────────────────

        private void WriteModel()
        {
            SetF(_model.PlayerHp, _sim.PlayerHp);
            SetF(_model.PlayerMaxHp, _sim.PlayerMaxHp);
            SetI(_model.Wave, _sim.WaveIndex);
            SetI(_model.Kills, _sim.Kills);
            SetI(_model.Score, _sim.Score);
            SetI(_model.EnemyCount, _sim.EnemyCount);
            SetI(_model.ProjectileCount, _sim.ProjectileCount);
            SetI(_model.WreckCount, _sim.WreckSlotCount);
            SetF(_model.SimTickMs, _simTickMs);
        }

        private static void SetF(R3.RP<float> rp, float v) { if (!Mathf.Approximately(rp.Value, v)) rp.Value = v; }
        private static void SetI(R3.RP<int> rp, int v) { if (rp.Value != v) rp.Value = v; }

        private void GoToResult()
        {
            _ready = false;
            var result = new BattleResult(_sim.WaveIndex, _sim.Score, _sim.Kills, _retreated);
            FlowNav.Go(this.GetUtility<IGameFlow>(), new ResultState(result));
        }

        private static Vector3 ToWorld(System.Numerics.Vector2 p, float z = 0f) => new(p.X, p.Y, z);
        private static Vector3 WithZ(Vector3 p, float z) => new(p.x, p.y, z);

        protected override void OnDestroy()
        {
            Time.timeScale = 1f; // 离开战斗还原全局速度（游戏速度是战斗内的临时旋钮，不外溢到标题 / 结算）
            _sim?.Dispose();
            _sim = null;
            base.OnDestroy();
        }
    }
}
