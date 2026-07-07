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
    public sealed class BattleDirector : MonoSystemBase
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

        [SerializeField, Tooltip("快速种网格（顶视圆形）。")]
        private Mesh _fastMesh;

        [SerializeField, Tooltip("装甲种网格（顶视方形）。")]
        private Mesh _tankMesh;

        [SerializeField, Tooltip("随机种子；0 = 用启动时间（每局不同的出生角度）。战斗结果几乎与种子无关——竞技场旋转对称。")]
        private int _seed;

        private IBattleSim _sim;
        private BattleModel _model;
        private bool _ready;

        // 终局 / 波间的定时推进
        private float _resultDelay = 1.2f;
        private float _interWaveDelay = 1.5f;
        private bool _ending;
        private bool _victory;
        private float _endTimer;
        private bool _betweenWaves;
        private float _waveTimer;
        private float _lastRange = -1f;

        // 敌人实例 id → 视觉，逐帧同步位置 / 血量；击杀时按 id 回收。
        private readonly Dictionary<int, EnemyView> _enemyViews = new();

        // 所有一次性特效（曳光 / 脉冲 / 飘字）的统一回收列表——都实现 ITimedEffect。
        private readonly List<MonoBehaviour> _effects = new();

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
                Debug.LogError("[BattleDirector] 配置未就绪，无法开始战斗。");
                return;
            }

            var cfg = config.Tables;
            _resultDelay = cfg.TbBattleGlobal.Data.ResultDelay;
            _interWaveDelay = cfg.TbBattleGlobal.Data.InterWaveDelay;

            _model = this.GetModel<BattleModel>();
            var setup = BattleSetupFactory.Build(cfg, _seed != 0 ? _seed : System.Environment.TickCount);

            _sim = new ReferenceBattleSim();
            _sim.EnemySpawned += OnEnemySpawned;
            _sim.EnemyHit += OnEnemyHit;
            _sim.PlayerHit += OnPlayerHit;
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

            if (_ending)
            {
                _endTimer -= dt;
                if (_endTimer <= 0f) GoToResult();
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
            view.Init(tank ? TankColor : FastColor, tank ? 1.3f : 0.8f, tank ? _tankMesh : _fastMesh);
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

            // 开火三连演出：炮管后坐 + 炮口闪光 + 曳光飞向命中点（hitscan 的伤害已同帧结算，曳光纯装饰）。
            _turret.AimAt(hitPos);
            _turret.Fire();
            var muzzle = WithZ(_turret.MuzzleWorldPos, TracerZ);
            SpawnPulse(muzzle, MuzzleColor, 0.15f, 0.55f, 0.12f);
            var tracer = Bag.Spawn(_tracerPrefab, _arenaRoot).GetComponent<ProjectileTracer>();
            tracer.Play(muzzle, WithZ(hitPos, TracerZ), TracerColor);
            _effects.Add(tracer);

            SpawnFloater(((int)e.Damage).ToString(), FloaterHitColor, ToWorld(e.Position, FloaterZ));
            SpawnPulse(WithZ(hitPos, PulseZ), ImpactColor, 0.2f, 0.9f, 0.16f);

            if (_enemyViews.TryGetValue(e.EnemyId, out var view) && view != null)
            {
                if (e.Killed)
                {
                    _enemyViews.Remove(e.EnemyId);
                    // 死亡爆发：按原型色扩散大圈。
                    bool tank = e.ArchetypeId == 2;
                    var c = (tank ? TankColor : FastColor) * 2f;
                    c.a = 0.9f;
                    SpawnPulse(WithZ(hitPos, PulseZ), c, 0.4f, tank ? 3.4f : 2.4f, 0.4f);
                    Bag.Despawn(view.gameObject);
                }
                else
                {
                    view.Flash();
                }
            }
        }

        private void OnPlayerHit(PlayerHitEvent e)
        {
            _shaker.Shake(0.22f, 0.25f);
            var playerPos = _turret.transform.position;
            SpawnFloater($"-{Mathf.CeilToInt(e.Damage)}", PlayerHitColor, WithZ(playerPos + new Vector3(0f, 0.9f, 0f), FloaterZ));
            var c = PlayerHitColor * 1.6f;
            c.a = 0.8f;
            SpawnPulse(WithZ(playerPos, PulseZ), c, 0.6f, 2.6f, 0.3f);

            // 让发动攻击的那只敌人向玩家猛扑一下 + 在其接触点炸一小圈啃咬光——不再是"贴脸静止"。
            if (_enemyViews.TryGetValue(e.EnemyId, out var attacker) && attacker != null)
            {
                attacker.Lunge();
                var bite = ImpactColor;
                bite.a = 0.7f;
                SpawnPulse(WithZ(ToWorld(e.Position), PulseZ), bite, 0.15f, 0.8f, 0.18f);
            }
        }

        private void OnWaveCleared(int wave)
        {
            _betweenWaves = true;
            _waveTimer = _interWaveDelay;
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

            if (hasTarget) _turret.AimAt(nearestPos);
        }

        private void SyncRangeRing()
        {
            if (Mathf.Approximately(_sim.PlayerRange, _lastRange)) return;
            _lastRange = _sim.PlayerRange;
            _decor.SetRange(_lastRange);
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
