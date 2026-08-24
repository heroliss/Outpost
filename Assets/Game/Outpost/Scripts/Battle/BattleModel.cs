using Game.Framework.Model;
using R3;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 战斗展示状态：把模拟内核（<see cref="Sim.IBattleSim"/>）的聚合值镜像成可订阅的 <c>RP</c>，供 HUD 只读绑定。
    /// 纯 C# Model，由 <see cref="BattleContext"/> 注册；只有 <see cref="BattleDirectorSystem"/> 写入（把 sim 快照/事件翻译进来），
    /// View 经查询 Command 读——写路径单一、读路径只读，模拟内核与视图完全解耦（后端置换 View 零改动）。
    /// </summary>
    public sealed class BattleModel : IModel
    {
        /// <summary>
        /// 战斗导演已完成配置、资源和模拟后端初始化，玩家命令可以被可靠接收。
        /// 状态进入不等于可交互：<c>BattleState</c> 只等待场景加载，本标志覆盖场景内异步 Setup 的剩余窗口。
        /// </summary>
        public readonly RP<bool> IsReady = new(false);

        public readonly RP<float> PlayerHp = new(0);
        public readonly RP<float> PlayerMaxHp = new(0);
        public readonly RP<int> Wave = new(0);
        public readonly RP<int> Kills = new(0);
        public readonly RP<int> Score = new(0);

        /// <summary>当前存活敌人数（性能行展示 + 两个 Sim 后端同题对比的规模指标）。</summary>
        public readonly RP<int> EnemyCount = new(0);

        /// <summary>当前在飞弹丸数（真弹道下弹×敌碰撞是模拟负载主项，性能行展示）。</summary>
        public readonly RP<int> ProjectileCount = new(0);

        /// <summary>本局累计击发弹丸总数（<c>long</c>——高射速长跑超 int；HUD 逗号分隔展示，与曳光弹里程碑同源）。</summary>
        public readonly RP<long> ShotsFired = new(0);

        /// <summary>战场留存残骸数（表现层统计：实例化渲染压力的主要持续来源，性能行展示）。</summary>
        public readonly RP<int> WreckCount = new(0);

        /// <summary>模拟内核单帧 Tick 耗时（毫秒，指数平滑）——后端对比的核心度量。</summary>
        public readonly RP<float> SimTickMs = new(0);

        /// <summary>当前模拟后端名（Reference / Ecs…），开局写入一次。</summary>
        public readonly RP<string> Backend = new("");
    }
}
