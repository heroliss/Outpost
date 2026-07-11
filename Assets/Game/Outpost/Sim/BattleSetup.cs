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

        /// <summary>残骸减速泥地参数（<see cref="WreckFieldSetup.SimCap"/> ≤ 0 = 机制关闭）。</summary>
        public WreckFieldSetup WreckField;
    }

    /// <summary>玩家（哨站炮塔）初始属性。玩家不移动，固定在原点自动索敌、转向目标后开火。</summary>
    public struct PlayerSetup
    {
        public float MaxHp;

        /// <summary>单次攻击伤害。</summary>
        public float Attack;

        /// <summary>攻击伤害升级上限（&le; 0 = 不封顶）。封顶后敌人不再被秒杀、需多发命中——把"火力"压力交给无上限的射速。</summary>
        public float MaxAttack;

        /// <summary>攻击间隔（秒）＝射速的倒数。攻速升级乘性缩短，下限见 <see cref="MinAttackInterval"/>。</summary>
        public float AttackInterval;

        /// <summary>
        /// 攻击间隔下限（&le; 0 = 仅防除零的极小值）。<b>玩家全部成长都封顶是永续稳态的前提</b>：
        /// 敌人规模进平台期后若玩家火力仍无限成长，每波消耗会衰减到零、失去张力。
        /// 下限取 0.004（250 发/秒 ≈ 每分钟一万五千发）时后期仍是可观的火墙。
        /// </summary>
        public float MinAttackInterval;

        /// <summary>索敌半径：只攻击与原点距离不超过它的敌人。</summary>
        public float Range;

        /// <summary>
        /// 索敌半径升级上限：<see cref="Range"/> 经 <see cref="PlayerModifier.RangeAdd"/> 提升时不超过它（&le; 0 = 不封顶）。
        /// 应小于 <see cref="BattleSetup.ArenaRadius"/>，留出拦截缓冲区——否则射程覆盖到出生环 = 出生即秒、游戏消失。
        /// </summary>
        public float MaxRange;

        /// <summary>每秒回血（不超过上限；只在波次进行中生效）。</summary>
        public float RegenPerSecond;

        /// <summary>每秒回血的升级上限（&le; 0 = 不封顶）。与其余封顶同理：平台期玩家不再变强。</summary>
        public float MaxRegen;

        /// <summary>生命上限的升级上限（&le; 0 = 不封顶）。与其余封顶同理：平台期玩家不再变强。</summary>
        public float MaxHpCap;

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

        /// <summary>
        /// 拦截溅射的危险半径：在离基地小于此距离处击毁敌人，弹头冲击波仍会连带削基地（越近越疼）。
        /// 0 = 关闭溅射。这是"近防炮"的核心张力来源——逼玩家尽量在远处早拦，也让"射程"升级有意义。
        /// </summary>
        public float SplashRadius;

        /// <summary>溅射伤害系数：实际溅射 = 敌人 Attack × 本系数 × 贴近度(0..1)。</summary>
        public float SplashDamageScale;

        /// <summary>弹丸飞行速度（单位/秒）。命中已非 hitscan：击发产生真实弹丸沿炮口方向直飞，伤害在弹着帧结算。</summary>
        public float ProjectileSpeed;

        /// <summary>弹丸碰撞半径（与敌人半径相加做扫掠求交）。</summary>
        public float ProjectileRadius;

        /// <summary>
        /// 弹丸消散半径：飞离原点超过此距离才消失（远大于射程——未命中的弹继续飞出场外，
        /// 途中仍可命中射程外的敌人）。在飞弹数量的主要旋钮：越大并发越多、模拟碰撞负载越高。
        /// </summary>
        public float ProjectileDespawnRadius;
    }

    /// <summary>
    /// 残骸减速泥地参数（残骸首次成为模拟状态）。规则本身以<b>密度网格</b>表述：场地按 <see cref="CellSize"/>
    /// 划格，敌人被击杀/自爆时其弹着/自爆点所在格计数 +1（总量 <see cref="SimCap"/> 环形上限，最老的出格时 -1）；
    /// 敌人移速 ×= <c>max(SlowFloor, 1 − SlowPerCount × 格计数)</c>——"上一波死在哪，下一波就在哪儿减速"，
    /// 残骸层从纯观赏变成防御地形。格子化是规则而非实现优化：两后端 O(1) 同实现、天然确定性。
    /// </summary>
    public struct WreckFieldSetup
    {
        /// <summary>密度格边长（世界单位）。网格覆盖 ±(ArenaRadius+1)。</summary>
        public float CellSize;

        /// <summary>每具残骸贡献的减速量（如 0.012 = 每具 -1.2% 移速）。</summary>
        public float SlowPerCount;

        /// <summary>减速下限乘数（如 0.4 = 最慢降到 40% 移速）。</summary>
        public float SlowFloor;

        /// <summary>模拟侧残骸留存上限（环形复写）。≤ 0 = 整个泥地机制关闭。</summary>
        public int SimCap;
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

        /// <summary>碰撞半径（决定抵达自爆的接触距离 = 与玩家半径之和）。</summary>
        public float Radius;

        /// <summary>击杀得分。</summary>
        public int Score;
    }

    /// <summary>
    /// 一个敌人角色的波次成长定义（由 <see cref="ReferenceBattleSim"/> 逐波展开成一条刷怪流）。
    /// 统一公式（仅在 w &ge; <see cref="UnlockWave"/> 时出现，step = w - UnlockWave）：
    /// <c>数量 = min(MaxCount, floor(BaseCount × CountGrowth^step) + floor(step × PerWave))</c>；
    /// <c>刷出间隔 = max(IntervalMin, Interval0 / CountGrowth^step - step × IntervalDecay)</c>——
    /// 间隔随数量同步乘性收缩，单波刷怪时长（≈ BaseCount × Interval0）近似恒定。
    /// 乘性 <see cref="CountGrowth"/> 让数量能在十几波内爬到千级，<see cref="MaxCount"/> 封顶即"规模平台期"——
    /// 之后每波压力恒定，托管稳态可以永续（难度曲线 = 爬坡段 + 平台段）。
    /// </summary>
    public struct WaveRole
    {
        /// <summary>敌人原型 id（对应 <see cref="EnemyArchetype.Id"/>；不在原型表则该角色被跳过）。</summary>
        public int EnemyId;
        /// <summary>解锁波次（w &ge; 本值才出现；常驻角色填 1）。</summary>
        public int UnlockWave;
        /// <summary>解锁波的基础数量。</summary>
        public int BaseCount;
        /// <summary>每波数量的乘性成长底数（如 1.35 = 每波多 35%；1 = 不乘性增长）。海量炮灰角色靠它指数爬坡。</summary>
        public float CountGrowth;
        /// <summary>每波数量的线性斜率（与乘性成长叠加；低频角色可只用线性）。</summary>
        public float PerWave;
        /// <summary>数量封顶（&le; 0 = 不封顶）。到顶即该角色进入平台期——无限模式能"永续"的前提。</summary>
        public int MaxCount;
        /// <summary>解锁波的刷出间隔（秒）。</summary>
        public float Interval0;
        /// <summary>刷出间隔下限（越后越密，但不低于此）。</summary>
        public float IntervalMin;
        /// <summary>刷出间隔每波递减量。</summary>
        public float IntervalDecay;
    }

    /// <summary>
    /// 无限模式的波次成长曲线：一组按角色（<see cref="WaveRole"/>）定义的刷怪流 + 全局数值成长。
    /// 设计意图：<b>数量指数爬坡到平台期（各角色 CountGrowth/MaxCount）× 生命/伤害轻指数增长（StatGrowth，同样封顶）</b>——
    /// 玩家升级变强、敌人前期越来越多，约 20 波后双方都到顶进入稳态：每波压力恒定（目标≈消耗一半维修血量）、托管永续。
    /// 角色表可扩（加敌人＝加一行），血越高的角色数量参数越小＝出场越少。
    /// </summary>
    public struct WaveScaling
    {
        /// <summary>各敌人角色的成长定义（顺序无关；按各自 UnlockWave 生效）。</summary>
        public WaveRole[] Roles;

        /// <summary>每波敌人生命 / 自爆伤害的乘法成长底数（如 1.02 = 每波强 2%）。成长系数 = StatGrowth^(w-1)。刻意偏小：难度主要靠数量。</summary>
        public float StatGrowth;

        /// <summary>
        /// 数值成长系数封顶（&le; 0 = 不封顶）。<b>永续的必要条件</b>：不封顶则 StatGrowth^w 无界，
        /// 后期单只漏怪的自爆伤害迟早超过玩家全血，任何稳态都会被击穿。与数量封顶一起构成平台期。
        /// </summary>
        public float MaxStatScale;
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
