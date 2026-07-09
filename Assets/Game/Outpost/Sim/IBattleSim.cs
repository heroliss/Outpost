using System;
using System.Numerics;

namespace Game.Outpost.Sim
{
    /// <summary>战斗宏观阶段。终态（<see cref="Defeat"/>）后 Tick 变为空操作。无限模式无胜利终态。</summary>
    public enum BattlePhase
    {
        /// <summary>尚未 Start。</summary>
        Idle,

        /// <summary>波次进行中（刷怪 / 移动 / 互相攻击）。</summary>
        WaveActive,

        /// <summary>本波清空、等待 <see cref="IBattleSim.BeginNextWave"/>（波间升级选择的停留点）。</summary>
        WaveCleared,

        /// <summary>哨站被摧毁——唯一终态（无限模式无胜利，比拼坚持到第几波）。</summary>
        Defeat,
    }

    /// <summary>存活敌人的只读快照，供表现层逐帧同步位置 / 血量。平面为 2D（业务侧自行映射到世界坐标，如 XZ 平面）。</summary>
    public readonly struct EnemySnapshot
    {
        /// <summary>实例 id（一局内唯一、单调递增），表现层用它对应池化实体。</summary>
        public readonly int Id;

        /// <summary>原型 id（对应 <see cref="EnemyArchetype.Id"/>），表现层用它选 prefab / 颜色。</summary>
        public readonly int ArchetypeId;

        public readonly Vector2 Position;
        public readonly float Hp;
        public readonly float MaxHp;

        public EnemySnapshot(int id, int archetypeId, Vector2 position, float hp, float maxHp)
        {
            Id = id;
            ArchetypeId = archetypeId;
            Position = position;
            Hp = hp;
            MaxHp = maxHp;
        }
    }

    /// <summary>敌人刷出（表现层据此生成池化实体）。</summary>
    public readonly struct EnemySpawnedEvent
    {
        public readonly int EnemyId;
        public readonly int ArchetypeId;
        public readonly Vector2 Position;

        public EnemySpawnedEvent(int enemyId, int archetypeId, Vector2 position)
        {
            EnemyId = enemyId;
            ArchetypeId = archetypeId;
            Position = position;
        }
    }

    /// <summary>
    /// 敌人被玩家击中（伤害飘字 / 击杀回收的驱动源；<see cref="Killed"/> 为 true 时该敌人已从存活列表移除）。
    /// <see cref="SplashDamage"/> &gt; 0 表示这次击杀发生在离基地过近处、弹头冲击波仍连带削了基地（见 <see cref="IBattleSim"/> 溅射规则）。
    /// </summary>
    public readonly struct EnemyHitEvent
    {
        public readonly int EnemyId;
        public readonly int ArchetypeId;
        public readonly Vector2 Position;
        public readonly float Damage;
        public readonly bool Killed;

        /// <summary>本次击杀连带给玩家造成的溅射伤害（0 = 无溅射；仅"击杀且离基地够近"时 &gt; 0）。</summary>
        public readonly float SplashDamage;

        public EnemyHitEvent(int enemyId, int archetypeId, Vector2 position, float damage, bool killed, float splashDamage = 0f)
        {
            EnemyId = enemyId;
            ArchetypeId = archetypeId;
            Position = position;
            Damage = damage;
            Killed = killed;
            SplashDamage = splashDamage;
        }
    }

    /// <summary>
    /// 炮塔击发一发——<b>每发都触发，无论是否命中</b>。表现层据此画炮口闪光 + 曳光；刻意与"敌人被击中"
    /// (<see cref="EnemyHitEvent"/>) 分离：前者是"炮管吐了一发"，后者是"某敌人挨了打"，一发命中会两者都触发。
    /// 高射速火墙里炮口在转向途中也持续击发，<see cref="Hit"/> 为 false 的是尚未对准目标的空放
    /// （不结算伤害、曳光射向炮口方向），正是"边转边扫"火舌的可见来源。
    /// </summary>
    public readonly struct TurretFiredEvent
    {
        /// <summary>这一发的落点：命中时为目标敌人位置；空放时为炮口方向在射程边缘上的点。</summary>
        public readonly Vector2 Aim;

        /// <summary>是否命中了敌人（false = 转向途中未对准的空放）。</summary>
        public readonly bool Hit;

        public TurretFiredEvent(Vector2 aim, bool hit)
        {
            Aim = aim;
            Hit = hit;
        }
    }

    /// <summary>
    /// 敌人抵达玩家并自爆：一次性造成 <see cref="Damage"/> 伤害后<b>即从存活列表移除</b>（不再贴脸驻留输出）。
    /// 表现层据 <see cref="EnemyId"/> 回收该敌人视觉、<see cref="ArchetypeId"/> 选爆炸颜色 / 体量、<see cref="Position"/> 定位爆点。
    /// </summary>
    public readonly struct EnemyDetonatedEvent
    {
        /// <summary>自爆的敌人实例 id（此刻已从存活列表移除，表现层据此回收其池化实体）。</summary>
        public readonly int EnemyId;

        /// <summary>原型 id（表现层用它选爆炸颜色 / 体量）。</summary>
        public readonly int ArchetypeId;

        /// <summary>自爆位置（抵达玩家的接触点附近）。</summary>
        public readonly Vector2 Position;

        public readonly float Damage;

        /// <summary>受击后的剩余血量（已扣减，不会为负）。</summary>
        public readonly float HpAfter;

        public EnemyDetonatedEvent(int enemyId, int archetypeId, Vector2 position, float damage, float hpAfter)
        {
            EnemyId = enemyId;
            ArchetypeId = archetypeId;
            Position = position;
            Damage = damage;
            HpAfter = hpAfter;
        }
    }

    /// <summary>
    /// 战斗模拟接缝：一局自动战斗的全部规则演算（刷怪 / 索敌 / 转向开火 / 移动 / 伤害 / 无限波次成长）。
    /// 纯 C# 契约、零引擎与框架依赖——表现（GameObject / ECS 渲染）、数据（配置表）、编排（Model / Flow）都在接缝外。
    /// 参考实现 <see cref="ReferenceBattleSim"/>；后续可整体置换为 ECS 后端而消费方零改动（ports &amp; adapters）。
    /// </summary>
    /// <remarks>
    /// <b>驱动契约</b>：单线程使用；调用方按帧 <see cref="Tick"/>，事件在 Tick / Start 调用栈内同步触发——
    /// 事件回调里只做读取与外发，<b>不要</b>回调内再调本接口的写方法（Start / Tick / BeginNextWave / ApplyModifier）。<br/>
    /// <b>接触模型</b>：敌人径直冲向玩家，抵达即<b>自爆</b>（<see cref="EnemyDetonated"/>，一次性伤害后移除，不驻留输出）；
    /// 玩家在离基地过近处击毁敌人会吃<b>拦截溅射</b>（<see cref="EnemyHitEvent.SplashDamage"/>，越近越疼、随 <c>PlayerSetup.SplashRadius/SplashDamageScale</c> 配置）。<br/>
    /// <b>开火模型</b>：炮塔按 <c>PlayerSetup.RotationSpeed</c> 逐帧转向最近目标；命中为 hitscan（对准最近目标即同帧结算，无飞行物）。
    /// 低射速下"瞄准后才发"——转向途中静默；有效射速够高（火墙）时炮口在转向途中<b>也持续击发</b>（<see cref="TurretFired"/>，未对准的空放不结算伤害），
    /// 使回转越慢越难覆盖四面来袭、越易漏怪（<see cref="TurretAngle"/> 供表现层画炮管）。<br/>
    /// <b>无限模式</b>：波次由 <c>WaveScaling</c> 逐波程序化生成——数量指数爬坡、约 20 波后到各角色 MaxCount 进入平台期（每波压力恒定）；
    /// 唯一终态是哨站被摧毁（<see cref="BattlePhase.Defeat"/>），无胜利。<br/>
    /// <b>波间维修</b>：撑过一波（进入 <see cref="BattlePhase.WaveCleared"/> 时）血量自动回满——血量语义是"本波承受力"，
    /// 失守只发生在单波承伤超过全血时；主动结束一局由业务层负责（撤离）。<br/>
    /// <b>聚合读取</b>：属性在两次 Tick 之间保持稳定；存活敌人经 <see cref="EnemyCount"/> + <see cref="GetEnemy"/>
    /// 按索引零分配遍历（索引顺序会因移除而变化，跨帧跟踪用 <see cref="EnemySnapshot.Id"/>）。<br/>
    /// <b>Dispose</b>：参考实现无资源可释放；ECS 后端会持有 World，消费方按 IDisposable 统一管理。
    /// </remarks>
    public interface IBattleSim : IDisposable
    {
        BattlePhase Phase { get; }

        /// <summary>当前波次（1 起；Start 前为 0）。无限模式无上限——它就是"坚持到第几波"的战绩。</summary>
        int WaveIndex { get; }

        float PlayerHp { get; }

        float PlayerMaxHp { get; }

        /// <summary>玩家当前索敌半径（升级会改变）。表现层据此绘制射程圈。</summary>
        float PlayerRange { get; }

        /// <summary>玩家当前单发攻击力（已含封顶）。业务侧据此判断"攻击已封顶、不再提供该升级"。</summary>
        float PlayerAttack { get; }

        /// <summary>玩家当前攻击间隔（秒，已含下限）。业务侧据此判断"攻速已到顶、不再提供该升级"。</summary>
        float PlayerAttackInterval { get; }

        /// <summary>玩家当前每秒回血（已含封顶）。业务侧据此判断"回血已封顶、不再提供该升级"。</summary>
        float PlayerRegen { get; }

        /// <summary>玩家当前炮塔回转速度（度/秒，已含封顶）。业务侧据此判断"回转已封顶、不再提供该升级"。</summary>
        float PlayerRotationSpeed { get; }

        /// <summary>炮塔当前朝向角（度，标准数学角：0 = +X、逆时针为正）。模拟内核已按回转速度逐帧转向目标，表现层据此画炮管指向。</summary>
        float TurretAngle { get; }

        /// <summary>累计击杀数。</summary>
        int Kills { get; }

        /// <summary>累计得分（按敌人原型的击杀分累加）。</summary>
        int Score { get; }

        /// <summary>当前存活敌人数。</summary>
        int EnemyCount { get; }

        /// <summary>按索引取存活敌人快照（0 ≤ index &lt; <see cref="EnemyCount"/>）。</summary>
        EnemySnapshot GetEnemy(int index);

        event Action<EnemySpawnedEvent> EnemySpawned;
        event Action<EnemyHitEvent> EnemyHit;

        /// <summary>炮塔击发一发（命中或空放都触发）。表现层据此画炮口闪光 / 曳光，与 <see cref="EnemyHit"/>（敌人反应）分离；高射速火墙中转向途中的空放 <see cref="TurretFiredEvent.Hit"/> = false。</summary>
        event Action<TurretFiredEvent> TurretFired;

        /// <summary>敌人抵达玩家并自爆（一次性伤害后即从存活列表移除）。取代了旧的"驻留逐拍攻击"模型。</summary>
        event Action<EnemyDetonatedEvent> EnemyDetonated;

        /// <summary>波次开始（参数 = 波次号，1 起）。Start 内会同步触发第 1 波。</summary>
        event Action<int> WaveStarted;

        /// <summary>本波清空、进入 <see cref="BattlePhase.WaveCleared"/>（参数 = 刚清空的波次号）。无限模式每波清空都触发，波间升级选完后 <see cref="BeginNextWave"/> 续下一波。</summary>
        event Action<int> WaveCleared;

        /// <summary>开始一局（只能调一次）：应用 setup、进入第 1 波。事件订阅应在此之前完成。</summary>
        void Start(BattleSetup setup);

        /// <summary>推进模拟。仅 <see cref="BattlePhase.WaveActive"/> 时有演算，其余阶段空操作。</summary>
        void Tick(float deltaTime);

        /// <summary>从 <see cref="BattlePhase.WaveCleared"/> 进入下一波（波间升级选完后由业务调用）。</summary>
        void BeginNextWave();

        /// <summary>应用玩家属性修正（升级）。任意非终态阶段可调，立即生效。</summary>
        void ApplyModifier(in PlayerModifier modifier);
    }
}
