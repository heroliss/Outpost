using System;
using System.Collections.Generic;
using System.Numerics;

namespace Game.Outpost.Sim
{
    /// <summary>
    /// <see cref="IBattleSim"/> 的参考实现：面向对象的直白写法（列表 + 结构体逐帧演算），
    /// 规模目标是切片的"几十只"量级。作为接缝的第一后端，它同时是规则的可执行规格——
    /// 纯 C# 可直接单测；后续 ECS 后端（压力波次）与它同题对比。
    /// </summary>
    /// <remarks>
    /// 确定性：唯一随机源是出生角度（<see cref="BattleSetup.Seed"/> 种子化的 <see cref="Random"/>），
    /// 敌人列表按索引顺序演算、死亡 swap-remove——同 Setup + 同 Tick 序列在同一平台上结果完全一致。
    /// </remarks>
    public sealed class ReferenceBattleSim : IBattleSim
    {
        // 存活敌人的运行时状态（原型静态属性经 ArchIndex 查 _archetypes，不冗余进实例）。
        private struct EnemyState
        {
            public int Id;
            public int ArchIndex;
            public Vector2 Pos;
            public float Hp;
            public float AttackCooldown;
        }

        // 当前波次一条刷怪流的推进状态。Timer 初始为 0：首只在下一次 Tick 立刻刷出。
        private struct SpawnState
        {
            public int ArchIndex;
            public int Remaining;
            public float Interval;
            public float Timer;
        }

        private BattleSetup _setup;
        private EnemyArchetype[] _archetypes;
        private Dictionary<int, int> _archIndexById;
        private Random _rng;

        private readonly List<EnemyState> _enemies = new();
        private SpawnState[] _spawns = Array.Empty<SpawnState>();

        private PlayerSetup _player;   // 可变副本：升级修正直接改这份
        private float _playerHp;
        private float _playerAttackCooldown;
        private int _nextEnemyId = 1;

        public BattlePhase Phase { get; private set; } = BattlePhase.Idle;
        public int WaveIndex { get; private set; }
        public int WaveCount => _setup?.Waves.Length ?? 0;
        public float PlayerHp => _playerHp;
        public float PlayerMaxHp => _player.MaxHp;
        public int Kills { get; private set; }
        public int Score { get; private set; }
        public int EnemyCount => _enemies.Count;

        public event Action<EnemySpawnedEvent> EnemySpawned;
        public event Action<EnemyHitEvent> EnemyHit;
        public event Action<PlayerHitEvent> PlayerHit;
        public event Action<int> WaveStarted;
        public event Action<int> WaveCleared;

        public EnemySnapshot GetEnemy(int index)
        {
            var e = _enemies[index];
            return new EnemySnapshot(e.Id, _archetypes[e.ArchIndex].Id, e.Pos, e.Hp, _archetypes[e.ArchIndex].MaxHp);
        }

        public void Start(BattleSetup setup)
        {
            if (Phase != BattlePhase.Idle)
                throw new InvalidOperationException("[ReferenceBattleSim] Start 只能调一次——重开一局请 new 新实例（与 FlowState 同心智）。");
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (setup.Enemies == null || setup.Enemies.Length == 0)
                throw new ArgumentException("[ReferenceBattleSim] Setup 缺少敌人原型。", nameof(setup));
            if (setup.Waves == null || setup.Waves.Length == 0)
                throw new ArgumentException("[ReferenceBattleSim] Setup 缺少波次。", nameof(setup));

            _setup = setup;
            _archetypes = setup.Enemies;
            _archIndexById = new Dictionary<int, int>(_archetypes.Length);
            for (int i = 0; i < _archetypes.Length; i++)
                _archIndexById[_archetypes[i].Id] = i;

            _rng = new Random(setup.Seed);
            _player = setup.Player;
            _playerHp = _player.MaxHp;
            _playerAttackCooldown = 0f;

            BeginWave(1);
        }

        public void BeginNextWave()
        {
            if (Phase != BattlePhase.WaveCleared)
                throw new InvalidOperationException($"[ReferenceBattleSim] BeginNextWave 仅在 WaveCleared 阶段可调（当前 {Phase}）。");
            BeginWave(WaveIndex + 1);
        }

        public void ApplyModifier(in PlayerModifier modifier)
        {
            if (Phase is BattlePhase.Victory or BattlePhase.Defeat || _setup == null) return;
            _player.Attack += modifier.AttackAdd;
            _player.AttackInterval = Math.Max(0.05f, _player.AttackInterval * modifier.AttackIntervalScale);
            _player.Range += modifier.RangeAdd;
            _player.RegenPerSecond += modifier.RegenAdd;
            if (modifier.MaxHpAdd != 0f)
            {
                _player.MaxHp += modifier.MaxHpAdd;
                _playerHp = Math.Min(_player.MaxHp, _playerHp + modifier.MaxHpAdd);
            }
        }

        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f || Phase != BattlePhase.WaveActive) return;

            TickSpawns(deltaTime);
            TickEnemies(deltaTime);
            if (_playerHp <= 0f)
            {
                _playerHp = 0f;
                Phase = BattlePhase.Defeat;
                return;
            }
            TickPlayer(deltaTime);
            CheckWaveEnd();
        }

        public void Dispose()
        {
            // 参考实现无非托管资源；清空事件防止终局后订阅方被迟到回调（接口为 ECS 后端的 World 释放而设）。
            EnemySpawned = null;
            EnemyHit = null;
            PlayerHit = null;
            WaveStarted = null;
            WaveCleared = null;
        }

        private void BeginWave(int waveIndex)
        {
            WaveIndex = waveIndex;
            var wave = _setup.Waves[waveIndex - 1];
            int count = wave?.Spawns?.Length ?? 0;
            _spawns = new SpawnState[count];
            for (int i = 0; i < count; i++)
            {
                var entry = wave.Spawns[i];
                if (!_archIndexById.TryGetValue(entry.ArchetypeId, out var archIndex))
                    throw new ArgumentException($"[ReferenceBattleSim] 第 {waveIndex} 波引用了不存在的敌人原型 id={entry.ArchetypeId}。");
                _spawns[i] = new SpawnState
                {
                    ArchIndex = archIndex,
                    Remaining = entry.Count,
                    Interval = Math.Max(0.01f, entry.Interval),
                    Timer = 0f, // 首只立刻刷
                };
            }
            Phase = BattlePhase.WaveActive;
            WaveStarted?.Invoke(waveIndex);
        }

        private void TickSpawns(float dt)
        {
            for (int i = 0; i < _spawns.Length; i++)
            {
                ref var s = ref _spawns[i];
                if (s.Remaining <= 0) continue;
                s.Timer -= dt;
                while (s.Timer <= 0f && s.Remaining > 0)
                {
                    SpawnEnemy(s.ArchIndex);
                    s.Remaining--;
                    s.Timer += s.Interval;
                }
            }
        }

        private void SpawnEnemy(int archIndex)
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            var pos = new Vector2(
                (float)Math.Cos(angle) * _setup.ArenaRadius,
                (float)Math.Sin(angle) * _setup.ArenaRadius);
            var e = new EnemyState
            {
                Id = _nextEnemyId++,
                ArchIndex = archIndex,
                Pos = pos,
                Hp = _archetypes[archIndex].MaxHp,
                AttackCooldown = _archetypes[archIndex].AttackInterval, // 抵近后先蓄力一拍再打，避免"贴脸即秒"
            };
            _enemies.Add(e);
            EnemySpawned?.Invoke(new EnemySpawnedEvent(e.Id, _archetypes[archIndex].Id, pos));
        }

        private void TickEnemies(float dt)
        {
            for (int i = 0; i < _enemies.Count; i++)
            {
                var e = _enemies[i];
                var arch = _archetypes[e.ArchIndex];
                float contact = arch.Radius + _player.Radius;
                float dist = e.Pos.Length();

                if (dist > contact)
                {
                    // 径直冲向原点，不越过接触距离（超小 dist 由 contact > 0 保证不除零）。
                    float newDist = Math.Max(contact, dist - arch.MoveSpeed * dt);
                    e.Pos *= newDist / dist;
                }
                else
                {
                    e.AttackCooldown -= dt;
                    while (e.AttackCooldown <= 0f && _playerHp > 0f)
                    {
                        _playerHp = Math.Max(0f, _playerHp - arch.Attack);
                        PlayerHit?.Invoke(new PlayerHitEvent(arch.Attack, _playerHp));
                        e.AttackCooldown += arch.AttackInterval;
                    }
                }
                _enemies[i] = e;
            }
        }

        private void TickPlayer(float dt)
        {
            if (_player.RegenPerSecond > 0f)
                _playerHp = Math.Min(_player.MaxHp, _playerHp + _player.RegenPerSecond * dt);

            _playerAttackCooldown -= dt;
            while (_playerAttackCooldown <= 0f)
            {
                int target = FindNearestInRange();
                if (target < 0)
                {
                    // 射程内无目标：冷却归零挂起（不积攒欠账），目标一进射程立刻开火。
                    _playerAttackCooldown = 0f;
                    return;
                }
                DamageEnemy(target, _player.Attack);
                _playerAttackCooldown += _player.AttackInterval;
            }
        }

        private int FindNearestInRange()
        {
            int best = -1;
            float bestSq = _player.Range * _player.Range;
            for (int i = 0; i < _enemies.Count; i++)
            {
                float dsq = _enemies[i].Pos.LengthSquared();
                if (dsq <= bestSq)
                {
                    bestSq = dsq;
                    best = i;
                }
            }
            return best;
        }

        private void DamageEnemy(int index, float damage)
        {
            var e = _enemies[index];
            e.Hp -= damage;
            var arch = _archetypes[e.ArchIndex];
            bool killed = e.Hp <= 0f;
            if (killed)
            {
                // swap-remove：末位补位，保持 List 紧凑（索引顺序变化已在接口契约声明）。
                _enemies[index] = _enemies[^1];
                _enemies.RemoveAt(_enemies.Count - 1);
                Kills++;
                Score += arch.Score;
            }
            else
            {
                _enemies[index] = e;
            }
            EnemyHit?.Invoke(new EnemyHitEvent(e.Id, arch.Id, e.Pos, damage, killed));
        }

        private void CheckWaveEnd()
        {
            if (_enemies.Count > 0) return;
            for (int i = 0; i < _spawns.Length; i++)
                if (_spawns[i].Remaining > 0) return;

            if (WaveIndex >= WaveCount)
            {
                Phase = BattlePhase.Victory;
            }
            else
            {
                Phase = BattlePhase.WaveCleared;
                WaveCleared?.Invoke(WaveIndex);
            }
        }
    }
}
