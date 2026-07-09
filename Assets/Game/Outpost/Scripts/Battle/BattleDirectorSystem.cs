using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Framework;
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
    /// 战斗导演：把纯 C# 模拟内核（<see cref="IBattleSim"/>）接到 Unity 表现与框架数据流上——
    /// 每帧 <c>Tick</c> 模拟、把聚合值写进 <see cref="BattleModel"/>（HUD 只读订阅）、把逐事件（刷怪/击发/命中/自爆/击杀）
    /// 翻成池化演出（敌人视觉 / 弹道曳光 / 脉冲圈 / 烟雾 / 碎片 / 伤害飘字 / 相机震动 / 炮塔朝向），终局把战绩交给 <see cref="IGameFlow"/> 进结算。
    /// "炮塔击发一发"(<c>TurretFired</c>)与"敌人被击中"(<c>EnemyHit</c>)是两条独立事件：前者驱动炮口/曳光（含火墙里转向途中的空放），后者驱动敌人白闪/击杀爆炸。
    /// <para>模拟内核对 Unity 一无所知，置换为 ECS 后端时本类的"事件→视觉/Model"翻译层原样保留（接缝价值所在）。
    /// System 层不能 ExecuteCommand（防环），故终局直接 <c>GetUtility&lt;IGameFlow&gt;()</c> 驱动流程。</para>
    /// <para>无限模式：波次一波比一波难，唯一终态是哨站失守；炮塔朝向与开火时机由内核决定（内核按回转速度转向、对准才发命中事件），
    /// 本类只按内核给的角度画炮管、命中即演出，不再自行平滑瞄准或缓冲开火。</para>
    /// </summary>
    public sealed class BattleDirectorSystem : MonoSystemBase
    {
        [SerializeField, Tooltip("敌人 / 特效的挂载根（世界空间，XY 平面）。")]
        private Transform _arenaRoot;

        [SerializeField, Tooltip("敌人视觉 prefab（EnemyView：着色 / 换形 / 白闪 / 血量暗化）。")]
        private GameObject _enemyPrefab;

        [SerializeField, Tooltip("伤害飘字 prefab（含 DamageFloater）。")]
        private GameObject _floaterPrefab;

        [SerializeField, Tooltip("弹道曳光 prefab（含 ProjectileTracer；开火曳光 / 击毁碎片共用）。")]
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

        private IBattleSim _sim;
        private BattleModel _model;
        private UpgradeModel _upgradeModel;
        private Tables _cfg;
        private bool _ready;

        // 终局 / 波间的定时推进
        private float _resultDelay = 1.2f;
        private float _interWaveDelay = 1.5f;
        private float _maxRange;       // 索敌半径升级上限：到顶后三选一不再提供增程雷达
        private float _maxAttack;      // 攻击升级上限：到顶后三选一不再提供强化弹头
        private float _maxRotation;    // 回转升级上限：到顶后三选一不再提供回转伺服
        private bool _ending;
        private float _endTimer;
        private bool _betweenWaves;
        private float _waveTimer;
        private bool _awaitingChoice; // 波清空后停在此、等玩家三选一（IsChoosing 面板弹出中）
        private float _lastRange = -1f;
        private float _autoPickTimer; // 托管：卡片亮相后到自动选定的倒计时（等待抉择期间生效）

        // 托管自动选卡前的亮相延迟：让三选一卡片先露个脸再被自动选掉，观战时看得清导演选了什么。
        private const float AutoPickRevealDelay = 0.6f;

        // 波间三选一：从（当前可用的）升级里随机取 3 个不同的候选。随机是纯展示（玩家的选择才是真输入），用领域 List 免每次分配。
        private const int ChoiceCount = 3;
        private readonly List<Upgrade> _upgradePool = new();

        // 敌人实例 id → 视觉，逐帧同步位置 / 血量；击杀时按 id 回收。
        private readonly Dictionary<int, EnemyView> _enemyViews = new();

        // 所有一次性特效（曳光 / 脉冲 / 飘字 / 碎片）的统一回收列表——都实现 ITimedEffect。
        private readonly List<MonoBehaviour> _effects = new();

        // 高射速下每帧的命中 / 击毁演出预算——超出即降级（不再生成曳光/爆炸，伤害仍每发结算），防池爆 + 刷屏。
        // 也是 OOP 后端在弹幕级吞吐下"看得出压力"的地方（M6 换 ECS 后同场景应流畅）。
        private const int HitFxPerFrame = 12;
        private const int KillFxPerFrame = 6;
        private int _hitFxBudget;
        private int _killFxBudget;

        // 原型演出参数（表未含表现字段，进表是后续项）。按原型 id 选色 / 形 / 体量 / 爆炸倍率。
        private static readonly Color FodderColor = new(0.75f, 1.0f, 0.35f);  // 无人机（炮灰，黄绿）
        private static readonly Color StrikerColor = new(1.0f, 0.34f, 0.22f); // 突袭者（快速突击，橙红）
        private static readonly Color HeavyColor = new(0.72f, 0.42f, 1.0f);   // 装甲兵（重甲，紫）
        private static readonly Color ScoutColor = new(0.35f, 0.95f, 1.0f);   // 掠袭机（极速，青）
        private static readonly Color SiegeColor = new(1.0f, 0.55f, 0.15f);   // 攻城核（重装，橙）
        private static readonly Color PlayerHitColor = new(1f, 0.30f, 0.24f);
        private static readonly Color TracerColor = new(0.9f, 3.2f, 3.0f, 1f);
        private static readonly Color ImpactColor = new(2.0f, 2.4f, 2.4f, 0.85f);
        private static readonly Color MuzzleColor = new(1.2f, 2.8f, 2.6f, 0.7f);
        private static readonly Color SmokeColor = new(0.42f, 0.42f, 0.46f, 0.55f); // 非 HDR 灰烟（不发光，读成烟）
        private static readonly Color DebrisColor = new(2.6f, 1.5f, 0.5f, 1f);       // HDR 暖橙火花碎片

        // 高射速火墙的曳光散射半径（世界单位）：按预热系数缩放，让密集连发读成一片弹雨而非一条直线（纯表现，不改内核命中）。
        private const float TracerScatter = 0.42f;

        // 表现层的 Z 分层（相机 -10 朝 +Z 看）：地板 0.5 > 地面环 0.3 > 单位 0 > 脉冲 -0.2 > 曳光 -0.3 > 飘字 -0.8。
        private const float PulseZ = -0.2f;
        private const float TracerZ = -0.3f;
        private const float FloaterZ = -0.8f;

        // 每种敌人的表现参数：颜色 / 体型直径 / 网格 / 是否转向来袭方向 / 爆炸体量倍率。
        private readonly struct EnemyVisual
        {
            public readonly Color Color;
            public readonly float Diameter;
            public readonly Mesh Mesh;
            public readonly bool FaceTravel;
            public readonly float ExplosionScale;

            public EnemyVisual(Color color, float diameter, Mesh mesh, bool faceTravel, float explosionScale)
            {
                Color = color;
                Diameter = diameter;
                Mesh = mesh;
                FaceTravel = faceTravel;
                ExplosionScale = explosionScale;
            }
        }

        // 原型 id → 表现参数（约定同 enemy.json：1 无人机 / 2 突袭者 / 3 装甲兵 / 4 掠袭机 / 5 攻城核）。
        private static EnemyVisual GetVisual(int archId) => archId switch
        {
            1 => new EnemyVisual(FodderColor, 0.5f, OutpostMeshes.Dart, true, 0.55f),
            3 => new EnemyVisual(HeavyColor, 1.35f, OutpostMeshes.Hexagon, false, 1.4f),
            4 => new EnemyVisual(ScoutColor, 0.55f, OutpostMeshes.Needle, true, 0.7f),
            5 => new EnemyVisual(SiegeColor, 1.8f, OutpostMeshes.Octagon, false, 2.0f),
            _ => new EnemyVisual(StrikerColor, 0.85f, OutpostMeshes.Arrowhead, true, 1.0f),
        };

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
            var cfg = _cfg;
            _resultDelay = cfg.TbBattleGlobal.Data.ResultDelay;
            _interWaveDelay = cfg.TbBattleGlobal.Data.InterWaveDelay;
            _maxRange = cfg.TbBattleGlobal.Data.PlayerMaxRange;
            _maxAttack = cfg.TbBattleGlobal.Data.PlayerMaxAttack;
            _maxRotation = cfg.TbBattleGlobal.Data.PlayerMaxRotationSpeed;

            _model = this.GetModel<BattleModel>();
            _upgradeModel = this.GetModel<UpgradeModel>();
            var setup = BattleSetupFactory.Build(cfg, _seed != 0 ? _seed : System.Environment.TickCount);

            _sim = new ReferenceBattleSim();
            _sim.EnemySpawned += OnEnemySpawned;
            _sim.EnemyHit += OnEnemyHit;
            _sim.TurretFired += OnTurretFired;
            _sim.EnemyDetonated += OnEnemyDetonated;
            _sim.WaveCleared += OnWaveCleared;

            _sim.Start(setup);

            _decor.Init(setup.ArenaRadius);
            _model.PlayerMaxHp.Value = _sim.PlayerMaxHp;
            WriteModel();
            _ready = true;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _hitFxBudget = HitFxPerFrame;   // 每帧刷新演出预算（高射速下超出即降级，见 OnEnemyHit）
            _killFxBudget = KillFxPerFrame;
            AdvanceEffects();
            if (!_ready || _sim == null) return;

            if (_ending)
            {
                _endTimer -= dt;
                if (_endTimer <= 0f) GoToResult();
                return;
            }

            // 波清空后停在此、等玩家三选一——面板由 UpgradeModel.IsChoosing 驱动，抉择前不推进。
            if (_awaitingChoice)
            {
                SyncEnemyViews();
                _turret.Face(_sim.TurretAngle);
                _turret.SetSpin(_sim.SpinUp);
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
                }
            }
            else
            {
                _sim.Tick(dt);
            }

            SyncEnemyViews();
            SyncRangeRing();
            _turret.Face(_sim.TurretAngle); // 内核已按回转速度算好朝向，表现层照画
            _turret.SetSpin(_sim.SpinUp);   // 射速预热 → 炮塔核心涨亮
            WriteModel();

            if (_sim.Phase == BattlePhase.Defeat)
            {
                _ending = true;
                _endTimer = _resultDelay;
                // 终局强调：战败红色大脉冲，从哨站位置扩散。
                SpawnPulse(WithZ(_turret.transform.position, PulseZ), new Color(2.4f, 0.5f, 0.4f, 0.9f), 1f, 16f, 0.9f);
            }
        }

        // ── 模拟事件 → 演出 ──────────────────────────────────────────────────

        private void OnEnemySpawned(EnemySpawnedEvent e)
        {
            var v = GetVisual(e.ArchetypeId);
            var go = Bag.Spawn(_enemyPrefab, _arenaRoot);
            var view = go.GetComponent<EnemyView>();
            view.Init(v.Color, v.Diameter, v.Mesh, v.FaceTravel);
            view.SetGroundPosition(ToWorld(e.Position));
            _enemyViews[e.EnemyId] = view;

            // 出生提示圈：从出生点收缩感的小脉冲，提示"这里来了新敌人"。
            var c = v.Color * 1.2f;
            c.a = 0.5f;
            SpawnPulse(ToWorld(e.Position, PulseZ), c, 2.6f * v.ExplosionScale, 0.4f, 0.35f);
        }

        // 炮塔击发一发（命中或空放都触发）：画炮口闪光 + 曳光 + 命中冲击。与 OnEnemyHit（敌人反应）分离——
        // 转向途中的空放也走这里，画出射向炮口方向的火舌。高射速下每帧限量，超预算即跳过（伤害早已在内核结算，防池爆 + 刷屏）。
        private void OnTurretFired(TurretFiredEvent e)
        {
            if (_hitFxBudget <= 0) return;
            _hitFxBudget--;
            FireBurst(ToWorld(e.Aim), e.Hit);
        }

        private void OnEnemyHit(EnemyHitEvent e)
        {
            var hitPos = ToWorld(e.Position);

            // 近距拦截的溅射警示：击毁点离基地过近，弹片仍连带削基地（基地红色冲击 + 轻震 + 飘字）。始终演（rare、要紧）。
            if (e.SplashDamage > 0f)
            {
                _shaker.Shake(0.14f, 0.16f);
                var warn = PlayerHitColor * 1.5f;
                warn.a = 0.7f;
                SpawnPulse(WithZ(_turret.transform.position, PulseZ), warn, 0.4f, 2.0f, 0.28f);
                SpawnFloater($"-{Mathf.CeilToInt(e.SplashDamage)}", PlayerHitColor,
                    WithZ(_turret.transform.position + new Vector3(0f, 0.9f, 0f), FloaterZ));
            }

            if (_enemyViews.TryGetValue(e.EnemyId, out var view) && view != null)
            {
                if (e.Killed)
                {
                    _enemyViews.Remove(e.EnemyId);
                    if (_killFxBudget > 0) { _killFxBudget--; SpawnKillExplosion(hitPos, e.ArchetypeId); }
                    Bag.Despawn(view.gameObject);
                }
                else
                {
                    view.Flash();
                }
            }
        }

        // 敌人抵达基地自爆：基地受创（震屏 + 红色冲击 + 掉血飘字 + 炮台冒烟）+ 来袭弹自身的爆炸 + 回收其视觉。
        private void OnEnemyDetonated(EnemyDetonatedEvent e)
        {
            var pos = ToWorld(e.Position);
            var playerPos = _turret.transform.position;
            var v = GetVisual(e.ArchetypeId);
            _shaker.Shake(0.42f, 0.42f); // 抵达基地的自爆比拦截更重

            SpawnFloater($"-{Mathf.CeilToInt(e.Damage)}", PlayerHitColor,
                WithZ(playerPos + new Vector3(0f, 0.9f, 0f), FloaterZ));

            // 基地受创：红色冲击环 + 炮台冒烟（多缕，读成结构受损）。
            var warn = PlayerHitColor * 2.0f;
            warn.a = 0.88f;
            SpawnPulse(WithZ(playerPos, PulseZ), warn, 0.6f, 3.8f, 0.4f);
            SpawnSmoke(playerPos, 0.6f, 2.6f, 0.85f);
            SpawnSmoke(playerPos + new Vector3(0.5f, 0.3f, 0f), 0.4f, 1.8f, 0.7f);

            // 来袭弹自身的爆炸：按原型色的大脉冲 + 亮白核 + 外扩冲击波 + 碎片飞溅 + 烟。
            var boom = v.Color * 2.7f;
            boom.a = 1f;
            SpawnPulse(WithZ(pos, PulseZ), boom, 0.5f, 4.1f * v.ExplosionScale, 0.56f);
            SpawnPulse(WithZ(pos, PulseZ), new Color(2.9f, 2.9f, 3.1f, 0.95f), 0.2f, 2.1f * v.ExplosionScale, 0.28f);
            SpawnPulse(WithZ(pos, PulseZ), boom, 1.7f * v.ExplosionScale, 5.2f * v.ExplosionScale, 0.34f);
            SpawnDebris(pos, 6, 2.0f * v.ExplosionScale, 0.32f);
            SpawnSmoke(pos, 0.4f, 2.2f * v.ExplosionScale, 0.7f);

            // 回收自爆敌人的视觉（它已从模拟移除）。
            if (_enemyViews.TryGetValue(e.EnemyId, out var view))
            {
                _enemyViews.Remove(e.EnemyId);
                if (view != null) Bag.Despawn(view.gameObject);
            }
        }

        // 拦截击毁的爆炸：按原型色扩散大圈 + 亮白核 + 外扩冲击波 + 向外飞溅的碎片 + 一缕烟，读作"被打爆的来袭弹"。
        private void SpawnKillExplosion(Vector3 hitPos, int archId)
        {
            var v = GetVisual(archId);
            var c = v.Color * 2.6f;
            c.a = 0.95f;
            SpawnPulse(WithZ(hitPos, PulseZ), c, 0.4f, 3.7f * v.ExplosionScale, 0.52f);
            SpawnPulse(WithZ(hitPos, PulseZ), new Color(2.7f, 2.8f, 3.0f, 0.92f), 0.15f, 1.8f * v.ExplosionScale, 0.24f);
            // 大个体（突袭者及以上）才加外扩冲击波 + 碎片 + 烟；炮灰 / 掠袭机只留脉冲，省得后期海量击杀刷屏。
            if (v.ExplosionScale >= 0.8f)
            {
                SpawnPulse(WithZ(hitPos, PulseZ), c, 1.4f * v.ExplosionScale, 4.6f * v.ExplosionScale, 0.3f); // 外扩冲击波
                SpawnDebris(hitPos, 5, 1.7f * v.ExplosionScale, 0.28f);
                SpawnSmoke(hitPos, 0.3f, 1.6f * v.ExplosionScale, 0.6f);
            }
        }

        private void OnWaveCleared(int wave)
        {
            // 每波清空：停下推进、弹三选一升级面板，等玩家抉择（ChooseUpgrade 才继续）。
            OfferUpgrades();
        }

        // ── 波间三选一升级 ─────────────────────────────────────────────────

        // 从当前可用的升级里随机取 3 个不同的候选，填进 UpgradeModel 并置 IsChoosing=true（面板据此弹出）。
        // 索敌半径已到上限时把增程雷达移出候选池——顶了的升级不再占抽卡位。
        private void OfferUpgrades()
        {
            bool rangeCapped = _maxRange > 0f && _sim.PlayerRange >= _maxRange - 0.001f;
            bool attackCapped = _maxAttack > 0f && _sim.PlayerAttack >= _maxAttack - 0.001f;
            bool rotCapped = _maxRotation > 0f && _sim.PlayerRotationSpeed >= _maxRotation - 0.001f;

            _upgradePool.Clear();
            foreach (var u in _cfg.TbUpgrade.DataList)
            {
                if (rangeCapped && u.Kind == UpgradeKind.Range) continue;
                if (attackCapped && u.Kind == UpgradeKind.Attack) continue;          // 攻击已封顶：溢出无意义，移出抽卡池
                if (rotCapped && u.Kind == UpgradeKind.RotationSpeed) continue;      // 回转已封顶
                _upgradePool.Add(u);
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

            // 强化反馈：玩家位置金色脉冲；射程等即时生效，SyncRangeRing 下帧会外扩射程圈。
            SpawnPulse(WithZ(_turret.transform.position, PulseZ), new Color(1.5f, 1.15f, 0.4f, 0.9f), 0.5f, 4f, 0.5f);

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

        // 托管选卡优先级（数字越小越优先）：攻速 / 转速是火力与覆盖的核心，探测范围其次；血上限 / 回血 / 攻击对火墙流意义小，尽量不选。
        private static int AutoPriority(UpgradeKind kind) => kind switch
        {
            UpgradeKind.AttackSpeed => 0,
            UpgradeKind.RotationSpeed => 1,
            UpgradeKind.Range => 2,
            UpgradeKind.MaxHp => 3,
            UpgradeKind.Regen => 4,
            UpgradeKind.Attack => 5,
            _ => 6,
        };

        private void SyncEnemyViews()
        {
            // 逐帧同步存活敌人的位置 / 血量（炮塔朝向由内核决定，这里不再参与瞄准）。
            for (int i = 0; i < _sim.EnemyCount; i++)
            {
                var snap = _sim.GetEnemy(i);
                if (_enemyViews.TryGetValue(snap.Id, out var view) && view != null)
                {
                    view.SetGroundPosition(ToWorld(snap.Position));
                    view.SetHpRatio(snap.MaxHp > 0f ? snap.Hp / snap.MaxHp : 0f);
                }
            }
        }

        private void SyncRangeRing()
        {
            if (Mathf.Approximately(_sim.PlayerRange, _lastRange)) return;
            _lastRange = _sim.PlayerRange;
            _decor.SetRange(_lastRange);
        }

        // 开火演出：炮管后坐 + 炮口闪光 + 曳光从当前炮口飞向落点（hitscan 伤害早已结算，此处纯装饰）。
        // 落点按预热强度做随机散射——高射速下密集连发才不会叠成一条直线，读成一片弹雨；命中冲击仍落在真实弹着点、不随散射抖。
        private void FireBurst(Vector3 aim, bool hit)
        {
            _turret.Fire();
            var muzzle = WithZ(_turret.MuzzleWorldPos, TracerZ);
            SpawnPulse(muzzle, MuzzleColor, 0.15f, 0.55f, 0.12f);

            var end = aim;
            float scatter = TracerScatter * _sim.SpinUp; // 越预热越散（点射时几乎不散、火墙时散成弹雨）
            if (scatter > 0.001f)
            {
                var j = Random.insideUnitCircle * scatter;
                end += new Vector3(j.x, j.y, 0f);
            }
            var tracer = Bag.Spawn(_tracerPrefab, _arenaRoot).GetComponent<ProjectileTracer>();
            tracer.Play(muzzle, WithZ(end, TracerZ), TracerColor);
            _effects.Add(tracer);

            if (hit) SpawnPulse(WithZ(aim, PulseZ), ImpactColor, 0.2f, 0.9f, 0.16f); // 命中冲击落在真实弹着点
        }

        // 击毁 / 自爆的碎片飞溅：从爆点向随机方向抛出若干短促发光碎片流（复用曳光，指定飞行时长拉出可见弧）。
        private void SpawnDebris(Vector3 center, int count, float spread, float duration)
        {
            for (int i = 0; i < count; i++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float len = spread * (0.55f + 0.6f * Random.value);
                var to = center + new Vector3(Mathf.Cos(ang) * len, Mathf.Sin(ang) * len, 0f);
                var shard = Bag.Spawn(_tracerPrefab, _arenaRoot).GetComponent<ProjectileTracer>();
                shard.Play(WithZ(center, TracerZ), WithZ(to, TracerZ), DebrisColor, duration);
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
        }

        private static void SetF(R3.RP<float> rp, float v) { if (!Mathf.Approximately(rp.Value, v)) rp.Value = v; }
        private static void SetI(R3.RP<int> rp, int v) { if (rp.Value != v) rp.Value = v; }

        private void GoToResult()
        {
            _ready = false;
            var result = new BattleResult(_sim.WaveIndex, _sim.Score, _sim.Kills);
            FlowNav.Go(this.GetUtility<IGameFlow>(), new ResultState(result));
        }

        private static Vector3 ToWorld(System.Numerics.Vector2 p, float z = 0f) => new(p.X, p.Y, z);
        private static Vector3 WithZ(Vector3 p, float z) => new(p.x, p.y, z);

        protected override void OnDestroy()
        {
            _sim?.Dispose();
            _sim = null;
            base.OnDestroy();
        }
    }
}
