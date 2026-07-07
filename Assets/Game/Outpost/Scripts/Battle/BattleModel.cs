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
        public readonly RP<float> PlayerHp = new(0);
        public readonly RP<float> PlayerMaxHp = new(0);
        public readonly RP<int> Wave = new(0);
        public readonly RP<int> WaveCount = new(0);
        public readonly RP<int> Kills = new(0);
        public readonly RP<int> Score = new(0);
    }
}
