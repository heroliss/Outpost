using System;
using System.Collections.Generic;
using System.Numerics;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Game.Outpost.Sim.Ecs
{
    // ── 组件（chunk 内 SoA 连续存储）────────────────────────────────────────

    /// <summary>敌人位置（XY 平面）——移动 job 并行迭代的主体数据。</summary>
    internal struct EnemyPos : IComponentData
    {
        public float2 Value;
    }

    /// <summary>敌人当前生命。</summary>
    internal struct EnemyHp : IComponentData
    {
        public float Value;
    }

    /// <summary>敌人出生即定的只读元数据（实例 id / 原型索引 / 出生波成长系数）。</summary>
    internal struct EnemyMeta : IComponentData
    {
        public int Id;
        public int ArchIndex;
        public float StatScale;
    }

    // ── job 与主线程之间的记录结构（托管委托不能进 Burst，事件以缓冲带回按序重放）──

    /// <summary>移动 job 里抵达接触距离的敌人（主线程按 id 升序重放自爆事件并销毁实体）。</summary>
    internal struct Detonation
    {
        public Entity Entity;
        public int Id;
        public int ArchIndex;
        public float2 Pos;
        public float StatScale;
    }

    /// <summary>开火 job 的一发记录（命中或空放），主线程据此重放 EnemyHit / TurretFired 并写回幸存者血量。</summary>
    internal struct ShotRecord
    {
        public Entity Entity;
        public byte Kind;    // 0 = 命中；1 = 空放（转向途中未对准）
        public byte Killed;
        public int EnemyId;
        public int ArchIndex;
        public float2 Pos;   // 命中：敌人位置；空放：炮口方向在射程边缘的落点
        public float Damage;
        public float Splash;
        public float HpAfter;
    }

    /// <summary>开火 job 读写的玩家侧聚合状态（打包成一个 NativeReference 进出 job）。</summary>
    internal struct PlayerCombatState
    {
        public float Hp;
        public float Cooldown;
        public int Kills;
        public int Score;
    }

    // ── Burst jobs ─────────────────────────────────────────────────────────

    /// <summary>
    /// 移动 + 抵达判定：并行遍历 chunk、原地写位置；抵达接触距离的入队待爆
    /// （伤害结算 / 事件 / 实体销毁回主线程做——结构变更与托管回调都不属于 job）。
    /// FloatMode.Strict：数学须与参考实现的托管 IEEE 运算逐位一致，禁 FMA / 重结合（对拍前提）。
    /// CompileSynchronously：编辑器里 Burst 默认异步编译、首次调度静默回退托管执行——性能度量会被污染，
    /// 强制同步编译换一次微小的进战斗停顿（玩家包全 AOT，无此差别）。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal struct MoveJob : IJobChunk
    {
        public float Dt;
        public float PlayerRadius;
        [ReadOnly] public NativeArray<EnemyArchetype> Archetypes;
        public ComponentTypeHandle<EnemyPos> PosHandle;
        [ReadOnly] public ComponentTypeHandle<EnemyMeta> MetaHandle;
        [ReadOnly] public EntityTypeHandle EntityHandle;
        public NativeQueue<Detonation>.ParallelWriter Detonations;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var pos = chunk.GetNativeArray(ref PosHandle);
            var meta = chunk.GetNativeArray(ref MetaHandle);
            var entities = chunk.GetNativeArray(EntityHandle);
            for (int i = 0; i < chunk.Count; i++)
            {
                var m = meta[i];
                var arch = Archetypes[m.ArchIndex];
                float contact = arch.Radius + PlayerRadius;
                float2 p = pos[i].Value;
                float dist = math.length(p);
                if (dist > contact)
                {
                    // 径直冲向原点，不越过接触距离（dist > contact > 0 保证不除零）——与参考实现逐式一致。
                    float newDist = math.max(contact, dist - arch.MoveSpeed * Dt);
                    pos[i] = new EnemyPos { Value = p * (newDist / dist) };
                }
                else
                {
                    Detonations.Enqueue(new Detonation
                    {
                        Entity = entities[i],
                        Id = m.Id,
                        ArchIndex = m.ArchIndex,
                        Pos = p,
                        StatScale = m.StatScale,
                    });
                }
            }
        }
    }

    /// <summary>射程内最近敌人（快照数组的 Burst 线性扫描；找不到写 -1）。既是开火资格判定，也是炮塔回转的目标。</summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal struct NearestScanJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Pos;
        public int Count;
        public float Range;
        public NativeReference<int> BestIndex;

        public void Execute()
        {
            int best = -1;
            float bestSq = Range * Range;
            for (int i = 0; i < Count; i++)
            {
                float dsq = math.lengthsq(Pos[i]);
                if (dsq <= bestSq)
                {
                    bestSq = dsq;
                    best = i;
                }
            }
            BestIndex.Value = best;
        }
    }

    /// <summary>
    /// 开火循环（单线程 Burst）：整段 shots 循环在 job 内跑完——每发选炮口锥内最近目标、结算伤害与拦截溅射、
    /// 击杀 swap-remove。循环本质是顺序语义（后一发的目标取决于前一发的击杀），并行化只会改变规则，
    /// 提速全靠 Burst 编译 + 快照数组的连续内存。事件记录进 <see cref="Shots"/> 由主线程按序重放。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal struct CombatJob : IJob
    {
        public float Dt;
        public float2 MuzzleDir;
        public float2 BarrelPoint;
        public float Attack;
        public float Range;
        public float AttackInterval;
        public float SplashRadius;
        public float SplashDamageScale;
        public float AimToleranceCosSq;
        public float FirehoseInterval;
        public int MaxShots;
        [ReadOnly] public NativeArray<EnemyArchetype> Archetypes;
        public NativeArray<float2> Pos;
        public NativeArray<float> Hp;
        public NativeArray<EnemyMeta> Meta;
        public NativeArray<Entity> Entities;
        public NativeReference<int> Count;
        public NativeReference<PlayerCombatState> Player;
        public NativeList<ShotRecord> Shots;
        public NativeList<Entity> KilledEntities;

        public void Execute()
        {
            int count = Count.Value;
            var player = Player.Value;
            float rangeSq = Range * Range;

            player.Cooldown -= Dt;
            bool firehose = AttackInterval < FirehoseInterval;
            int shots = 0;
            // 循环内敌人不移动、空放不改战场——锥一旦扫空整个 tick 都空，"射程内尚有敌"也只需查一次（与参考实现同款缓存）。
            bool coneEmpty = false;
            bool anyInRange = true, anyInRangeChecked = false;
            while (player.Cooldown <= 0f && shots < MaxShots)
            {
                int t = coneEmpty ? -1 : FindNearestInCone(count, rangeSq);
                if (t >= 0)
                {
                    float2 p = Pos[t];
                    var m = Meta[t];
                    var arch = Archetypes[m.ArchIndex];
                    Entity ent = Entities[t];
                    float hp = Hp[t] - Attack;
                    bool killed = hp <= 0f;
                    float splash = 0f;
                    if (killed)
                    {
                        // 拦截溅射：离基地过近处击毁，弹头冲击波连带削基地（越近越疼）——与参考实现逐式一致。
                        float dist = math.length(p);
                        if (SplashRadius > 0f && dist < SplashRadius)
                        {
                            float proximity = 1f - dist / SplashRadius;
                            splash = arch.Attack * m.StatScale * SplashDamageScale * proximity;
                            player.Hp = math.max(0f, player.Hp - splash);
                        }
                        player.Kills++;
                        player.Score += arch.Score;
                        KilledEntities.Add(ent);
                        // swap-remove：末位补位，保持工作数组紧凑（索引顺序变化已在接口契约声明）。
                        count--;
                        Pos[t] = Pos[count];
                        Hp[t] = Hp[count];
                        Meta[t] = Meta[count];
                        Entities[t] = Entities[count];
                    }
                    else
                    {
                        Hp[t] = hp;
                    }
                    Shots.Add(new ShotRecord
                    {
                        Entity = ent,
                        Kind = 0,
                        Killed = (byte)(killed ? 1 : 0),
                        EnemyId = m.Id,
                        ArchIndex = m.ArchIndex,
                        Pos = p,
                        Damage = Attack,
                        Splash = splash,
                        HpAfter = hp,
                    });
                }
                else
                {
                    coneEmpty = true;
                    if (!anyInRangeChecked)
                    {
                        anyInRange = FindNearestInRange(count, rangeSq) >= 0;
                        anyInRangeChecked = true;
                    }
                    if (!firehose || !anyInRange) break; // 低射速静默 / 射程内已空：停火
                    Shots.Add(new ShotRecord { Kind = 1, Pos = BarrelPoint });
                }
                player.Cooldown += AttackInterval;
                shots++;
            }
            if (player.Cooldown < 0f) player.Cooldown = 0f;

            Count.Value = count;
            Player.Value = player;
        }

        // 炮口锥内、射程内的最近敌人索引；无则 -1。点积判定与参考实现逐式一致（BattleSimTuning.AimToleranceCosSq 同源）。
        private int FindNearestInCone(int count, float rangeSq)
        {
            int best = -1;
            float bestSq = rangeSq;
            for (int i = 0; i < count; i++)
            {
                float2 p = Pos[i];
                float dsq = math.lengthsq(p);
                if (dsq > bestSq) continue;
                float dot = MuzzleDir.x * p.x + MuzzleDir.y * p.y;
                if (dot <= 0f || dot * dot < AimToleranceCosSq * dsq) continue;
                bestSq = dsq;
                best = i;
            }
            return best;
        }

        private int FindNearestInRange(int count, float rangeSq)
        {
            int best = -1;
            float bestSq = rangeSq;
            for (int i = 0; i < count; i++)
            {
                float dsq = math.lengthsq(Pos[i]);
                if (dsq <= bestSq)
                {
                    bestSq = dsq;
                    best = i;
                }
            }
            return best;
        }
    }

    // ── 后端本体 ───────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IBattleSim"/> 的 DOTS 后端：与 <see cref="ReferenceBattleSim"/> 同一套规则规格，
    /// 存储换 Entities chunk（SoA 连续内存）、热路径换 Burst 编译的 job——移动并行（<see cref="MoveJob"/>），
    /// 开火循环单线程保顺序语义（<see cref="CombatJob"/>）。规则数学与参考实现逐式对齐、判定阈值同源
    /// （<see cref="BattleSimTuning"/>），同 Setup + 同 Tick 序列可与参考实现对拍。
    /// </summary>
    /// <remarks>
    /// <b>驱动形态</b>：自建独立 <see cref="World"/>、不进 player loop、不用 SystemGroup——接缝契约是外部逐帧
    /// <see cref="Tick"/>、事件在调用栈内同步返回，因此所有 job 当帧 Complete，事件以记录缓冲带回主线程按序重放
    /// （托管委托不能进 Burst）。实体只有敌人；玩家是单例状态，留在托管字段、进出 job 打包成 <see cref="PlayerCombatState"/>。<br/>
    /// <b>快照</b>：每 Tick 从 chunk 收集一次工作数组（位置/血量/元数据/实体句柄），开火 job 在其上 swap-remove 后
    /// 即为当帧终态——<see cref="GetEnemy"/> 直接读它（O(1)，表现层逐帧全量遍历友好），
    /// 不 Tick 的阶段（WaveCleared / Defeat / 波间）快照保持有效。chunk 仍是权威存储：血量稀疏写回、击杀批量销毁。<br/>
    /// <b>与参考实现的可观察差异</b>（对拍以聚合值为准，ADR-0030）：同帧多个自爆的事件顺序为实例 id 升序
    /// （参考实现是存活列表倒序索引）——每次扣血独立 <c>max(0,·)</c> 收敛、聚合结果与次序无关，只影响事件流里
    /// HpAfter 的中间值序列；<see cref="GetEnemy"/> 的索引顺序是 chunk 序而非列表序（接口本就声明索引顺序不稳定）。<br/>
    /// <b>三角函数留在托管侧</b>：回转 / 炮口方向用 <see cref="Math"/>（与参考实现同一路径），
    /// 规避 Burst libm 与 .NET 在超越函数上的 ulp 级差异放大成对拍分歧；job 内只有 IEEE 定义严格的加乘除/开方。
    /// </remarks>
    public sealed class EcsBattleSim : IBattleSim
    {
        // 当前波次一条刷怪流的推进状态（托管推进，与参考实现同款）。
        private struct SpawnState
        {
            public int ArchIndex;
            public int Remaining;
            public float Interval;
            public float Timer;
        }

        private const double Rad2Deg = 180.0 / Math.PI;

        private BattleSetup _setup;
        private NativeArray<EnemyArchetype> _archetypes; // 原型表进 NativeArray 供 job 查表（EnemyArchetype 本就是 blittable struct）
        private Dictionary<int, int> _archIndexById;
        private System.Random _rng;

        private World _world;
        private EntityManager _em;
        private EntityArchetype _enemyEntityArchetype;
        private EntityQuery _query;

        private SpawnState[] _spawns = Array.Empty<SpawnState>();
        private readonly List<SpawnState> _streamScratch = new(4);

        private PlayerSetup _player;   // 可变副本：升级修正直接改这份
        private float _playerHp;
        private float _playerAttackCooldown;
        private float _turretAngleDeg;
        private float _waveStatScale = 1f;
        private int _nextEnemyId = 1;

        // 快照工作数组（Persistent，容量随波次增长复用）：语义见类型注释「快照」段。
        private NativeList<float2> _snapPos;
        private NativeList<float> _snapHp;
        private NativeList<EnemyMeta> _snapMeta;
        private NativeList<Entity> _snapEntities;
        private int _aliveCount;

        private NativeQueue<Detonation> _detonations;
        private readonly List<Detonation> _detScratch = new();
        private NativeList<ShotRecord> _shots;
        private NativeList<Entity> _killedEntities;
        private NativeReference<int> _countRef;
        private NativeReference<PlayerCombatState> _playerRef;
        private NativeReference<int> _bestRef;
        private bool _disposed;

        public BattlePhase Phase { get; private set; } = BattlePhase.Idle;
        public int WaveIndex { get; private set; }
        public float PlayerHp => _playerHp;
        public float PlayerMaxHp => _player.MaxHp;
        public float PlayerRange => _player.Range;
        public float PlayerAttack => _player.Attack;
        public float PlayerAttackInterval => _player.AttackInterval;
        public float PlayerRegen => _player.RegenPerSecond;
        public float PlayerRotationSpeed => _player.RotationSpeed;
        public float TurretAngle => _turretAngleDeg;
        public int Kills { get; private set; }
        public int Score { get; private set; }
        public int EnemyCount => _aliveCount;

        public event Action<EnemySpawnedEvent> EnemySpawned;
        public event Action<EnemyHitEvent> EnemyHit;
        public event Action<TurretFiredEvent> TurretFired;
        public event Action<EnemyDetonatedEvent> EnemyDetonated;
        public event Action<int> WaveStarted;
        public event Action<int> WaveCleared;

        public EnemySnapshot GetEnemy(int index)
        {
            var m = _snapMeta[index];
            var p = _snapPos[index];
            var arch = _archetypes[m.ArchIndex];
            return new EnemySnapshot(m.Id, arch.Id, new Vector2(p.x, p.y), _snapHp[index], arch.MaxHp * m.StatScale);
        }

        public void Start(BattleSetup setup)
        {
            if (Phase != BattlePhase.Idle)
                throw new InvalidOperationException("[EcsBattleSim] Start 只能调一次——重开一局请 new 新实例（与 FlowState 同心智）。");
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (setup.Enemies == null || setup.Enemies.Length == 0)
                throw new ArgumentException("[EcsBattleSim] Setup 缺少敌人原型。", nameof(setup));

            _setup = setup;
            _archetypes = new NativeArray<EnemyArchetype>(setup.Enemies, Allocator.Persistent);
            _archIndexById = new Dictionary<int, int>(setup.Enemies.Length);
            for (int i = 0; i < setup.Enemies.Length; i++)
                _archIndexById[setup.Enemies[i].Id] = i;

            _rng = new System.Random(setup.Seed);
            _player = setup.Player;
            _playerHp = _player.MaxHp;
            _playerAttackCooldown = 0f;
            _turretAngleDeg = 0f;

            _world = new World("OutpostBattle (ECS)");
            _em = _world.EntityManager;
            _enemyEntityArchetype = _em.CreateArchetype(typeof(EnemyPos), typeof(EnemyHp), typeof(EnemyMeta));
            _query = _em.CreateEntityQuery(typeof(EnemyPos), typeof(EnemyHp), typeof(EnemyMeta));

            _snapPos = new NativeList<float2>(1024, Allocator.Persistent);
            _snapHp = new NativeList<float>(1024, Allocator.Persistent);
            _snapMeta = new NativeList<EnemyMeta>(1024, Allocator.Persistent);
            _snapEntities = new NativeList<Entity>(1024, Allocator.Persistent);
            _detonations = new NativeQueue<Detonation>(Allocator.Persistent);
            _shots = new NativeList<ShotRecord>(256, Allocator.Persistent);
            _killedEntities = new NativeList<Entity>(256, Allocator.Persistent);
            _countRef = new NativeReference<int>(Allocator.Persistent);
            _playerRef = new NativeReference<PlayerCombatState>(Allocator.Persistent);
            _bestRef = new NativeReference<int>(Allocator.Persistent);

            BeginWave(1);
        }

        public void BeginNextWave()
        {
            if (Phase != BattlePhase.WaveCleared)
                throw new InvalidOperationException($"[EcsBattleSim] BeginNextWave 仅在 WaveCleared 阶段可调（当前 {Phase}）。");
            BeginWave(WaveIndex + 1);
        }

        public void ApplyModifier(in PlayerModifier modifier)
        {
            if (Phase == BattlePhase.Defeat || _setup == null) return;
            // 与参考实现逐行一致（全部成长封顶，见 PlayerSetup 各 Max* 字段）。
            _player.Attack += modifier.AttackAdd;
            if (_player.MaxAttack > 0f && _player.Attack > _player.MaxAttack)
                _player.Attack = _player.MaxAttack;
            float minInterval = _player.MinAttackInterval > 0f ? _player.MinAttackInterval : 0.0008f;
            _player.AttackInterval = Math.Max(minInterval, _player.AttackInterval * modifier.AttackIntervalScale);
            _player.Range += modifier.RangeAdd;
            if (_player.MaxRange > 0f && _player.Range > _player.MaxRange)
                _player.Range = _player.MaxRange;
            _player.RegenPerSecond += modifier.RegenAdd;
            if (_player.MaxRegen > 0f && _player.RegenPerSecond > _player.MaxRegen)
                _player.RegenPerSecond = _player.MaxRegen;
            _player.RotationSpeed += modifier.RotationSpeedAdd;
            if (_player.MaxRotationSpeed > 0f && _player.RotationSpeed > _player.MaxRotationSpeed)
                _player.RotationSpeed = _player.MaxRotationSpeed;
            if (modifier.MaxHpAdd != 0f)
            {
                float newMax = _player.MaxHp + modifier.MaxHpAdd;
                if (_player.MaxHpCap > 0f && newMax > _player.MaxHpCap) newMax = _player.MaxHpCap;
                float gained = newMax - _player.MaxHp;
                _player.MaxHp = newMax;
                _playerHp = Math.Min(_player.MaxHp, _playerHp + Math.Max(0f, gained));
            }
        }

        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f || Phase != BattlePhase.WaveActive) return;

            TickSpawns(deltaTime);
            MoveAndDetonate(deltaTime);
            if (CheckDefeat())
            {
                GatherSnapshot(); // 终局帧也要刷新快照：表现层还要画残余敌人海
                return;
            }
            TickPlayer(deltaTime);
            if (CheckDefeat()) return; // 溅射致死：快照已在 TickPlayer 内更新为当帧终态
            CheckWaveEnd();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 清空事件防止终局后订阅方被迟到回调（与参考实现同约定）。
            EnemySpawned = null;
            EnemyHit = null;
            TurretFired = null;
            EnemyDetonated = null;
            WaveStarted = null;
            WaveCleared = null;

            // 所有 job 都在 Tick 内当帧 Complete，此处无在途依赖；容器可能因 Start 前被 Dispose 而未创建，逐一守卫。
            if (_archetypes.IsCreated) _archetypes.Dispose();
            if (_snapPos.IsCreated) _snapPos.Dispose();
            if (_snapHp.IsCreated) _snapHp.Dispose();
            if (_snapMeta.IsCreated) _snapMeta.Dispose();
            if (_snapEntities.IsCreated) _snapEntities.Dispose();
            if (_detonations.IsCreated) _detonations.Dispose();
            if (_shots.IsCreated) _shots.Dispose();
            if (_killedEntities.IsCreated) _killedEntities.Dispose();
            if (_countRef.IsCreated) _countRef.Dispose();
            if (_playerRef.IsCreated) _playerRef.Dispose();
            if (_bestRef.IsCreated) _bestRef.Dispose();
            if (_world != null && _world.IsCreated) _world.Dispose();
            _world = null;
        }

        // ── 波次与刷怪（托管，公式与参考实现逐行一致）───────────────────────

        private void BeginWave(int waveIndex)
        {
            WaveIndex = waveIndex;
            var sc = _setup.Scaling;
            _waveStatScale = (float)Math.Pow(Math.Max(1f, sc.StatGrowth), waveIndex - 1);
            if (sc.MaxStatScale > 0f && _waveStatScale > sc.MaxStatScale)
                _waveStatScale = sc.MaxStatScale;

            _streamScratch.Clear();
            var roles = sc.Roles;
            if (roles != null)
            {
                for (int r = 0; r < roles.Length; r++)
                {
                    var role = roles[r];
                    if (waveIndex < role.UnlockWave) continue;
                    int step = waveIndex - role.UnlockWave;
                    double growth = role.CountGrowth > 1f ? Math.Pow(role.CountGrowth, step) : 1.0;
                    double rawCount = Math.Floor(role.BaseCount * growth) + Math.Floor(step * role.PerWave);
                    if (role.MaxCount > 0 && rawCount > role.MaxCount) rawCount = role.MaxCount;
                    int count = rawCount >= int.MaxValue ? int.MaxValue : (int)rawCount;
                    float interval = (float)(role.Interval0 / growth) - step * role.IntervalDecay;
                    if (interval < role.IntervalMin) interval = role.IntervalMin;
                    AddStream(role.EnemyId, count, interval);
                }
            }

            _spawns = _streamScratch.ToArray();
            Phase = BattlePhase.WaveActive;
            WaveStarted?.Invoke(waveIndex);
        }

        private void AddStream(int archId, int count, float interval)
        {
            if (count <= 0) return;
            if (!_archIndexById.TryGetValue(archId, out var archIndex)) return;
            _streamScratch.Add(new SpawnState
            {
                ArchIndex = archIndex,
                Remaining = count,
                Interval = Math.Max(0.001f, interval),
                Timer = 0f,
            });
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
            // 出生角是唯一随机源，走托管 System.Random + System.Math——与参考实现共享同一 RNG 语义与三角实现（对拍前提）。
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            var pos = new float2(
                (float)Math.Cos(angle) * _setup.ArenaRadius,
                (float)Math.Sin(angle) * _setup.ArenaRadius);
            var arch = _archetypes[archIndex];
            int id = _nextEnemyId++;
            var e = _em.CreateEntity(_enemyEntityArchetype);
            _em.SetComponentData(e, new EnemyPos { Value = pos });
            _em.SetComponentData(e, new EnemyHp { Value = arch.MaxHp * _waveStatScale });
            _em.SetComponentData(e, new EnemyMeta { Id = id, ArchIndex = archIndex, StatScale = _waveStatScale });
            EnemySpawned?.Invoke(new EnemySpawnedEvent(id, arch.Id, new Vector2(pos.x, pos.y)));
        }

        // ── 每帧演算 ───────────────────────────────────────────────────────

        private void MoveAndDetonate(float dt)
        {
            var job = new MoveJob
            {
                Dt = dt,
                PlayerRadius = _player.Radius,
                Archetypes = _archetypes,
                PosHandle = _em.GetComponentTypeHandle<EnemyPos>(false),
                MetaHandle = _em.GetComponentTypeHandle<EnemyMeta>(true),
                EntityHandle = _em.GetEntityTypeHandle(),
                Detonations = _detonations.AsParallelWriter(),
            };
            job.ScheduleParallel(_query, default).Complete();

            if (_detonations.IsEmpty()) return;
            _detScratch.Clear();
            while (_detonations.TryDequeue(out var d)) _detScratch.Add(d);
            // 并行入队次序不稳定，按实例 id 升序重放保证事件流可复现；每次扣血独立 max(0,·) 收敛，聚合结果与次序无关。
            _detScratch.Sort(static (a, b) => a.Id.CompareTo(b.Id));

            var toDestroy = new NativeArray<Entity>(_detScratch.Count, Allocator.Temp);
            for (int i = 0; i < _detScratch.Count; i++)
            {
                var d = _detScratch[i];
                var arch = _archetypes[d.ArchIndex];
                float dmg = arch.Attack * d.StatScale;
                _playerHp = Math.Max(0f, _playerHp - dmg);
                toDestroy[i] = d.Entity;
                EnemyDetonated?.Invoke(new EnemyDetonatedEvent(d.Id, arch.Id, new Vector2(d.Pos.x, d.Pos.y), dmg, _playerHp));
            }
            _em.DestroyEntity(toDestroy);
            toDestroy.Dispose();
        }

        private void TickPlayer(float dt)
        {
            if (_player.RegenPerSecond > 0f)
                _playerHp = Math.Min(_player.MaxHp, _playerHp + _player.RegenPerSecond * dt);

            GatherSnapshot();

            var scan = new NearestScanJob
            {
                Pos = _snapPos.AsArray(),
                Count = _aliveCount,
                Range = _player.Range,
                BestIndex = _bestRef,
            };
            scan.Schedule().Complete();
            int target = _bestRef.Value;
            if (target < 0)
            {
                // 射程内无目标：冷却不往负累（不积欠账），朝向保持不动。
                if (_playerAttackCooldown < 0f) _playerAttackCooldown = 0f;
                return;
            }

            // 回转与炮口方向的三角函数留在托管侧（理由见类型注释）；本 tick 内朝向不变，炮口落点也随之恒定，预算好传进 job。
            var tpos = _snapPos[target];
            float desired = (float)(Math.Atan2(tpos.y, tpos.x) * Rad2Deg);
            _turretAngleDeg = SimMath.MoveTowardsAngleDeg(_turretAngleDeg, desired, _player.RotationSpeed * dt);

            double muzzleRad = _turretAngleDeg / Rad2Deg;
            var muzzleDir = new float2((float)Math.Cos(muzzleRad), (float)Math.Sin(muzzleRad));
            var barrelPoint = new float2(muzzleDir.x * _player.Range, muzzleDir.y * _player.Range);

            _shots.Clear();
            _killedEntities.Clear();
            _countRef.Value = _aliveCount;
            _playerRef.Value = new PlayerCombatState
            {
                Hp = _playerHp,
                Cooldown = _playerAttackCooldown,
                Kills = Kills,
                Score = Score,
            };

            var combat = new CombatJob
            {
                Dt = dt,
                MuzzleDir = muzzleDir,
                BarrelPoint = barrelPoint,
                Attack = _player.Attack,
                Range = _player.Range,
                AttackInterval = _player.AttackInterval,
                SplashRadius = _player.SplashRadius,
                SplashDamageScale = _player.SplashDamageScale,
                AimToleranceCosSq = BattleSimTuning.AimToleranceCosSq,
                FirehoseInterval = BattleSimTuning.FirehoseFireInterval,
                MaxShots = BattleSimTuning.MaxShotsPerTick,
                Archetypes = _archetypes,
                Pos = _snapPos.AsArray(),
                Hp = _snapHp.AsArray(),
                Meta = _snapMeta.AsArray(),
                Entities = _snapEntities.AsArray(),
                Count = _countRef,
                Player = _playerRef,
                Shots = _shots,
                KilledEntities = _killedEntities,
            };
            combat.Schedule().Complete();

            _aliveCount = _countRef.Value;
            var st = _playerRef.Value;
            _playerHp = st.Hp;
            _playerAttackCooldown = st.Cooldown;
            Kills = st.Kills;
            Score = st.Score;

            // 结构写回：击杀批量销毁（chunk 仍是权威存储，快照数组只是本帧工作/只读视图）。
            if (_killedEntities.Length > 0)
                _em.DestroyEntity(_killedEntities.AsArray());

            // 事件按 job 记录顺序重放（每发 EnemyHit 先于 TurretFired，与参考实现一致）；
            // 幸存者血量稀疏写回 chunk——同一敌人同帧多次中弹按记录顺序覆盖、后死于后续发的用 Exists 挡掉（实体已销毁）。
            for (int i = 0; i < _shots.Length; i++)
            {
                var s = _shots[i];
                if (s.Kind == 0)
                {
                    if (s.Killed == 0 && _em.Exists(s.Entity))
                        _em.SetComponentData(s.Entity, new EnemyHp { Value = s.HpAfter });
                    var arch = _archetypes[s.ArchIndex];
                    var pos = new Vector2(s.Pos.x, s.Pos.y);
                    EnemyHit?.Invoke(new EnemyHitEvent(s.EnemyId, arch.Id, pos, s.Damage, s.Killed != 0, s.Splash));
                    TurretFired?.Invoke(new TurretFiredEvent(pos, true));
                }
                else
                {
                    TurretFired?.Invoke(new TurretFiredEvent(new Vector2(s.Pos.x, s.Pos.y), false));
                }
            }
        }

        private bool CheckDefeat()
        {
            if (_playerHp > 0f) return false;
            _playerHp = 0f;
            Phase = BattlePhase.Defeat;
            return true;
        }

        private void CheckWaveEnd()
        {
            if (_aliveCount > 0) return;
            for (int i = 0; i < _spawns.Length; i++)
                if (_spawns[i].Remaining > 0) return;

            // 波间维修（语义见参考实现 CheckWaveEnd 注释）。
            _playerHp = _player.MaxHp;
            Phase = BattlePhase.WaveCleared;
            WaveCleared?.Invoke(WaveIndex);
        }

        // 从 chunk 收集本帧快照工作数组。ToComponentDataArray 是按 chunk 的批量拷贝；
        // 组件类型与快照元素同尺寸，Reinterpret 零拷贝改视图后整块复制进持久列表。
        private void GatherSnapshot()
        {
            var pos = _query.ToComponentDataArray<EnemyPos>(Allocator.Temp);
            var hp = _query.ToComponentDataArray<EnemyHp>(Allocator.Temp);
            var meta = _query.ToComponentDataArray<EnemyMeta>(Allocator.Temp);
            var ents = _query.ToEntityArray(Allocator.Temp);
            int n = pos.Length;

            _snapPos.ResizeUninitialized(n);
            _snapHp.ResizeUninitialized(n);
            _snapMeta.ResizeUninitialized(n);
            _snapEntities.ResizeUninitialized(n);
            if (n > 0)
            {
                NativeArray<float2>.Copy(pos.Reinterpret<float2>(), _snapPos.AsArray(), n);
                NativeArray<float>.Copy(hp.Reinterpret<float>(), _snapHp.AsArray(), n);
                NativeArray<EnemyMeta>.Copy(meta, _snapMeta.AsArray(), n);
                NativeArray<Entity>.Copy(ents, _snapEntities.AsArray(), n);
            }
            _aliveCount = n;

            pos.Dispose();
            hp.Dispose();
            meta.Dispose();
            ents.Dispose();
        }
    }
}
