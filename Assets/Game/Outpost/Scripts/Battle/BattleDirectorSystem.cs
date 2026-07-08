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
    /// 每帧 <c>Tick</c> 模拟、把聚合值写进 <see cref="BattleModel"/>（HUD 只读订阅）、把逐事件（刷怪/命中/受击/击杀）
    /// 翻成池化演出（敌人视觉 / 弹道曳光 / 脉冲圈 / 伤害飘字 / 相机震动），终局把结果交给 <see cref="IGameFlow"/> 进结算。
    /// <para>模拟内核对 Unity 一无所知，置换为 ECS 后端时本类的"事件→视觉/Model"翻译层原样保留（接缝价值所在）。
    /// System 层不能 ExecuteCommand（防环），故终局直接 <c>GetUtility&lt;IGameFlow&gt;()</c> 驱动流程。</para>
    /// </summary>
    public sealed class BattleDirectorSystem : MonoSystemBase
    {
        [SerializeField, Tooltip("敌人 / 特效的挂载根（世界空间，XY 平面）。")]
        private Transform _arenaRoot;

        [SerializeField, Tooltip("敌人视觉 prefab（EnemyView：着色 / 换形 / 白闪 / 血量暗化）。")]
        private GameObject _enemyPrefab;

        [SerializeField, Tooltip("伤害飘字 prefab（含 DamageFloater）。")]
        private GameObject _floaterPrefab;

        [SerializeField, Tooltip("弹道曳光 prefab（含 ProjectileTracer）。")]
        private GameObject _tracerPrefab;

        [SerializeField, Tooltip("脉冲圈 prefab（含 PulseEffect；命中 / 死亡 / 出生 / 炮口闪光共用）。")]
        private GameObject _pulsePrefab;

        [SerializeField, Tooltip("玩家炮塔表现（瞄准 / 后坐由 director 驱动）。")]
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
        private bool _ending;
        private bool _victory;
        private float _endTimer;
        private bool _betweenWaves;
        private float _waveTimer;
        private bool _awaitingChoice; // 波清空后停在此、等玩家三选一（IsChoosing 面板弹出中）
        private float _lastRange = -1f;

        // 波间三选一：从全部升级里随机取 3 个不同的候选。随机是纯展示（玩家的选择才是真输入），用领域 List 免每次分配。
        private const int ChoiceCount = 3;
        private readonly List<Upgrade> _upgradePool = new();

        // 敌人实例 id → 视觉，逐帧同步位置 / 血量；击杀时按 id 回收。
        private readonly Dictionary<int, EnemyView> _enemyViews = new();

        // 所有一次性特效（曳光 / 脉冲 / 飘字）的统一回收列表——都实现 ITimedEffect。
        private readonly List<MonoBehaviour> _effects = new();

        // 待发射击缓冲：模拟是 hitscan、伤害命中同帧已结算，但"开火演出"（炮口闪光 + 曳光）要压到炮管转到位后才释放，
        // 避免"还没转过去就冒火"。每帧检查炮管是否对准该击的目标点，对准或超时才发。
        private struct PendingShot { public Vector3 Target; public float Age; }
        private readonly List<PendingShot> _pendingShots = new();
        private const float AimToleranceDeg = 7f;   // 炮口角度差在此内即视为对准
        private const float MaxAimWait = 0.3f;      // 兜底：超时强发，防目标瞬移/异常时卡住不开火

        // 原型演出参数（M1 硬编码：表未含表现字段，进表是后续项）。id=2 装甲种，其余按快速种。
        private static readonly Color FastColor = new(1.0f, 0.34f, 0.22f);
        private static readonly Color TankColor = new(0.72f, 0.42f, 1.0f);
        private static readonly Color FloaterHitColor = new(1f, 0.95f, 0.55f);
        private static readonly Color PlayerHitColor = new(1f, 0.30f, 0.24f);
        private static readonly Color TracerColor = new(0.9f, 3.2f, 3.0f, 1f);
        private static readonly Color ImpactColor = new(2.0f, 2.4f, 2.4f, 0.85f);
        private static readonly Color MuzzleColor = new(1.2f, 2.8f, 2.6f, 0.7f);

        // 表现层的 Z 分层（相机 -10 朝 +Z 看）：地板 0.5 > 地面环 0.3 > 单位 0 > 脉冲 -0.2 > 曳光 -0.3 > 飘字 -0.8。
        private const float PulseZ = -0.2f;
        private const float TracerZ = -0.3f;
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
            var cfg = _cfg;
            _resultDelay = cfg.TbBattleGlobal.Data.ResultDelay;
            _interWaveDelay = cfg.TbBattleGlobal.Data.InterWaveDelay;

            _model = this.GetModel<BattleModel>();
            _upgradeModel = this.GetModel<UpgradeModel>();
            var setup = BattleSetupFactory.Build(cfg, _seed != 0 ? _seed : System.Environment.TickCount);

            _sim = new ReferenceBattleSim();
            _sim.EnemySpawned += OnEnemySpawned;
            _sim.EnemyHit += OnEnemyHit;
            _sim.EnemyDetonated += OnEnemyDetonated;
            _sim.WaveCleared += OnWaveCleared;

            _sim.Start(setup);

            _decor.Init(setup.ArenaRadius);
            _model.PlayerMaxHp.Value = _sim.PlayerMaxHp;
            _model.WaveCount.Value = _sim.WaveCount;
            WriteModel();
            _ready = true;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            AdvanceEffects();
            if (!_ready || _sim == null) return;

            // 待发射击独立于战斗相位推进：无论 tick / 波间 / 抉择 / 终局，缓冲里对准的炮击都照常释放（含最后一击的曳光）。
            ProcessPendingShots(dt);

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
            WriteModel();

            if (_sim.Phase is BattlePhase.Victory or BattlePhase.Defeat)
            {
                _ending = true;
                _victory = _sim.Phase == BattlePhase.Victory;
                _endTimer = _resultDelay;
                // 终局强调：胜利青色大脉冲 / 战败红色大脉冲，从玩家位置扩散。
                var c = _victory ? new Color(0.5f, 2.4f, 2.2f, 0.9f) : new Color(2.4f, 0.5f, 0.4f, 0.9f);
                SpawnPulse(WithZ(_turret.transform.position, PulseZ), c, 1f, 16f, 0.9f);
            }
        }

        // ── 模拟事件 → 演出 ──────────────────────────────────────────────────

        private void OnEnemySpawned(EnemySpawnedEvent e)
        {
            var go = Bag.Spawn(_enemyPrefab, _arenaRoot);
            var view = go.GetComponent<EnemyView>();
            bool tank = e.ArchetypeId == 2;
            // 快速种 = 指向来袭方向的箭头（faceTravel）；装甲种 = 固定朝向的厚重六边形。
            view.Init(
                tank ? TankColor : FastColor,
                tank ? 1.3f : 0.8f,
                tank ? OutpostMeshes.Hexagon : OutpostMeshes.Arrowhead,
                !tank);
            view.SetGroundPosition(ToWorld(e.Position));
            _enemyViews[e.EnemyId] = view;

            // 出生提示圈：从出生点收缩感的小脉冲，提示"这里来了新敌人"。
            var c = (tank ? TankColor : FastColor) * 1.2f;
            c.a = 0.5f;
            SpawnPulse(ToWorld(e.Position, PulseZ), c, tank ? 2.6f : 1.8f, 0.4f, 0.35f);
        }

        private void OnEnemyHit(EnemyHitEvent e)
        {
            var hitPos = ToWorld(e.Position);

            // 开火演出的时机门控：命中反馈（飘字 + 命中脉冲）表示 hitscan 已结算、保持即时；但"炮口闪光 + 曳光"
            // 要等炮管指向该击目标才放，避免"还没转过去就冒火"。
            // 此刻若已对准（该敌人正是炮管一直追踪的最近目标——常态）就即刻开火；否则入缓冲，交给 ProcessPendingShots
            // 逐帧等转到位。⚠ 关键：必须在这里当场判定，不能无脑入缓冲——否则等下一帧才检查时，SyncEnemyViews 可能已
            // 把炮管转向下一个最近目标（尤其这一击刚好击杀），旧击错过瞄准窗口、只能等超时错着方向发射（曾经的 bug）。
            if (_turret.IsAimedAt(hitPos, AimToleranceDeg))
                FireBurst(hitPos);
            else
                _pendingShots.Add(new PendingShot { Target = hitPos });

            SpawnFloater(((int)e.Damage).ToString(), FloaterHitColor, ToWorld(e.Position, FloaterZ));
            SpawnPulse(WithZ(hitPos, PulseZ), ImpactColor, 0.2f, 0.9f, 0.16f);

            // 近距拦截的溅射警示：击毁点离基地过近，弹片仍连带削基地（基地红色冲击 + 轻震 + 飘字）。
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
                    // 拦截爆炸：按原型色扩散大圈 + 亮白核 + 一圈冲击波，读作"被打爆的来袭弹"。
                    bool tank = e.ArchetypeId == 2;
                    var c = (tank ? TankColor : FastColor) * 2.6f;
                    c.a = 0.95f;
                    SpawnPulse(WithZ(hitPos, PulseZ), c, 0.4f, tank ? 5.0f : 3.7f, 0.52f);
                    SpawnPulse(WithZ(hitPos, PulseZ), new Color(2.7f, 2.8f, 3.0f, 0.92f), 0.15f, 1.8f, 0.24f);
                    SpawnPulse(WithZ(hitPos, PulseZ), c, tank ? 2.0f : 1.4f, tank ? 6.2f : 4.6f, 0.3f); // 外扩冲击波
                    Bag.Despawn(view.gameObject);
                }
                else
                {
                    view.Flash();
                }
            }
        }

        // 敌人抵达基地自爆：基地受创（震屏 + 红色冲击 + 掉血飘字）+ 来袭弹自身的爆炸（原型色大脉冲 + 亮白核）+ 回收其视觉。
        private void OnEnemyDetonated(EnemyDetonatedEvent e)
        {
            var pos = ToWorld(e.Position);
            var playerPos = _turret.transform.position;
            _shaker.Shake(0.42f, 0.42f); // 抵达基地的自爆比拦截更重

            SpawnFloater($"-{Mathf.CeilToInt(e.Damage)}", PlayerHitColor,
                WithZ(playerPos + new Vector3(0f, 0.9f, 0f), FloaterZ));

            // 基地受创的红色冲击环。
            var warn = PlayerHitColor * 2.0f;
            warn.a = 0.88f;
            SpawnPulse(WithZ(playerPos, PulseZ), warn, 0.6f, 3.8f, 0.4f);

            // 来袭弹自身的爆炸：按原型色的大脉冲 + 亮白核 + 外扩冲击波。
            bool tank = e.ArchetypeId == 2;
            var boom = (tank ? TankColor : FastColor) * 2.7f;
            boom.a = 1f;
            SpawnPulse(WithZ(pos, PulseZ), boom, 0.5f, tank ? 5.6f : 4.1f, 0.56f);
            SpawnPulse(WithZ(pos, PulseZ), new Color(2.9f, 2.9f, 3.1f, 0.95f), 0.2f, 2.1f, 0.28f);
            SpawnPulse(WithZ(pos, PulseZ), boom, tank ? 2.4f : 1.7f, tank ? 7.0f : 5.2f, 0.34f);

            // 回收自爆敌人的视觉（它已从模拟移除）。
            if (_enemyViews.TryGetValue(e.EnemyId, out var view))
            {
                _enemyViews.Remove(e.EnemyId);
                if (view != null) Bag.Despawn(view.gameObject);
            }
        }

        private void OnWaveCleared(int wave)
        {
            // 非最后一波清空：停下推进、弹三选一升级面板，等玩家抉择（ChooseUpgrade 才继续）。
            OfferUpgrades();
        }

        // ── 波间三选一升级 ─────────────────────────────────────────────────

        // 从全部升级里随机取 3 个不同的候选，填进 UpgradeModel 并置 IsChoosing=true（面板据此弹出）。
        private void OfferUpgrades()
        {
            _upgradePool.Clear();
            _upgradePool.AddRange(_cfg.TbUpgrade.DataList);
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

        private void SyncEnemyViews()
        {
            // 同步位置 / 血量的同时找最近敌人，让炮管平时就追踪来袭方向（而非开火瞬间才转）。
            float nearestSq = float.MaxValue;
            Vector3 nearestPos = default;
            bool hasTarget = false;

            for (int i = 0; i < _sim.EnemyCount; i++)
            {
                var snap = _sim.GetEnemy(i);
                var pos = ToWorld(snap.Position);
                if (_enemyViews.TryGetValue(snap.Id, out var view) && view != null)
                {
                    view.SetGroundPosition(pos);
                    view.SetHpRatio(snap.MaxHp > 0f ? snap.Hp / snap.MaxHp : 0f);
                }

                float dsq = snap.Position.LengthSquared();
                if (dsq < nearestSq)
                {
                    nearestSq = dsq;
                    nearestPos = pos;
                    hasTarget = true;
                }
            }

            // 有待发（还没转到位的）炮击时，炮管优先转向该击目标把这一击"演"完，再回到追踪最近敌人——
            // 否则刚入缓冲的击会因炮管转去追新的最近目标而永远对不准，最终超时错向发射。待发清空即恢复常态追踪。
            if (_pendingShots.Count > 0)
                _turret.AimAt(_pendingShots[0].Target);
            else if (hasTarget)
                _turret.AimAt(nearestPos);
        }

        private void SyncRangeRing()
        {
            if (Mathf.Approximately(_sim.PlayerRange, _lastRange)) return;
            _lastRange = _sim.PlayerRange;
            _decor.SetRange(_lastRange);
        }

        // 逐帧检查待发缓冲：炮管已对准该击目标（或等待超时）才释放开火演出——炮口闪光 + 从当前炮口射向目标点的曳光。
        private void ProcessPendingShots(float dt)
        {
            for (int i = _pendingShots.Count - 1; i >= 0; i--)
            {
                var s = _pendingShots[i];
                s.Age += dt;
                if (_turret.IsAimedAt(s.Target, AimToleranceDeg) || s.Age >= MaxAimWait)
                {
                    FireBurst(s.Target);
                    _pendingShots.RemoveAt(i);
                }
                else
                {
                    _pendingShots[i] = s;
                }
            }
        }

        // 开火演出：炮管后坐 + 炮口闪光 + 曳光从当前炮口飞向目标点（hitscan 伤害早已结算，此处纯装饰）。
        private void FireBurst(Vector3 targetPos)
        {
            _turret.Fire();
            var muzzle = WithZ(_turret.MuzzleWorldPos, TracerZ);
            SpawnPulse(muzzle, MuzzleColor, 0.15f, 0.55f, 0.12f);
            var tracer = Bag.Spawn(_tracerPrefab, _arenaRoot).GetComponent<ProjectileTracer>();
            tracer.Play(muzzle, WithZ(targetPos, TracerZ), TracerColor);
            _effects.Add(tracer);
        }

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
            var result = new BattleResult(_victory, _sim.Score, _sim.WaveIndex, _sim.WaveCount, _sim.Kills);
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
