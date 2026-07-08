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
    /// 确定性：随机源仅出生角度（<see cref="BattleSetup.Seed"/> 种子化的 <see cref="Random"/>）；
    /// 敌人列表按索引顺序演算、死亡 swap-remove，炮塔朝向按固定回转速度逐帧趋近目标——
    /// 同 Setup + 同 Tick 序列在同一平台上结果完全一致。无限模式：波次由 <see cref="WaveScaling"/> 逐波生成，
    /// 唯一终态是哨站被摧毁（<see cref="BattlePhase.Defeat"/>）。
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

        private const float AimToleranceDeg = 6f;   // 炮口角度差在此内即视为对准、可开火
        private const int MaxShotsPerTick = 64;      // 无上限射速下单帧最多发数（防病态循环；远超玩法所需）
        private const float FirehoseFireInterval = 0.06f; // 有效射速间隔低于此即进"火墙"：炮口未对准也持续击发（边转边扫、空放不结算伤害）
        private const double Rad2Deg = 180.0 / Math.PI;

        private BattleSetup _setup;
        private EnemyArchetype[] _archetypes;
        private Dictionary<int, int> _archIndexById;
        private Random _rng;

        private readonly List<EnemyState> _enemies = new();
        private SpawnState[] _spawns = Array.Empty<SpawnState>();
        private readonly List<SpawnState> _streamScratch = new(3); // BeginWave 组流的复用缓冲，免每波分配

        private PlayerSetup _player;   // 可变副本：升级修正直接改这份
        private float _playerHp;
        private float _playerAttackCooldown;
        private float _turretAngleDeg;      // 炮塔当前朝向（度）；逐帧按回转速度趋近最近目标
        private float _spinUp;              // 射速预热系数 0..1：有目标缓升、无目标缓降
        private float _waveStatScale = 1f;  // 当前波次的敌人成长系数（StatGrowth^(w-1)），出生时写进 EnemyState
        private int _nextEnemyId = 1;

        public BattlePhase Phase { get; private set; } = BattlePhase.Idle;
        public int WaveIndex { get; private set; }
        public float PlayerHp => _playerHp;
        public float PlayerMaxHp => _player.MaxHp;
        public float PlayerRange => _player.Range;
        public float PlayerAttack => _player.Attack;
        public float PlayerRotationSpeed => _player.RotationSpeed;
        public float TurretAngle => _turretAngleDeg;
        public float SpinUp => _spinUp;
        public int Kills { get; private set; }
        public int Score { get; private set; }
        public int EnemyCount => _enemies.Count;

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
            _spinUp = 0f;

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
            _player.Attack += modifier.AttackAdd;
            if (_player.MaxAttack > 0f && _player.Attack > _player.MaxAttack)
                _player.Attack = _player.MaxAttack; // 攻击封顶：不再秒杀，火力压力交给无上限射速
            // 攻速无上限：仅留极小下限防除零 / 单帧过多次开火（~75000 发/分，远超玩法所需）
            _player.AttackInterval = Math.Max(0.0008f, _player.AttackInterval * modifier.AttackIntervalScale);
            _player.Range += modifier.RangeAdd;
            if (_player.MaxRange > 0f && _player.Range > _player.MaxRange)
                _player.Range = _player.MaxRange; // 索敌半径封顶（不越过缓冲区；到顶后业务侧不再提供该升级）
            _player.RegenPerSecond += modifier.RegenAdd;
            _player.RotationSpeed += modifier.RotationSpeedAdd;
            if (_player.MaxRotationSpeed > 0f && _player.RotationSpeed > _player.MaxRotationSpeed)
                _player.RotationSpeed = _player.MaxRotationSpeed; // 回转封顶（见 PlayerSetup.MaxRotationSpeed）
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
            TickEnemies(deltaTime);       // 抵达基地的敌人自爆、伤害玩家
            if (CheckDefeat()) return;    // 自爆致死：抢在 TickPlayer 回血前判负，避免"已阵亡又被回血救活"
            TickPlayer(deltaTime);        // 玩家转向 + 开火拦截（含近距拦截的溅射伤害）
            if (CheckDefeat()) return;    // 溅射致死
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

        // 按成长曲线程序化生成第 waveIndex 波的刷怪流（无限模式：一波比一波多 / 强）。逐角色统一公式展开。
        private void BeginWave(int waveIndex)
        {
            WaveIndex = waveIndex;
            var sc = _setup.Scaling;
            _waveStatScale = (float)Math.Pow(Math.Max(1f, sc.StatGrowth), waveIndex - 1);

            _streamScratch.Clear();
            var roles = sc.Roles;
            if (roles != null)
            {
                for (int r = 0; r < roles.Length; r++)
                {
                    var role = roles[r];
                    if (waveIndex < role.UnlockWave) continue;
                    int step = waveIndex - role.UnlockWave;                 // 解锁后经过的波数
                    int count = role.BaseCount + (int)Math.Floor(step * role.PerWave);
                    float interval = role.Interval0 - step * role.IntervalDecay;
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
                Interval = Math.Max(0.01f, interval),
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

        private void TickEnemies(float dt)
        {
            // 逆序遍历：抵达基地的敌人自爆后 swap-remove（末位补位），逆序保证可安全边遍历边移除、不漏不重。
            for (int i = _enemies.Count - 1; i >= 0; i--)
            {
                var e = _enemies[i];
                var arch = _archetypes[e.ArchIndex];
                float contact = arch.Radius + _player.Radius;
                float dist = e.Pos.Length();

                if (dist > contact)
                {
                    // 径直冲向原点，不越过接触距离（dist > contact > 0 保证不除零）。
                    float newDist = Math.Max(contact, dist - arch.MoveSpeed * dt);
                    e.Pos *= newDist / dist;
                    _enemies[i] = e;
                }
                else
                {
                    // 抵达哨站：自爆——按成长系数放大的一次性接触伤害后从场上移除（不再贴脸驻留 DPS）。
                    float dmg = arch.Attack * e.StatScale;
                    _playerHp = Math.Max(0f, _playerHp - dmg);
                    EnemyDetonated?.Invoke(new EnemyDetonatedEvent(e.Id, arch.Id, e.Pos, dmg, _playerHp));
                    _enemies[i] = _enemies[^1];
                    _enemies.RemoveAt(_enemies.Count - 1);
                }
            }
        }

        private void TickPlayer(float dt)
        {
            if (_player.RegenPerSecond > 0f)
                _playerHp = Math.Min(_player.MaxHp, _playerHp + _player.RegenPerSecond * dt);

            int target = FindNearestInRange();
            if (target < 0)
            {
                // 射程内无目标：射速预热缓降；冷却不往负累（不积欠账），朝向保持不动。
                _spinUp = _player.SpinDownTime > 0f ? Math.Max(0f, _spinUp - dt / _player.SpinDownTime) : 0f;
                if (_playerAttackCooldown < 0f) _playerAttackCooldown = 0f;
                return;
            }

            // 锁定目标：射速预热缓升（近防炮点火感）+ 逐帧把炮口转向目标（越慢，切换分散目标的空当越大）。
            _spinUp = _player.SpinUpTime > 0f ? Math.Min(1f, _spinUp + dt / _player.SpinUpTime) : 1f;
            var tpos = _enemies[target].Pos;
            float desired = (float)(Math.Atan2(tpos.Y, tpos.X) * Rad2Deg);
            _turretAngleDeg = MoveTowardsAngleDeg(_turretAngleDeg, desired, _player.RotationSpeed * dt);

            // 开火：有效射速 = 基础射速 × spinUp（无上限）。每发打的是"炮口锥内的最近敌人"——炮管指着谁就打谁，
            // 不必是全局最近（回转扫过的其他敌人照样命中、照样结算伤害）。炮塔另按回转速度转向"全局最近"(target)＝想咬住的主威胁，
            // 扫掠途中顺带清掉挡在炮口上的其余敌人。炮口锥内为空时——
            //   · 低射速：本 tick 停火、下 tick 把炮口转过去（"瞄准后才发"的点射，转向途中静默蓄势）；
            //   · 高射速(火墙)：炮口在转向途中也持续击发，空放射向炮口方向（此刻真无敌人可命中、不结算伤害），画出"边转边扫"的火舌。
            // 单帧可多发。
            _playerAttackCooldown -= dt;
            if (_spinUp > 0.001f)
            {
                float effInterval = _player.AttackInterval / _spinUp;
                bool firehose = effInterval < FirehoseFireInterval;
                int shots = 0;
                while (_playerAttackCooldown <= 0f && shots < MaxShotsPerTick)
                {
                    int t = FindNearestInCone(_turretAngleDeg, AimToleranceDeg); // 炮口锥内最近敌人（指哪打哪，扫过即中）
                    if (t >= 0)
                    {
                        var p = _enemies[t].Pos;
                        DamageEnemy(t, _player.Attack);                     // 命中：结算伤害（内部发 EnemyHit）
                        TurretFired?.Invoke(new TurretFiredEvent(p, true));
                    }
                    else if (firehose && FindNearestInRange() >= 0)
                    {
                        TurretFired?.Invoke(new TurretFiredEvent(BarrelPoint(), false)); // 炮口空、射程内尚有敌：转向途中空放
                    }
                    else break;                                            // 低射速静默蓄势 / 射程内已空：停火
                    _playerAttackCooldown += effInterval;
                    shots++;
                }
            }
            if (_playerAttackCooldown < 0f) _playerAttackCooldown = 0f;
        }

        // 炮口锥内(与炮口夹角 ≤ toleranceDeg)、射程内的最近敌人索引；无则 -1。炮管指哪打哪——回转扫过的敌人即被此命中。
        private int FindNearestInCone(float angleDeg, float toleranceDeg)
        {
            int best = -1;
            float bestSq = _player.Range * _player.Range;
            for (int i = 0; i < _enemies.Count; i++)
            {
                var pos = _enemies[i].Pos;
                float dsq = pos.LengthSquared();
                if (dsq > bestSq) continue;
                float a = (float)(Math.Atan2(pos.Y, pos.X) * Rad2Deg);
                if (Math.Abs(DeltaAngleDeg(angleDeg, a)) > toleranceDeg) continue;
                bestSq = dsq;
                best = i;
            }
            return best;
        }

        // 炮口方向在射程边缘上的落点（空放曳光的终点，让"边转边扫"的火舌有可见去向）。
        private Vector2 BarrelPoint()
        {
            double rad = _turretAngleDeg / Rad2Deg;
            return new Vector2((float)Math.Cos(rad) * _player.Range, (float)Math.Sin(rad) * _player.Range);
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
            float splash = 0f;
            if (killed)
            {
                // 拦截溅射：在离基地过近处击毁，弹头冲击波仍连带削基地——越近越疼（贴脸≈满溅射，半径边缘=0）。
                float dist = e.Pos.Length();
                if (_player.SplashRadius > 0f && dist < _player.SplashRadius)
                {
                    float proximity = 1f - dist / _player.SplashRadius; // 0(边缘)..1(贴脸)
                    splash = arch.Attack * e.StatScale * _player.SplashDamageScale * proximity;
                    _playerHp = Math.Max(0f, _playerHp - splash);
                }
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
            EnemyHit?.Invoke(new EnemyHitEvent(e.Id, arch.Id, e.Pos, damage, killed, splash));
        }

        private void CheckWaveEnd()
        {
            if (_enemies.Count > 0) return;
            for (int i = 0; i < _spawns.Length; i++)
                if (_spawns[i].Remaining > 0) return;

            // 无限模式：本波清空即停在 WaveCleared 等升级选择，选完 BeginNextWave 续下一（更难）波，无胜利终态。
            Phase = BattlePhase.WaveCleared;
            WaveCleared?.Invoke(WaveIndex);
        }

        // ── 角度工具（度制，标准数学角）──────────────────────────────────────
        private static float NormalizeDeg(float a)
        {
            a %= 360f;
            if (a < 0f) a += 360f;
            return a;
        }

        // from→to 的最短带符号角差，落在 [-180, 180]。
        private static float DeltaAngleDeg(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d < -180f) d += 360f;
            else if (d > 180f) d -= 360f;
            return d;
        }

        // 以 maxDelta 为步长把 cur 朝 target 转（不过冲），返回归一化角。
        private static float MoveTowardsAngleDeg(float cur, float target, float maxDelta)
        {
            float d = DeltaAngleDeg(cur, target);
            if (maxDelta >= Math.Abs(d)) return NormalizeDeg(target);
            return NormalizeDeg(cur + Math.Sign(d) * maxDelta);
        }
    }
}
