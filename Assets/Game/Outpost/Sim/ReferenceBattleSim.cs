using System;
using System.Collections.Generic;
using System.Numerics;

namespace Game.Outpost.Sim
{
    /// <summary>
    /// <see cref="IBattleSim"/> 的参考实现：面向对象的直白写法（列表 + 结构体逐帧演算，索敌 / 命中都是 O(n) 线性扫描，
    /// 不做空间分区）。平台期的数千同屏它仍能演算，但这份"直白"正是与后续 ECS 后端同题对比的基线——
    /// 作为接缝的第一后端，它同时是规则的可执行规格，纯 C# 可直接单测 / 无头跑数。
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
        private static readonly float AimToleranceCosSq = (float)Math.Pow(Math.Cos(AimToleranceDeg * Math.PI / 180.0), 2); // 锥判定用 cos²(容差)，见 FindNearestInCone
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
        private float _waveStatScale = 1f;  // 当前波次的敌人成长系数（StatGrowth^(w-1)），出生时写进 EnemyState
        private int _nextEnemyId = 1;

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
                // 射程内无目标：冷却不往负累（不积欠账），朝向保持不动。
                if (_playerAttackCooldown < 0f) _playerAttackCooldown = 0f;
                return;
            }

            // 锁定目标：逐帧把炮口转向目标（越慢，切换分散目标的空当越大）。
            var tpos = _enemies[target].Pos;
            float desired = (float)(Math.Atan2(tpos.Y, tpos.X) * Rad2Deg);
            _turretAngleDeg = MoveTowardsAngleDeg(_turretAngleDeg, desired, _player.RotationSpeed * dt);

            // 炮口方向单位向量（本 tick 内朝向不变，shots 循环共用）——锥内判定用点积，免逐敌 Atan2。
            double muzzleRad = _turretAngleDeg / Rad2Deg;
            var muzzleDir = new Vector2((float)Math.Cos(muzzleRad), (float)Math.Sin(muzzleRad));

            // 开火：有效射速 = 基础射速（无上限、无预热）。每发打的是"炮口锥内的最近敌人"——炮管指着谁就打谁，
            // 不必是全局最近（回转扫过的其他敌人照样命中、照样结算伤害）。炮塔另按回转速度转向"全局最近"(target)＝想咬住的主威胁，
            // 扫掠途中顺带清掉挡在炮口上的其余敌人。炮口锥内为空时——
            //   · 低射速：本 tick 停火、下 tick 把炮口转过去（"瞄准后才发"的点射，转向途中静默）；
            //   · 高射速(火墙)：炮口在转向途中也持续击发，空放射向炮口方向（此刻真无敌人可命中、不结算伤害），画出"边转边扫"的火舌。
            // 单帧可多发。
            _playerAttackCooldown -= dt;
            float effInterval = _player.AttackInterval;
            bool firehose = effInterval < FirehoseFireInterval;
            int shots = 0;
            // 循环内敌人不移动、空放不改战场——锥一旦扫空整个 tick 都空，"射程内尚有敌"也只需查一次。
            // 缓存两者，避免高射速下每发空放重复 O(n) 扫描。
            bool coneEmpty = false;
            bool anyInRange = true, anyInRangeChecked = false;
            while (_playerAttackCooldown <= 0f && shots < MaxShotsPerTick)
            {
                int t = coneEmpty ? -1 : FindNearestInCone(muzzleDir); // 炮口锥内最近敌人（指哪打哪，扫过即中）
                if (t >= 0)
                {
                    var p = _enemies[t].Pos;
                    DamageEnemy(t, _player.Attack);                     // 命中：结算伤害（内部发 EnemyHit）
                    TurretFired?.Invoke(new TurretFiredEvent(p, true));
                }
                else
                {
                    coneEmpty = true;
                    if (!anyInRangeChecked) { anyInRange = FindNearestInRange() >= 0; anyInRangeChecked = true; }
                    if (!firehose || !anyInRange) break;               // 低射速静默 / 射程内已空：停火
                    TurretFired?.Invoke(new TurretFiredEvent(BarrelPoint(), false)); // 炮口空、射程内尚有敌：转向途中空放
                }
                _playerAttackCooldown += effInterval;
                shots++;
            }
            if (_playerAttackCooldown < 0f) _playerAttackCooldown = 0f;
        }

        // 炮口锥内(与炮口夹角 ≤ AimToleranceDeg)、射程内的最近敌人索引；无则 -1。炮管指哪打哪——回转扫过的敌人即被此命中。
        // 判定用点积（dot ≥ cos(容差)·|p| ⟺ dot² ≥ cos²·|p|²，且 dot > 0）——这是每发都跑的最热路径，
        // 数千同屏 × 每秒上千发时逐敌 Atan2 会成为主要开销，点积与角度比较数学等价且零三角函数。
        private int FindNearestInCone(Vector2 muzzleDir)
        {
            int best = -1;
            float bestSq = _player.Range * _player.Range;
            for (int i = 0; i < _enemies.Count; i++)
            {
                var pos = _enemies[i].Pos;
                float dsq = pos.LengthSquared();
                if (dsq > bestSq) continue;
                float dot = muzzleDir.X * pos.X + muzzleDir.Y * pos.Y;
                if (dot <= 0f || dot * dot < AimToleranceCosSq * dsq) continue;
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

            // 波间维修：撑过一波即回满血——"每波消耗多少血"成为独立的单波压力指标（目标≈一半），
            // 不与持续回血叠加出"伤害小于回血则永生、大于则必死"的双稳态；失守只发生在单波承伤超过全血时。
            _playerHp = _player.MaxHp;

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
