namespace Game.Outpost.Sim
{
    /// <summary>
    /// 一局战斗的完整输入：玩家初始属性 + 敌人原型表 + 波次表 + 随机种子。
    /// 纯数据、无任何引擎/框架/配置后端类型——由业务侧把配置表（Luban 等）翻译成本结构后喂给
    /// <see cref="IBattleSim.Start"/>。同一份 Setup + 同一种子 + 相同的 Tick 序列 = 完全相同的战斗结果（可测试、可复现）。
    /// </summary>
    public sealed class BattleSetup
    {
        /// <summary>随机种子（目前唯一的随机性来源是敌人出生角度）。固定种子 = 确定性战斗。</summary>
        public int Seed;

        /// <summary>敌人出生环半径（玩家固定在原点，敌人从这个半径的圆环上刷出、径直冲向玩家）。</summary>
        public float ArenaRadius = 10f;

        public PlayerSetup Player;

        /// <summary>全部敌人原型。波次条目经 <see cref="WaveSpawnEntry.ArchetypeId"/> 引用这里的 <see cref="EnemyArchetype.Id"/>。</summary>
        public EnemyArchetype[] Enemies;

        /// <summary>波次序列（打完最后一波 = 胜利）。</summary>
        public WaveSetup[] Waves;
    }

    /// <summary>玩家（哨站炮塔）初始属性。玩家不移动，固定在原点自动索敌开火。</summary>
    public struct PlayerSetup
    {
        public float MaxHp;

        /// <summary>单次攻击伤害。</summary>
        public float Attack;

        /// <summary>攻击间隔（秒）。</summary>
        public float AttackInterval;

        /// <summary>索敌半径：只攻击与原点距离不超过它的敌人。</summary>
        public float Range;

        /// <summary>每秒回血（不超过上限；只在波次进行中生效）。</summary>
        public float RegenPerSecond;

        /// <summary>玩家碰撞半径：敌人抵近到「双方半径之和」即自爆。</summary>
        public float Radius;

        /// <summary>
        /// 拦截溅射的危险半径：在离基地小于此距离处击毁敌人，弹头冲击波仍会连带削基地（越近越疼）。
        /// 0 = 关闭溅射。这是"近防炮"的核心张力来源——逼玩家尽量在远处早拦，也让"射程"升级有意义。
        /// </summary>
        public float SplashRadius;

        /// <summary>溅射伤害系数：实际溅射 = 敌人 Attack × 本系数 × 贴近度(0..1)。</summary>
        public float SplashDamageScale;
    }

    /// <summary>敌人原型（一种敌人的静态属性）。</summary>
    public struct EnemyArchetype
    {
        public int Id;
        public float MaxHp;
        public float MoveSpeed;

        /// <summary>抵达玩家自爆时对玩家的一次性伤害。</summary>
        public float Attack;

        /// <summary>攻击间隔（秒）。当前"接触即自爆"模型下未使用，保留列以备后续"驻留攻击型"敌人。</summary>
        public float AttackInterval;

        /// <summary>碰撞半径（决定抵达自爆的接触距离 = 与玩家半径之和）。</summary>
        public float Radius;

        /// <summary>击杀得分。</summary>
        public int Score;
    }

    /// <summary>一波内的一条刷怪流：某原型刷 Count 只、每 Interval 秒一只（首只立刻刷）。多条流并行推进。</summary>
    public struct WaveSpawnEntry
    {
        public int ArchetypeId;
        public int Count;
        public float Interval;
    }

    /// <summary>一个波次 = 若干条并行的刷怪流。全部刷完且场上清空 = 本波结束。</summary>
    public sealed class WaveSetup
    {
        public WaveSpawnEntry[] Spawns;
    }

    /// <summary>
    /// 玩家属性修正（波间三选一升级的落点）。全字段一次性应用、立即生效：
    /// 加法项默认 0（不变）、<see cref="AttackIntervalScale"/> 是乘法项默认 1（不变，小于 1 = 攻速变快）。
    /// <see cref="MaxHpAdd"/> 在提升上限的同时回复等量当前血（升级不该是"空上限"）。
    /// </summary>
    public readonly struct PlayerModifier
    {
        public readonly float AttackAdd;
        public readonly float AttackIntervalScale;
        public readonly float RangeAdd;
        public readonly float MaxHpAdd;
        public readonly float RegenAdd;

        public PlayerModifier(
            float attackAdd = 0f,
            float attackIntervalScale = 1f,
            float rangeAdd = 0f,
            float maxHpAdd = 0f,
            float regenAdd = 0f)
        {
            AttackAdd = attackAdd;
            AttackIntervalScale = attackIntervalScale;
            RangeAdd = rangeAdd;
            MaxHpAdd = maxHpAdd;
            RegenAdd = regenAdd;
        }
    }
}
