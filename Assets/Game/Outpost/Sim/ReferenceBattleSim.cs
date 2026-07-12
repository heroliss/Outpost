using System;
using System.Collections.Generic;
using System.Numerics;

namespace Game.Outpost.Sim
{
    /// <summary>
    /// <see cref="IBattleSim"/> 的参考实现：面向对象的直白写法——列表 + 结构体逐帧演算，索敌 O(N) 线性扫描、
    /// 弹丸碰撞 O(P×N) 逐弹扫掠全敌，<b>刻意不做空间分区</b>。这份"直白"是与 ECS 后端同题对比的基线
    /// （后期在飞弹 × 敌人海把它推向帧预算正是演示本体，ADR-0031）；作为接缝的第一后端，
    /// 它同时是规则的可执行规格，纯 C# 可直接单测 / 无头跑数。
    /// </summary>
    /// <remarks>
    /// 确定性：随机源仅出生角度（<see cref="BattleSetup.Seed"/> 种子化的 <see cref="Random"/>——
    /// 击发不再消耗 RNG，故 RNG 序列 = 刷怪序列，两后端一致）；敌人列表按索引顺序演算、死亡 swap-remove；
    /// 同帧多个自爆按<b>实例 id 升序</b>结算（事件流与泥地记账顺序是两后端共同契约）；
    /// 炮塔朝向按固定回转速度逐帧趋近目标——同 Setup + 同 Tick 序列在同一平台上结果完全一致。
    /// 无限模式：波次由 <see cref="WaveScaling"/> 逐波生成，唯一终态是哨站被摧毁（<see cref="BattlePhase.Defeat"/>）。
    /// </remarks>
    public sealed class ReferenceBattleSim : IBattleSim
    {
        // 存活敌人的运行时状态（原型静态属性经 ArchIndex 查 _archetypes，不冗余进实例；StatScale 是该敌人出生波的成长系数）。
        private struct EnemyState
        {
            public int Id;
            public int ArchIndex;
            public Vector2 Pos;
            public float Hp;
            public float StatScale;
        }

        // 当前波次一条刷怪流的推进状态。Timer 初始为 0：首只在下一次 Tick 立刻刷出。
        private struct SpawnState
        {
            public int ArchIndex;
            public int Remaining;
            public float Interval;
            public float Timer;
        }

        // 在飞弹丸：从原点沿 Dir 定速直飞；Damage 是击发瞬间的攻击力快照（升级在弹丸在飞期间生效不追溯）。
        private struct ProjectileState
        {
            public Vector2 Pos;
            public Vector2 Dir;
            public float Damage;
        }

        // 判定阈值（对准容差 / 单帧发数 / 火墙门槛与散布）是两后端共享的规格常量，见 BattleSimTuning。
        private const double Rad2Deg = 180.0 / Math.PI;

        private BattleSetup _setup;
        private EnemyArchetype[] _archetypes;
        private Dictionary<int, int> _archIndexById;
        private Random _rng;

        private readonly List<EnemyState> _enemies = new();
        private readonly List<ProjectileState> _projectiles = new();
        private SpawnState[] _spawns = Array.Empty<SpawnState>();
        private readonly List<SpawnState> _streamScratch = new(3); // BeginWave 组流的复用缓冲，免每波分配
        private readonly List<int> _detonatingIdx = new();         // 本 tick 抵达自爆的敌人索引（收集后按 id 序结算）
        private Comparison<int> _detonatorIdCompare;               // 缓存比较器，免逐 tick 闭包分配

        private PlayerSetup _player;   // 可变副本：升级修正直接改这份
        private float _playerHp;
        private float _playerAttackCooldown;
        private float _turretAngleDeg;      // 炮塔当前朝向（度）；逐帧按回转速度趋近最近目标
        private float _waveStatScale = 1f;  // 当前波次的敌人成长系数（StatGrowth^(w-1)），出生时写进 EnemyState
        private int _nextEnemyId = 1;

        // 残骸减速泥地 + 推挤（规则见 WreckFieldSetup）：密度格计数 + 逐实体 SoA 槽位（环形复写挤掉最老的）。
        private int[] _wreckCells;
        private Vector2[] _wreckPos;
        private int[] _wreckArch;     // 原型索引（推挤接触半径 / 快照原型 id）
        private float[] _wreckDrift;  // 累计漂移（≥ PushMaxDrift 后不再被推）
        private int[] _wreckSeq;      // 创建序号（表现层镜像的换血检测信号）
        private int[] _wreckCell;     // 当前所在密度格（记账跟随位置）
        private int _wreckSlotCount;
        private int _wreckNext;       // 环形写入游标
        private int _nextWreckSeq = 1;
        private int _wreckGridDim;
        private float _wreckGridHalf;

        // 敌人占位网格（推挤查询用，每 tick 重建；CSR 三段式：计数 → 前缀和 → 填充）。
        // 格边长按"最大接触距离"（敌半径 + 残骸半径×WreckBodyScale 的全原型上界）推导，3×3 邻域必覆盖一切接触对。
        private float _enemyGridCellSize;
        private float _enemyGridHalf;
        private int _enemyGridDim;
        private int[] _enemyCellCount;
        private int[] _enemyCellStart;  // 长度 cells+1（前缀和）
        private int[] _enemyCellItems;  // 按格分段的敌人索引（容量随敌人数增长）

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
        public int EnemyCount => _enemies.Count;
        public int ProjectileCount => _projectiles.Count;

        public event Action<EnemySpawnedEvent> EnemySpawned;
        public event Action<EnemyHitEvent> EnemyHit;
        public event Action<TurretFiredEvent> TurretFired;
        public event Action<EnemyDetonatedEvent> EnemyDetonated;
        public event Action<int> WaveStarted;
        public event Action<int> WaveCleared;

        public EnemySnapshot GetEnemy(int index)
        {
            var e = _enemies[index];
            var arch = _archetypes[e.ArchIndex];
            return new EnemySnapshot(e.Id, arch.Id, e.Pos, e.Hp, arch.MaxHp * e.StatScale);
        }

        public ProjectileSnapshot GetProjectile(int index)
        {
            var p = _projectiles[index];
            return new ProjectileSnapshot(p.Pos, p.Dir);
        }

        public WreckGridInfo WreckGrid
            => _wreckCells == null ? default : new WreckGridInfo(_wreckGridDim, _setup.WreckField.CellSize, _wreckGridHalf);

        public int GetWreckCellCount(int index) => _wreckCells[index];

        public int WreckSlotCount => _wreckSlotCount;

        public WreckSnapshot GetWreckSlot(int slot)
            => new(_wreckSeq[slot], _archetypes[_wreckArch[slot]].Id, _wreckPos[slot]);

        public void Start(BattleSetup setup)
        {
            if (Phase != BattlePhase.Idle)
                throw new InvalidOperationException("[ReferenceBattleSim] Start 只能调一次——重开一局请 new 新实例（与 FlowState 同心智）。");
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (setup.Enemies == null || setup.Enemies.Length == 0)
                throw new ArgumentException("[ReferenceBattleSim] Setup 缺少敌人原型。", nameof(setup));

            _setup = setup;
            _archetypes = setup.Enemies;
            _archIndexById = new Dictionary<int, int>(_archetypes.Length);
            for (int i = 0; i < _archetypes.Length; i++)
                _archIndexById[_archetypes[i].Id] = i;

            _rng = new Random(setup.Seed);
            _player = setup.Player;
            _playerHp = _player.MaxHp;
            _playerAttackCooldown = 0f;
            _turretAngleDeg = 0f;
            _detonatorIdCompare = (a, b) => _enemies[a].Id.CompareTo(_enemies[b].Id);

            var wf = setup.WreckField;
            if (wf.SimCap > 0)
            {
                _wreckGridHalf = setup.ArenaRadius + 1f;
                _wreckGridDim = Math.Max(1, (int)Math.Ceiling(_wreckGridHalf * 2f / wf.CellSize));
                _wreckCells = new int[_wreckGridDim * _wreckGridDim];
                _wreckPos = new Vector2[wf.SimCap];
                _wreckArch = new int[wf.SimCap];
                _wreckDrift = new float[wf.SimCap];
                _wreckSeq = new int[wf.SimCap];
                _wreckCell = new int[wf.SimCap];

                float maxRadius = 0f;
                for (int i = 0; i < _archetypes.Length; i++)
                    if (_archetypes[i].Radius > maxRadius) maxRadius = _archetypes[i].Radius;
                _enemyGridCellSize = Math.Max(1f, maxRadius * (1f + BattleSimTuning.WreckBodyScale));
                _enemyGridHalf = setup.ArenaRadius + 3f;
                _enemyGridDim = Math.Max(1, (int)Math.Ceiling(_enemyGridHalf * 2f / _enemyGridCellSize));
                _enemyCellCount = new int[_enemyGridDim * _enemyGridDim];
                _enemyCellStart = new int[_enemyGridDim * _enemyGridDim + 1];
                _enemyCellItems = new int[256];
            }

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
            if (Phase == BattlePhase.Defeat || _setup == null) return;
            // 全部成长封顶（见 PlayerSetup 各 Max* 字段）：敌人规模进平台期后玩家也不再变强，
            // 稳态的"每波消耗"才不会随成长衰减到零。到顶的升级由业务侧移出三选一池。
            _player.Attack += modifier.AttackAdd;
            if (_player.MaxAttack > 0f && _player.Attack > _player.MaxAttack)
                _player.Attack = _player.MaxAttack;
            float minInterval = _player.MinAttackInterval > 0f ? _player.MinAttackInterval : 0.0008f; // 兜底防除零
            _player.AttackInterval = Math.Max(minInterval, _player.AttackInterval * modifier.AttackIntervalScale);
            _player.Range += modifier.RangeAdd;
            if (_player.MaxRange > 0f && _player.Range > _player.MaxRange)
                _player.Range = _player.MaxRange; // 索敌半径不越过拦截缓冲区
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
                float gained = newMax - _player.MaxHp; // 实际提升量（可能被封顶截短），回复等量当前血
                _player.MaxHp = newMax;
                _playerHp = Math.Min(_player.MaxHp, _playerHp + Math.Max(0f, gained));
            }
        }

        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f || Phase != BattlePhase.WaveActive) return;

            TickSpawns(deltaTime);
            TickEnemies(deltaTime);       // 移动（含泥地减速）+ 抵达自爆（id 序结算）
            if (CheckDefeat()) return;    // 自爆致死：抢在回血/拦截前判负
            TickPlayer(deltaTime);        // 回血 + 回转 + 击发（生成弹丸）
            TickProjectiles(deltaTime);   // 弹丸推进 + 扫掠弹着结算（拦截溅射在此发生）
            if (CheckDefeat()) return;    // 溅射致死
            TickWreckPush(deltaTime);     // 敌人拱开残骸（记账跟随位置）——负载随残骸留存累计增长
            CheckWaveEnd();
        }

        // 玩家血量归零即判负。分两处调用：自爆后（回血前）与拦截溅射后。
        private bool CheckDefeat()
        {
            if (_playerHp > 0f) return false;
            _playerHp = 0f;
            Phase = BattlePhase.Defeat;
            return true;
        }

        public void Dispose()
        {
            // 参考实现无非托管资源；清空事件防止终局后订阅方被迟到回调（接口为 ECS 后端的 World 释放而设）。
            EnemySpawned = null;
            EnemyHit = null;
            TurretFired = null;
            EnemyDetonated = null;
            WaveStarted = null;
            WaveCleared = null;
        }

        // 按成长曲线程序化生成第 waveIndex 波的刷怪流（无限模式：数量指数爬坡、到 MaxCount 进平台期）。逐角色统一公式展开。
        private void BeginWave(int waveIndex)
        {
            WaveIndex = waveIndex;
            var sc = _setup.Scaling;
            _waveStatScale = (float)Math.Pow(Math.Max(1f, sc.StatGrowth), waveIndex - 1);
            if (sc.MaxStatScale > 0f && _waveStatScale > sc.MaxStatScale)
                _waveStatScale = sc.MaxStatScale; // 数值成长封顶：平台期敌人不再变强（永续的必要条件，见 WaveScaling.MaxStatScale）

            _streamScratch.Clear();
            var roles = sc.Roles;
            if (roles != null)
            {
                for (int r = 0; r < roles.Length; r++)
                {
                    var role = roles[r];
                    if (waveIndex < role.UnlockWave) continue;
                    int step = waveIndex - role.UnlockWave;                 // 解锁后经过的波数
                    // 数量 = 乘性爬坡 + 线性斜率，封顶进平台期（公式见 WaveRole 文档）。
                    // 先在 double 域封顶再转 int：指数增长几十波后会溢出 int（转出负数 = 刷怪流凭空消失）。
                    double growth = role.CountGrowth > 1f ? Math.Pow(role.CountGrowth, step) : 1.0;
                    double rawCount = Math.Floor(role.BaseCount * growth) + Math.Floor(step * role.PerWave);
                    if (role.MaxCount > 0 && rawCount > role.MaxCount) rawCount = role.MaxCount;
                    int count = rawCount >= int.MaxValue ? int.MaxValue : (int)rawCount;
                    // 刷出间隔随数量同步乘性收缩（数量 ×k ⇒ 间隔 ÷k），单波刷怪时长近似恒定——
                    // 若用线性递减，指数涨的数量会让间隔突然撞底、波形从"细流"骤变"闸门"。
                    float interval = (float)(role.Interval0 / growth) - step * role.IntervalDecay;
                    if (interval < role.IntervalMin) interval = role.IntervalMin;
                    AddStream(role.EnemyId, count, interval);              // count ≤ 0 或原型缺失则内部跳过
                }
            }

            _spawns = _streamScratch.ToArray();
            Phase = BattlePhase.WaveActive;
            WaveStarted?.Invoke(waveIndex);
        }

        // 追加一条刷怪流；数量 ≤ 0 或角色 id 不在原型表则跳过（角色缺席不报错）。
        private void AddStream(int archId, int count, float interval)
        {
            if (count <= 0) return;
            if (!_archIndexById.TryGetValue(archId, out var archIndex)) return;
            _streamScratch.Add(new SpawnState
            {
                ArchIndex = archIndex,
                Remaining = count,
                Interval = Math.Max(0.001f, interval), // 仅防零/负；真实下限由 WaveRole.IntervalMin 配置
                Timer = 0f, // 首只立刻刷
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
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            var pos = new Vector2(
                (float)Math.Cos(angle) * _setup.ArenaRadius,
                (float)Math.Sin(angle) * _setup.ArenaRadius);
            var arch = _archetypes[archIndex];
            var e = new EnemyState
            {
                Id = _nextEnemyId++,
                ArchIndex = archIndex,
                Pos = pos,
                StatScale = _waveStatScale,
                Hp = arch.MaxHp * _waveStatScale,
            };
            _enemies.Add(e);
            EnemySpawned?.Invoke(new EnemySpawnedEvent(e.Id, arch.Id, pos));
        }

        // 移动（含泥地减速）+ 抵达收集：移动逐敌独立；同帧多个自爆统一按实例 id 升序结算——
        // 事件流顺序与泥地环形记账顺序因此在两后端间可复现（对拍契约）。
        private void TickEnemies(float dt)
        {
            _detonatingIdx.Clear();
            var wf = _setup.WreckField;
            for (int i = 0; i < _enemies.Count; i++)
            {
                var e = _enemies[i];
                var arch = _archetypes[e.ArchIndex];
                float contact = arch.Radius + _player.Radius;
                float dist = e.Pos.Length();

                if (dist > contact)
                {
                    float speed = arch.MoveSpeed;
                    if (_wreckCells != null)
                    {
                        // 泥地减速：所在格残骸越多越慢（下限 SlowFloor）。
                        int cell = SimMath.WreckCellIndex(e.Pos.X, e.Pos.Y, _wreckGridHalf, wf.CellSize, _wreckGridDim);
                        float mult = 1f - wf.SlowPerCount * _wreckCells[cell];
                        if (mult < wf.SlowFloor) mult = wf.SlowFloor;
                        speed *= mult;
                    }
                    // 径直冲向原点，不越过接触距离（dist > contact > 0 保证不除零）。
                    float newDist = Math.Max(contact, dist - speed * dt);
                    e.Pos *= newDist / dist;
                    _enemies[i] = e;
                }
                else
                {
                    _detonatingIdx.Add(i);
                }
            }
            if (_detonatingIdx.Count == 0) return;

            // id 升序结算自爆：一次性伤害 + 泥地记账 + 事件（每次扣血独立 max(0,·)，聚合结果与次序无关，
            // 定序只为事件流与记账可复现）。
            _detonatingIdx.Sort(_detonatorIdCompare);
            for (int k = 0; k < _detonatingIdx.Count; k++)
            {
                var e = _enemies[_detonatingIdx[k]];
                var arch = _archetypes[e.ArchIndex];
                float dmg = arch.Attack * e.StatScale;
                _playerHp = Math.Max(0f, _playerHp - dmg);
                AddWreck(e.Pos, e.ArchIndex);
                EnemyDetonated?.Invoke(new EnemyDetonatedEvent(e.Id, arch.Id, e.Pos, dmg, _playerHp));
            }

            // 统一移除：按索引降序 swap-remove（降序保证换入元素不污染待移除索引）。
            _detonatingIdx.Sort();
            for (int k = _detonatingIdx.Count - 1; k >= 0; k--)
            {
                int idx = _detonatingIdx[k];
                _enemies[idx] = _enemies[^1];
                _enemies.RemoveAt(_enemies.Count - 1);
            }
        }

        private void TickPlayer(float dt)
        {
            if (_player.RegenPerSecond > 0f)
                _playerHp = Math.Min(_player.MaxHp, _playerHp + _player.RegenPerSecond * dt);

            int target = FindNearestInRange();
            if (target < 0)
            {
                // 射程内无目标：冷却不往负累（不积欠账），朝向保持不动。
                if (_playerAttackCooldown < 0f) _playerAttackCooldown = 0f;
                return;
            }

            // 锁定目标：逐帧把炮口转向目标（越慢，切换分散目标的空当越大）。
            var tpos = _enemies[target].Pos;
            float desired = (float)(Math.Atan2(tpos.Y, tpos.X) * Rad2Deg);
            _turretAngleDeg = SimMath.MoveTowardsAngleDeg(_turretAngleDeg, desired, _player.RotationSpeed * dt);

            // 击发（真弹道）：不分射速一律「边转边打」——按有效射速沿当前炮口方向吐弹，不设对准门槛，
            // 转向途中照发（甩枪那几发划过战场，打到谁由 TickProjectiles 的扫掠碰撞决定，穿排/漏射自然涌现）。
            // 单帧可多发（上限 MaxShotsPerTick）。
            double muzzleRad = _turretAngleDeg / Rad2Deg;
            var muzzleDir = new Vector2((float)Math.Cos(muzzleRad), (float)Math.Sin(muzzleRad));
            float effInterval = _player.AttackInterval;

            _playerAttackCooldown -= dt;
            int shots = 0;
            while (_playerAttackCooldown <= 0f && shots < BattleSimTuning.MaxShotsPerTick)
            {
                _projectiles.Add(new ProjectileState { Pos = default, Dir = muzzleDir, Damage = _player.Attack });
                TurretFired?.Invoke(new TurretFiredEvent(muzzleDir));
                _playerAttackCooldown += effInterval;
                shots++;
            }
            if (_playerAttackCooldown < 0f) _playerAttackCooldown = 0f;
        }

        // 弹丸推进 + 扫掠弹着：位移段 vs 全体存活敌人取最早交点——直白 O(P×N)、刻意不加空间分区
        // （本后端的对比基线身份，见类注释）。命中即结算并消散；未命中飞到消散半径。
        private void TickProjectiles(float dt)
        {
            if (_projectiles.Count == 0) return;
            float step = _player.ProjectileSpeed * dt;
            float despawnSq = _player.ProjectileDespawnRadius * _player.ProjectileDespawnRadius;

            for (int i = 0; i < _projectiles.Count; )
            {
                var p = _projectiles[i];
                float dx = p.Dir.X * step, dy = p.Dir.Y * step;

                int best = -1;
                float bestT = float.MaxValue;
                for (int j = 0; j < _enemies.Count; j++)
                {
                    var e = _enemies[j];
                    float t = SimMath.SegmentCircleHitT(p.Pos.X, p.Pos.Y, dx, dy, e.Pos.X, e.Pos.Y,
                        _archetypes[e.ArchIndex].Radius + _player.ProjectileRadius);
                    if (t >= 0f && t < bestT)
                    {
                        bestT = t;
                        best = j;
                    }
                }

                if (best >= 0)
                {
                    var impact = new Vector2(p.Pos.X + dx * bestT, p.Pos.Y + dy * bestT);
                    DamageEnemy(best, p.Damage, impact);
                    _projectiles[i] = _projectiles[^1];
                    _projectiles.RemoveAt(_projectiles.Count - 1);
                    continue; // swap-remove：原位换入末位弹，不前进
                }

                p.Pos = new Vector2(p.Pos.X + dx, p.Pos.Y + dy);
                if (p.Pos.LengthSquared() >= despawnSq)
                {
                    _projectiles[i] = _projectiles[^1];
                    _projectiles.RemoveAt(_projectiles.Count - 1);
                    continue;
                }
                _projectiles[i] = p;
                i++;
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

        // 弹着结算：伤害 / 击杀 / 拦截溅射（按弹着点距离）/ 泥地记账；EnemyHit 的位置 = 弹着点。
        private void DamageEnemy(int index, float damage, Vector2 impact)
        {
            var e = _enemies[index];
            e.Hp -= damage;
            var arch = _archetypes[e.ArchIndex];
            bool killed = e.Hp <= 0f;
            float splash = 0f;
            if (killed)
            {
                // 拦截溅射：在离基地过近处击毁，弹头冲击波仍连带削基地——越近越疼（贴脸≈满溅射，半径边缘=0）。
                float dist = impact.Length();
                if (_player.SplashRadius > 0f && dist < _player.SplashRadius)
                {
                    float proximity = 1f - dist / _player.SplashRadius; // 0(边缘)..1(贴脸)
                    splash = arch.Attack * e.StatScale * _player.SplashDamageScale * proximity;
                    _playerHp = Math.Max(0f, _playerHp - splash);
                }
                AddWreck(impact, e.ArchIndex); // 残骸落定在弹着点附近（确定性散布）
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
            EnemyHit?.Invoke(new EnemyHitEvent(e.Id, arch.Id, impact, damage, killed, splash));
        }

        // 残骸落定：事件点 + 确定性散布偏移（SimMath.WreckRestOffset，零 RNG 消耗），写入环形槽位并在静置格记账；
        // 写满 SimCap 后复写游标处最老的槽位（旧居民出格 -1）。
        private void AddWreck(Vector2 eventPos, int archIndex)
        {
            if (_wreckCells == null) return;
            int seq = _nextWreckSeq++;
            SimMath.WreckRestOffset(eventPos.X, eventPos.Y, _archetypes[archIndex].Radius, seq, out float ox, out float oy);
            var pos = new Vector2(eventPos.X + ox, eventPos.Y + oy);
            int cell = SimMath.WreckCellIndex(pos.X, pos.Y, _wreckGridHalf, _setup.WreckField.CellSize, _wreckGridDim);

            int slot = _wreckNext;
            if (_wreckSlotCount == _wreckPos.Length) _wreckCells[_wreckCell[slot]]--; // 环形复写：最老的出格
            else _wreckSlotCount++;
            _wreckPos[slot] = pos;
            _wreckArch[slot] = archIndex;
            _wreckDrift[slot] = 0f;
            _wreckSeq[slot] = seq;
            _wreckCell[slot] = cell;
            _wreckNext = (_wreckNext + 1) % _wreckPos.Length;
            _wreckCells[cell]++;
        }

        // 推挤：每具残骸被"重叠的最近敌人"推开（距离平方最小、平票取小实例 id——顺序无关的归约：
        // 结果与遍历顺序无关，两后端可逐位对拍、ECS 侧可逐槽并行）。密度记账跟随位置（跨格旧 -1 新 +1）。
        // 刻意不设预算——它是规则不是演出；演算量随留存残骸数累计增长，正是两后端同题对比的主要负载源（ADR-0032）。
        private void TickWreckPush(float dt)
        {
            if (_wreckCells == null || _wreckSlotCount == 0 || _enemies.Count == 0) return;
            var wf = _setup.WreckField;
            if (wf.PushSpeed <= 0f) return;

            RebuildEnemyGrid();
            float maxStep = wf.PushSpeed * dt;
            float recover = wf.DriftRecoverPerSecond > 0f ? wf.DriftRecoverPerSecond * dt : 0f;

            for (int slot = 0; slot < _wreckSlotCount; slot++)
            {
                float drift = _wreckDrift[slot];
                if (recover > 0f && drift > 0f)
                {
                    // 车辙回淤：漂移预算随时间恢复——车流必须持续碾压才能保持通道。
                    drift -= recover;
                    if (drift < 0f) drift = 0f;
                    _wreckDrift[slot] = drift;
                }
                if (drift >= wf.PushMaxDrift) continue; // 漂移到顶：本 tick 拱不动了（回淤后下一 tick 又能动一点）

                var wpos = _wreckPos[slot];
                float wreckR = _archetypes[_wreckArch[slot]].Radius * BattleSimTuning.WreckBodyScale;
                int cx = (int)Math.Floor((wpos.X + _enemyGridHalf) / _enemyGridCellSize);
                int cy = (int)Math.Floor((wpos.Y + _enemyGridHalf) / _enemyGridCellSize);
                if (cx < 0) cx = 0; else if (cx >= _enemyGridDim) cx = _enemyGridDim - 1;
                if (cy < 0) cy = 0; else if (cy >= _enemyGridDim) cy = _enemyGridDim - 1;

                int best = -1;
                float bestDsq = float.MaxValue;
                int bestId = int.MaxValue;
                float bestContact = 0f;
                for (int oy = -1; oy <= 1; oy++)
                {
                    int gy = cy + oy;
                    if (gy < 0 || gy >= _enemyGridDim) continue;
                    for (int ox2 = -1; ox2 <= 1; ox2++)
                    {
                        int gx = cx + ox2;
                        if (gx < 0 || gx >= _enemyGridDim) continue;
                        int cell = gy * _enemyGridDim + gx;
                        int start = _enemyCellStart[cell];
                        int end = start + _enemyCellCount[cell];
                        for (int k = start; k < end; k++)
                        {
                            int ei = _enemyCellItems[k];
                            var e = _enemies[ei];
                            float contact = _archetypes[e.ArchIndex].Radius + wreckR;
                            float ddx = wpos.X - e.Pos.X, ddy = wpos.Y - e.Pos.Y;
                            float dsq = ddx * ddx + ddy * ddy;
                            if (dsq >= contact * contact) continue;
                            if (dsq < bestDsq || (dsq == bestDsq && e.Id < bestId))
                            {
                                bestDsq = dsq;
                                bestId = e.Id;
                                best = ei;
                                bestContact = contact;
                            }
                        }
                    }
                }
                if (best < 0) continue;

                var epos = _enemies[best].Pos;
                float dist = (float)Math.Sqrt(bestDsq);
                float dirX, dirY;
                if (dist > 1e-4f)
                {
                    dirX = (wpos.X - epos.X) / dist;
                    dirY = (wpos.Y - epos.Y) / dist;
                }
                else
                {
                    // 完全重合（弹着点即敌人中心的常见情形）：沿远离哨站的径向让开（与静置偏移同款回退）。
                    float wl = wpos.Length();
                    if (wl > 1e-4f) { dirX = wpos.X / wl; dirY = wpos.Y / wl; }
                    else { dirX = 1f; dirY = 0f; }
                }
                float move = bestContact - dist;
                if (move > maxStep) move = maxStep;
                float room = wf.PushMaxDrift - drift;
                if (move > room) move = room;
                if (move <= 1e-4f) continue;

                wpos = new Vector2(wpos.X + dirX * move, wpos.Y + dirY * move);
                _wreckPos[slot] = wpos;
                _wreckDrift[slot] = drift + move;
                int newCell = SimMath.WreckCellIndex(wpos.X, wpos.Y, _wreckGridHalf, wf.CellSize, _wreckGridDim);
                if (newCell != _wreckCell[slot])
                {
                    _wreckCells[_wreckCell[slot]]--;
                    _wreckCells[newCell]++;
                    _wreckCell[slot] = newCell; // 记账跟随位置：车辙被踩穿、路边堆垄
                }
            }
        }

        // 敌人占位网格重建（CSR 三段式：计数 → 前缀和 → 填充；填充期间计数清零复用为写游标）。O(敌人数)，每 tick 一次。
        private void RebuildEnemyGrid()
        {
            int n = _enemies.Count;
            if (_enemyCellItems.Length < n)
                _enemyCellItems = new int[Math.Max(n, _enemyCellItems.Length * 2)];

            Array.Clear(_enemyCellCount, 0, _enemyCellCount.Length);
            for (int i = 0; i < n; i++)
                _enemyCellCount[EnemyCellIndex(_enemies[i].Pos)]++;

            int sum = 0;
            for (int c = 0; c < _enemyCellCount.Length; c++)
            {
                _enemyCellStart[c] = sum;
                sum += _enemyCellCount[c];
            }
            _enemyCellStart[_enemyCellCount.Length] = sum;

            Array.Clear(_enemyCellCount, 0, _enemyCellCount.Length);
            for (int i = 0; i < n; i++)
            {
                int c = EnemyCellIndex(_enemies[i].Pos);
                _enemyCellItems[_enemyCellStart[c] + _enemyCellCount[c]++] = i;
            }
        }

        // 敌人占位格索引（与 SimMath.WreckCellIndex 同式的钳边网格，只是格边长 / 覆盖范围不同）。
        private int EnemyCellIndex(Vector2 p)
            => SimMath.WreckCellIndex(p.X, p.Y, _enemyGridHalf, _enemyGridCellSize, _enemyGridDim);

        private void CheckWaveEnd()
        {
            if (_enemies.Count > 0) return;
            for (int i = 0; i < _spawns.Length; i++)
                if (_spawns[i].Remaining > 0) return;

            // 波间维修：撑过一波即回满血——"每波消耗多少血"成为独立的单波压力指标（目标≈一半），
            // 不与持续回血叠加出"伤害小于回血则永生、大于则必死"的双稳态；失守只发生在单波承伤超过全血时。
            _playerHp = _player.MaxHp;

            // 无限模式：本波清空即停在 WaveCleared 等升级选择，选完 BeginNextWave 续下一（更难）波，无胜利终态。
            // 在飞弹丸不阻塞清波、随全场冻结（Tick 早退），续波后继续飞。
            Phase = BattlePhase.WaveCleared;
            WaveCleared?.Invoke(WaveIndex);
        }
    }
}
