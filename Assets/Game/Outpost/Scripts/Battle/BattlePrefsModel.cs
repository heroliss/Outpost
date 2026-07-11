using Game.Framework.Model;
using R3;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 跨局的战斗偏好（当前只有模拟后端选择），注册在根 OutpostContext。
    /// <b>运行时真源</b>：设置窗经 <see cref="SetBattleBackendCommand"/> 写入、
    /// <see cref="BattleDirectorSystem"/> 每局开局采样一次（局中改动下一局生效——模拟是一次性实例，不做热切）。
    /// 持久化走 <c>OutpostSettings</c> 快照（音量/语言同一心智：真源在运行时对象、存档只是落盘快照），
    /// 由 Load/SaveSettingsCommand 回灌与收集。
    /// </summary>
    public sealed class BattlePrefsModel : IModel
    {
        /// <summary>战斗模拟后端。默认 <see cref="BattleSimBackend.Ecs"/>（产品默认，与战斗场景序列化值一致）。</summary>
        public readonly RP<BattleSimBackend> Backend = new(BattleSimBackend.Ecs);
    }
}
