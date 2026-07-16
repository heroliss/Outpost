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

        /// <summary>该实例的随机体型系数（出生时取，一生不变）。表现层用它缩放渲染体型；生命/碰撞半径已按它放大。</summary>
        public readonly float SizeScale;

        public EnemySnapshot(int id, int archetypeId, Vector2 position, float hp, float maxHp, float sizeScale)
        {
            Id = id;
            ArchetypeId = archetypeId;
            Position = position;
            Hp = hp;
            MaxHp = maxHp;
            SizeScale = sizeScale;
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
    /// 敌人被弹丸击中（在<b>弹着帧</b>触发，<see cref="Position"/> 为弹着点——弹着特效 / 音效天然同帧同点；
    /// <see cref="Killed"/> 为 true 时该敌人已从存活列表移除）。
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

        /// <summary>被击中敌人的随机体型系数（表现层用它把击毁爆炸/碎片按体型放大——巨怪炸得更大一圈）。</summary>
        public readonly float SizeScale;

        public EnemyHitEvent(int enemyId, int archetypeId, Vector2 position, float damage, bool killed, float splashDamage = 0f, float sizeScale = 1f)
        {
            EnemyId = enemyId;
            ArchetypeId = archetypeId;
            Position = position;
            Damage = damage;
            Killed = killed;
            SplashDamage = splashDamage;
            SizeScale = sizeScale;
        }
    }

    /// <summary>
    /// 炮塔击发一发（在<b>击发帧</b>触发——炮口闪光 / 后坐 / 火力热度的驱动源）。与"敌人被击中"
    /// (<see cref="EnemyHitEvent"/>，在<b>弹着帧</b>触发) 刻意分离：击发产生真实弹丸沿 <see cref="Direction"/>
    /// 直飞，命不命中、命中谁由物理决定（扫掠碰撞）——本事件不再携带命中信息。
    /// </summary>
    public readonly struct TurretFiredEvent
    {
        /// <summary>这一发弹丸的初始飞行方向（单位向量；火墙散布已计入）。</summary>
        public readonly Vector2 Direction;

        public TurretFiredEvent(Vector2 direction) => Direction = direction;
    }

    /// <summary>在飞弹丸的只读快照（表现层逐帧实例化绘制的读源；索引顺序不稳定，与 <see cref="EnemySnapshot"/> 同语义）。</summary>
    public readonly struct ProjectileSnapshot
    {
        public readonly Vector2 Position;

        /// <summary>飞行方向（单位向量，表现层据此定弹丸朝向）。</summary>
        public readonly Vector2 Direction;

        /// <summary>
        /// 曳光弹档位：0 = 普通弹，1/2/3 = 第 10/100/1000 发的里程碑曳光弹（表现层据此换色/放大，
        /// 直观展示已射出的子弹量）。击发时按<b>累计发数</b>确定、随弹丸一生不变。
        /// </summary>
        public readonly byte Tracer;

        public ProjectileSnapshot(Vector2 position, Vector2 direction, byte tracer)
        {
            Position = position;
            Direction = direction;
            Tracer = tracer;
        }
    }

    /// <summary>
    /// 一具残骸的只读快照（按<b>环形槽位</b>读取，槽位号在 0 ≤ slot &lt; <see cref="IBattleSim.WreckSlotCount"/>）。
    /// 槽位是稳定地址：同一具残骸一直住在同一槽，直到环形上限把它复写——<see cref="Seq"/>（创建序号，全局单调递增）
    /// 变化即"换血"信号，表现层据此增量镜像：Seq 变 = 新残骸落定，<see cref="Position"/> 变 = 被敌人推挤犁动。
    /// </summary>
    public readonly struct WreckSnapshot
    {
        /// <summary>创建序号（&gt; 0，全局单调递增）。同槽位序号变化 = 旧残骸被环形复写。</summary>
        public readonly int Seq;

        /// <summary>原型 id（对应 <see cref="EnemyArchetype.Id"/>，表现层用它选网格 / 颜色 / 体型）。</summary>
        public readonly int ArchetypeId;

        /// <summary>当前位置（静置点起步，会被推挤逐帧改变；密度记账始终跟随本值所在格）。</summary>
        public readonly Vector2 Position;

        public WreckSnapshot(int seq, int archetypeId, Vector2 position)
        {
            Seq = seq;
            ArchetypeId = archetypeId;
            Position = position;
        }
    }

    /// <summary>
    /// 泥地密度格的网格布局（可视化 / 调试的只读元数据；<see cref="Dim"/> = 0 表示泥地机制关闭）。
    /// 格计数经 <see cref="IBattleSim.GetWreckCellCount"/> 按索引读取（index = iy × Dim + ix）。
    /// </summary>
    public readonly struct WreckGridInfo
    {
        /// <summary>网格一边的格数（正方形 Dim×Dim；0 = 泥地机制关闭）。</summary>
        public readonly int Dim;

        /// <summary>格边长（世界单位）。</summary>
        public readonly float CellSize;

        /// <summary>网格覆盖的半宽：坐标 (x,y) 落格按 floor((x+Half)/CellSize) 取整、越界钳到边缘格（与 <see cref="SimMath.WreckCellIndex"/> 同式）。</summary>
        public readonly float Half;

        public WreckGridInfo(int dim, float cellSize, float half)
        {
            Dim = dim;
            CellSize = cellSize;
            Half = half;
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

        /// <summary>自爆敌人的随机体型系数（表现层用它把自爆爆炸/碎片按体型放大）。</summary>
        public readonly float SizeScale;

        public EnemyDetonatedEvent(int enemyId, int archetypeId, Vector2 position, float damage, float hpAfter, float sizeScale = 1f)
        {
            EnemyId = enemyId;
            ArchetypeId = archetypeId;
            Position = position;
            Damage = damage;
            HpAfter = hpAfter;
            SizeScale = sizeScale;
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
    /// <b>开火模型（真弹道）</b>：炮塔按 <c>PlayerSetup.RotationSpeed</c> 逐帧转向最近目标，对准（角差 ≤ 容差）后按有效射速击发；
    /// 火墙（间隔低于门槛）时转向途中也持续击发并带小角度确定性散布。每次击发产生一颗<b>真实弹丸</b>沿炮口方向直飞
    /// （<see cref="TurretFired"/> 在击发帧触发）；命中为<b>扫掠线段碰撞</b>——弹丸本 tick 位移段与敌圆求交、取最早交点，
    /// <see cref="EnemyHit"/> 在<b>弹着帧</b>触发、位置为弹着点；未命中的弹飞到消散半径才消失（途中仍可命中射程外敌人）。
    /// 同帧多个自爆按实例 id 升序结算（后端间事件流可复现的契约之一）。弹丸经 <see cref="ProjectileCount"/> +
    /// <see cref="GetProjectile"/> 零分配遍历；不 Tick 的阶段（波间抉择等）弹丸随全场冻结、续波后继续飞。<br/>
    /// <b>残骸减速泥地 + 推挤</b>：残骸是<b>逐实体模拟状态</b>（环形槽位，经 <see cref="WreckSlotCount"/> +
    /// <see cref="GetWreckSlot"/> 读取，表现层直接镜像绘制）——击杀/自爆在事件点附近静置一具（确定性散布），
    /// 敌人移速按所在密度格残骸数打折；敌人还会把重叠的残骸<b>拱开</b>（每具残骸被最近重叠敌人推离、
    /// 密度记账跟随位置），车辙被踩穿、路边堆垄，参数见 <see cref="WreckFieldSetup"/>。
    /// 推挤是顺序无关的归约（最近敌 + id 平票），两后端可逐位对拍。<br/>
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

        /// <summary>本局累计击发的弹丸总数（含未命中）。<see cref="long"/>——高射速长跑会超 int 范围；表现层逗号分隔展示 + 曳光弹里程碑判定。</summary>
        long TotalShotsFired { get; }

        /// <summary>当前存活敌人数。</summary>
        int EnemyCount { get; }

        /// <summary>按索引取存活敌人快照（0 ≤ index &lt; <see cref="EnemyCount"/>）。</summary>
        EnemySnapshot GetEnemy(int index);

        /// <summary>当前在飞弹丸数（性能行展示 + 表现层遍历上界）。</summary>
        int ProjectileCount { get; }

        /// <summary>按索引取在飞弹丸快照（0 ≤ index &lt; <see cref="ProjectileCount"/>；索引顺序不稳定）。</summary>
        ProjectileSnapshot GetProjectile(int index);

        /// <summary>泥地密度格布局（<c>Dim</c> = 0 表示机制关闭）。一局内布局不变，可开局缓存。</summary>
        WreckGridInfo WreckGrid { get; }

        /// <summary>按格索引取当前残骸计数（0 ≤ index &lt; Dim×Dim，O(1)）——泥地热力图可视化的数据源。</summary>
        int GetWreckCellCount(int index);

        /// <summary>已使用的残骸槽位数（≤ <c>WreckFieldSetup.SimCap</c>；环形写满后恒等于上限）。</summary>
        int WreckSlotCount { get; }

        /// <summary>按环形槽位取残骸快照（0 ≤ slot &lt; <see cref="WreckSlotCount"/>）——表现层增量镜像的读源，语义见 <see cref="WreckSnapshot"/>。</summary>
        WreckSnapshot GetWreckSlot(int slot);

        event Action<EnemySpawnedEvent> EnemySpawned;
        event Action<EnemyHitEvent> EnemyHit;

        /// <summary>炮塔击发一发（击发帧；弹丸已生成）。表现层据此画炮口闪光 / 驱动火力热度；弹着反应见 <see cref="EnemyHit"/>。</summary>
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
