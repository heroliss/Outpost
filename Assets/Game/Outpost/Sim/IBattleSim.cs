using System;
using System.Numerics;

namespace Game.Outpost.Sim
{
    /// <summary>战斗宏观阶段。终态（<see cref="Victory"/> / <see cref="Defeat"/>）后 Tick 变为空操作。</summary>
    public enum BattlePhase
    {
        /// <summary>尚未 Start。</summary>
        Idle,

        /// <summary>波次进行中（刷怪 / 移动 / 互相攻击）。</summary>
        WaveActive,

        /// <summary>本波清空、等待 <see cref="IBattleSim.BeginNextWave"/>（波间升级选择的停留点）。</summary>
        WaveCleared,

        Victory,
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

    /// <summary>敌人被玩家击中（伤害飘字 / 击杀回收的驱动源；<see cref="Killed"/> 为 true 时该敌人已从存活列表移除）。</summary>
    public readonly struct EnemyHitEvent
    {
        public readonly int EnemyId;
        public readonly int ArchetypeId;
        public readonly Vector2 Position;
        public readonly float Damage;
        public readonly bool Killed;

        public EnemyHitEvent(int enemyId, int archetypeId, Vector2 position, float damage, bool killed)
        {
            EnemyId = enemyId;
            ArchetypeId = archetypeId;
            Position = position;
            Damage = damage;
            Killed = killed;
        }
    }

    /// <summary>玩家被敌人击中（表现层据 <see cref="EnemyId"/> 让该敌人猛扑演出、<see cref="Position"/> 定位啃咬特效）。</summary>
    public readonly struct PlayerHitEvent
    {
        /// <summary>发动这次攻击的敌人实例 id（对应存活列表里的 <see cref="EnemySnapshot.Id"/>）。</summary>
        public readonly int EnemyId;

        /// <summary>攻击者当前位置（贴近玩家的接触点附近）。</summary>
        public readonly Vector2 Position;

        public readonly float Damage;

        /// <summary>受击后的剩余血量（已扣减，不会为负）。</summary>
        public readonly float HpAfter;

        public PlayerHitEvent(int enemyId, Vector2 position, float damage, float hpAfter)
        {
            EnemyId = enemyId;
            Position = position;
            Damage = damage;
            HpAfter = hpAfter;
        }
    }

    /// <summary>
    /// 战斗模拟接缝：一局自动战斗的全部规则演算（刷怪 / 索敌 / 移动 / 伤害 / 波次 / 胜负）。
    /// 纯 C# 契约、零引擎与框架依赖——表现（GameObject / ECS 渲染）、数据（配置表）、编排（Model / Flow）都在接缝外。
    /// 参考实现 <see cref="ReferenceBattleSim"/>；后续可整体置换为 ECS 后端而消费方零改动（ports &amp; adapters）。
    /// </summary>
    /// <remarks>
    /// <b>驱动契约</b>：单线程使用；调用方按帧 <see cref="Tick"/>，事件在 Tick / Start 调用栈内同步触发——
    /// 事件回调里只做读取与外发，<b>不要</b>回调内再调本接口的写方法（Start / Tick / BeginNextWave / ApplyModifier）。<br/>
    /// <b>聚合读取</b>：属性在两次 Tick 之间保持稳定；存活敌人经 <see cref="EnemyCount"/> + <see cref="GetEnemy"/>
    /// 按索引零分配遍历（索引顺序会因移除而变化，跨帧跟踪用 <see cref="EnemySnapshot.Id"/>）。<br/>
    /// <b>Dispose</b>：参考实现无资源可释放；ECS 后端会持有 World，消费方按 IDisposable 统一管理。
    /// </remarks>
    public interface IBattleSim : IDisposable
    {
        BattlePhase Phase { get; }

        /// <summary>当前波次（1 起；Start 前为 0）。</summary>
        int WaveIndex { get; }

        int WaveCount { get; }

        float PlayerHp { get; }

        float PlayerMaxHp { get; }

        /// <summary>玩家当前索敌半径（升级会改变）。表现层据此绘制射程圈。</summary>
        float PlayerRange { get; }

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
        event Action<PlayerHitEvent> PlayerHit;

        /// <summary>波次开始（参数 = 波次号，1 起）。Start 内会同步触发第 1 波。</summary>
        event Action<int> WaveStarted;

        /// <summary>非最后一波清空、进入 <see cref="BattlePhase.WaveCleared"/>（参数 = 刚清空的波次号）。最后一波清空直接进 Victory、不触发本事件。</summary>
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
