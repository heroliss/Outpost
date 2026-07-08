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

        /// <summary>攻击伤害升级上限（&le; 0 = 不封顶）。封顶后敌人不再被秒杀、需多发命中——把"火力"压力交给无上限的射速。</summary>
        public float MaxAttack;

        /// <summary>攻击间隔（秒）＝基础射速的倒数。攻速升级乘性缩短、<b>无上限</b>；实际射速还要乘 spin-up 预热系数。</summary>
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
        /// 回转速度升级上限（&le; 0 = 不封顶）。<b>封顶是无限模式能收尾的关键</b>：射速无上限、炮塔面对单一方向火力无穷，
        /// 但回转封顶后 360° 密集来袭时它每次只能扫一个方向、转身需时——后期密度越大、对面漏得越多，
        /// 击杀率随数量渐降直至被压垮（难度靠数量，而非"炮塔横扫无限快永不失守"）。
        /// </summary>
        public float MaxRotationSpeed;

        /// <summary>射速预热爬升时长（秒）：锁定目标后有效射速在此时间内从 0 线性升到满（近防炮点火感）。</summary>
        public float SpinUpTime;

        /// <summary>射速预热回落时长（秒）：脱离目标后有效射速在此时间内降回 0。</summary>
        public float SpinDownTime;

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
    /// 一个敌人角色的波次成长定义（由 <see cref="ReferenceBattleSim"/> 逐波展开成一条刷怪流）。
    /// 统一公式（仅在 w &ge; <see cref="UnlockWave"/> 时出现）：
    /// <c>数量 = BaseCount + floor((w - UnlockWave) × PerWave)</c>；
    /// <c>刷出间隔 = max(IntervalMin, Interval0 - (w - UnlockWave) × IntervalDecay)</c>。
    /// </summary>
    public struct WaveRole
    {
        /// <summary>敌人原型 id（对应 <see cref="EnemyArchetype.Id"/>；不在原型表则该角色被跳过）。</summary>
        public int EnemyId;
        /// <summary>解锁波次（w &ge; 本值才出现；常驻角色填 1）。</summary>
        public int UnlockWave;
        /// <summary>解锁波的基础数量。</summary>
        public int BaseCount;
        /// <summary>每波数量斜率（血越高的角色应越小，即出场越少）。</summary>
        public float PerWave;
        /// <summary>解锁波的刷出间隔（秒）。</summary>
        public float Interval0;
        /// <summary>刷出间隔下限（越后越密，但不低于此）。</summary>
        public float IntervalMin;
        /// <summary>刷出间隔每波递减量。</summary>
        public float IntervalDecay;
    }

    /// <summary>
    /// 无限模式的波次成长曲线：一组按角色（<see cref="WaveRole"/>）定义的刷怪流 + 全局数值成长。
    /// 设计意图：<b>数量线性增长（各角色）× 生命/伤害轻指数增长（StatGrowth）</b>——玩家线性变强、敌人越来越多，
    /// 后期数量必然压过单炮塔吞吐 ⇒ 一定会失守。角色表可扩（加敌人＝加一行），血越高的角色 PerWave 越小＝出场越少。
    /// </summary>
    public struct WaveScaling
    {
        /// <summary>各敌人角色的成长定义（顺序无关；按各自 UnlockWave 生效）。</summary>
        public WaveRole[] Roles;

        /// <summary>每波敌人生命 / 自爆伤害的乘法成长底数（如 1.02 = 每波强 2%）。成长系数 = StatGrowth^(w-1)。刻意偏小：难度主要靠数量。</summary>
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
