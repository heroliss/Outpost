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
    /// 每帧 <c>Tick</c> 模拟、把聚合值写进 <see cref="BattleModel"/>（HUD 只读订阅）、把逐事件（刷怪/命中/击杀）
    /// 翻成池化视觉（几何体敌人 + 伤害飘字），终局把结果交给 <see cref="IGameFlow"/> 进结算。
    /// <para>模拟内核对 Unity 一无所知，置换为 ECS 后端时本类的"事件→视觉/Model"翻译层原样保留（接缝价值所在）。
    /// System 层不能 ExecuteCommand（防环），故终局直接 <c>GetUtility&lt;IGameFlow&gt;()</c> 驱动流程。</para>
    /// </summary>
    public sealed class BattleDirector : MonoSystemBase
    {
        [SerializeField, Tooltip("敌人 / 飘字的挂载根（世界空间，XY 平面）。")]
        private Transform _arenaRoot;

        [SerializeField, Tooltip("敌人视觉 prefab（几何体 + Renderer；按原型着色 / 缩放）。")]
        private GameObject _enemyPrefab;

        [SerializeField, Tooltip("伤害飘字 prefab（含 DamageFloater）。")]
        private GameObject _floaterPrefab;

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

        // 敌人实例 id → 视觉，逐帧同步位置；击杀时按 id 回收。
        private readonly Dictionary<int, Transform> _enemyViews = new();
        private readonly List<DamageFloater> _floaters = new();

        // 原型着色（M1 硬编码：突袭者=警戒红、装甲兵=钢青；配置未含颜色字段，加进表是后续项）。
        private static readonly Color FastColor = new(0.90f, 0.35f, 0.30f);
        private static readonly Color TankColor = new(0.45f, 0.62f, 0.72f);
        private static readonly Color HitColor = new(1f, 0.92f, 0.5f);

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
            _sim.WaveCleared += OnWaveCleared;

            _sim.Start(setup);

            _model.PlayerMaxHp.Value = _sim.PlayerMaxHp;
            _model.WaveCount.Value = _sim.WaveCount;
            WriteModel();
            _ready = true;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            AdvanceFloaters();
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
            WriteModel();

            if (_sim.Phase is BattlePhase.Victory or BattlePhase.Defeat)
            {
                _ending = true;
                _victory = _sim.Phase == BattlePhase.Victory;
                _endTimer = _resultDelay;
            }
        }

        // ── 模拟事件 → 视觉 ──────────────────────────────────────────────────

        private void OnEnemySpawned(EnemySpawnedEvent e)
        {
            var go = Bag.Spawn(_enemyPrefab, _arenaRoot);
            StyleEnemy(go, e.ArchetypeId);
            go.transform.localPosition = ToWorld(e.Position);
            _enemyViews[e.EnemyId] = go.transform;
        }

        private void OnEnemyHit(EnemyHitEvent e)
        {
            SpawnFloater(((int)e.Damage).ToString(), HitColor, ToWorld(e.Position));
            if (e.Killed && _enemyViews.TryGetValue(e.EnemyId, out var tr))
            {
                _enemyViews.Remove(e.EnemyId);
                if (tr != null) Bag.Despawn(tr.gameObject);
            }
        }

        private void OnWaveCleared(int wave)
        {
            _betweenWaves = true;
            _waveTimer = _interWaveDelay;
        }

        private void SyncEnemyViews()
        {
            for (int i = 0; i < _sim.EnemyCount; i++)
            {
                var snap = _sim.GetEnemy(i);
                if (_enemyViews.TryGetValue(snap.Id, out var tr) && tr != null)
                    tr.localPosition = ToWorld(snap.Position);
            }
        }

        private void StyleEnemy(GameObject go, int archetypeId)
        {
            bool tank = archetypeId == 2;
            float d = tank ? 1.3f : 0.8f;
            go.transform.localScale = new Vector3(d, d, d);
            var r = go.GetComponentInChildren<Renderer>();
            if (r != null) r.material.color = tank ? TankColor : FastColor;
        }

        private void SpawnFloater(string content, Color color, Vector3 worldPos)
        {
            if (_floaterPrefab == null) return;
            var go = Bag.Spawn(_floaterPrefab, _arenaRoot);
            var f = go.GetComponent<DamageFloater>();
            if (f != null)
            {
                f.Play(content, color, worldPos);
                _floaters.Add(f);
            }
        }

        private void AdvanceFloaters()
        {
            for (int i = _floaters.Count - 1; i >= 0; i--)
            {
                var f = _floaters[i];
                if (f == null) { _floaters.RemoveAt(i); continue; }
                if (f.IsDone)
                {
                    _floaters.RemoveAt(i);
                    Bag.Despawn(f.gameObject);
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

        private static Vector3 ToWorld(System.Numerics.Vector2 p) => new(p.X, p.Y, 0f);

        protected override void OnDestroy()
        {
            _sim?.Dispose();
            _sim = null;
            base.OnDestroy();
        }
    }
}
