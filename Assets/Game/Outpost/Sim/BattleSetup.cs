namespace Game.Outpost.Sim
{
    /// <summary>
    /// 一局战斗的完整输入：玩家初始属性 + 敌人原型表 + 波次成长曲线 + 随机种子。
    /// 纯数据、无任何引擎/框架/配置后端类型——由业务侧把配置表（Luban 等）翻译成本结构后喂给
    /// <see cref="IBattleSim.Start"/>。同一份 Setup + 同一种子 + 相同的 Tick 序列 = 完全相同的战斗结果（可测试、可复现）。
    /// <para><b>无限模式</b>：不再预列举波次，波次由 <see cref="WaveScaling"/> 的成长参数程序化生成、一波比一波难，
    /// 直到哨站被摧毁（<see cref="BattlePhase.Defeat"/>）——没有胜利终态，比拼的是坚持到第几波 / 击杀多少。</para>
    /// </summary>
    public sealed class BattleSetup
    {
        /// <summary>随机种子（目前唯一的随机性来源是敌人出生角度）。固定种子 = 确定性战斗。</summary>
        public int Seed;

        /// <summary>敌人出生环半径（玩家固定在原点，敌人从这个半径的圆环上刷出、径直冲向玩家）。</summary>
        public float ArenaRadius = 10f;

        public PlayerSetup Player;

        /// <summary>全部敌人原型。波次成长经 <see cref="WaveScaling"/> 的角色 id 引用这里的 <see cref="EnemyArchetype.Id"/>。</summary>
        public EnemyArchetype[] Enemies;

        /// <summary>波次成长曲线（无限模式下逐波程序化生成的参数）。</summary>
        public WaveScaling Scaling;
    }

    /// <summary>玩家（哨站炮塔）初始属性。玩家不移动，固定在原点自动索敌、转向目标后开火。</summary>
    public struct PlayerSetup
    {
        public float MaxHp;

        /// <summary>单次攻击伤害。</summary>
        public float Attack;

        /// <summary>攻击间隔（秒）。</summary>
        public float AttackInterval;

        /// <summary>索敌半径：只攻击与原点距离不超过它的敌人。</summary>
        public float Range;

        /// <summary>
        /// 索敌半径升级上限：<see cref="Range"/> 经 <see cref="PlayerModifier.RangeAdd"/> 提升时不超过它（&le; 0 = 不封顶）。
        /// 应小于 <see cref="BattleSetup.ArenaRadius"/>，留出拦截缓冲区——否则射程覆盖到出生环 = 出生即秒、游戏消失。
        /// </summary>
        public float MaxRange;

        /// <summary>每秒回血（不超过上限；只在波次进行中生效）。</summary>
        public float RegenPerSecond;

        /// <summary>玩家碰撞半径：敌人抵近到「双方半径之和」即自爆。</summary>
        public float Radius;

        /// <summary>
        /// 炮塔回转速度（度/秒）：炮口需转到指向目标（在容差内）才能开火。切换目标要花时间转过去——
        /// 越慢，分散来袭（尤其快速突袭者、后期无人机海）越容易在再瞄的空当里漏过。回转伺服升级提升它。
        /// </summary>
        public float RotationSpeed;

        /// <summary>
        /// 拦截溅射的危险半径：在离基地小于此距离处击毁敌人，弹头冲击波仍会连带削基地（越近越疼）。
        /// 0 = 关闭溅射。这是"近防炮"的核心张力来源——逼玩家尽量在远处早拦，也让"射程"升级有意义。
        /// </summary>
        public float SplashRadius;

        /// <summary>溅射伤害系数：实际溅射 = 敌人 Attack × 本系数 × 贴近度(0..1)。</summary>
        public float SplashDamageScale;
    }

    /// <summary>敌人原型（一种敌人的静态属性）。生命 / 自爆伤害是第 1 波基准值，按波次 <see cref="WaveScaling.StatGrowth"/> 放大。</summary>
    public struct EnemyArchetype
    {
        public int Id;

        /// <summary>生命上限（第 1 波基准值；实际生命 = 本值 × 该波成长系数）。</summary>
        public float MaxHp;

        public float MoveSpeed;

        /// <summary>抵达玩家自爆时对玩家的一次性伤害（第 1 波基准值；实际伤害 = 本值 × 该波成长系数）。</summary>
        public float Attack;

        /// <summary>攻击间隔（秒）。当前"接触即自爆"模型下未使用，保留列以备后续"驻留攻击型"敌人。</summary>
        public float AttackInterval;

        /// <summary>碰撞半径（决定抵达自爆的接触距离 = 与玩家半径之和）。</summary>
        public float Radius;

        /// <summary>击杀得分。</summary>
        public int Score;
    }

    /// <summary>
    /// 无限模式的波次成长曲线：一组成长参数，由 <see cref="ReferenceBattleSim"/> 逐波程序化展开成刷怪流。
    /// 设计意图：<b>数量线性增长 × 生命/伤害轻指数增长</b>——玩家每波一张升级卡大致线性变强，敌人越来越多，
    /// 前期有明显成长感、后期数量必然压过单炮塔吞吐 ⇒ 一定会失守（有限局，比拼坚持到第几波）。
    /// <para>三个角色（血低→高、出场多→少）：<b>无人机</b>=慢弱靠量的炮灰（后期海量，ECS 压力源）、
    /// <b>突袭者</b>=快速突击（惩罚慢回转）、<b>装甲兵</b>=慢厚重甲（出场最少）。三者原型 id 由业务侧
    /// （配置→Setup 的工厂）按约定填入；某角色 id 在原型表里不存在则该角色不参与刷怪。</para>
    /// </summary>
    public struct WaveScaling
    {
        /// <summary>无人机（炮灰）原型 id。</summary>
        public int FodderArchId;
        /// <summary>突袭者（快速突击）原型 id。</summary>
        public int StrikerArchId;
        /// <summary>装甲兵（重甲）原型 id。</summary>
        public int HeavyArchId;

        /// <summary>无人机第 1 波数量。</summary>
        public int FodderBase;
        /// <summary>无人机每波追加量：count = FodderBase + floor((w-1) × 本值)。后期海量。</summary>
        public float FodderPerWave;
        /// <summary>无人机第 1 波刷出间隔（秒）。</summary>
        public float FodderInterval0;
        /// <summary>无人机刷出间隔下限（越后越密，但不低于此）。</summary>
        public float FodderIntervalMin;
        /// <summary>无人机刷出间隔每波递减量。</summary>
        public float FodderIntervalDecay;

        /// <summary>突袭者解锁波次（w &ge; 本值才出现）。</summary>
        public int StrikerUnlockWave;
        /// <summary>突袭者每波数量斜率：count = max(1, floor((w - 解锁波 + 1) × 本值))。</summary>
        public float StrikerPerWave;
        /// <summary>突袭者刷出间隔（秒）。</summary>
        public float StrikerInterval;

        /// <summary>装甲兵解锁波次（w &ge; 本值才出现）。</summary>
        public int HeavyUnlockWave;
        /// <summary>装甲兵每波数量斜率（血最高、出场最少）。</summary>
        public float HeavyPerWave;
        /// <summary>装甲兵刷出间隔（秒，很稀）。</summary>
        public float HeavyInterval;

        /// <summary>每波敌人生命 / 自爆伤害的乘法成长底数（如 1.025 = 每波强 2.5%）。成长系数 = StatGrowth^(w-1)。刻意偏小：难度主要靠数量。</summary>
        public float StatGrowth;
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

        /// <summary>炮塔回转速度增量（度/秒）。</summary>
        public readonly float RotationSpeedAdd;

        public PlayerModifier(
            float attackAdd = 0f,
            float attackIntervalScale = 1f,
            float rangeAdd = 0f,
            float maxHpAdd = 0f,
            float regenAdd = 0f,
            float rotationSpeedAdd = 0f)
        {
            AttackAdd = attackAdd;
            AttackIntervalScale = attackIntervalScale;
            RangeAdd = rangeAdd;
            MaxHpAdd = maxHpAdd;
            RegenAdd = regenAdd;
            RotationSpeedAdd = rotationSpeedAdd;
        }
    }
}
