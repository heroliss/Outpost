using Game.Framework.Model;
using R3;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 跨局的战斗偏好（模拟后端选择 + 泥地热力图开关），注册在根 OutpostContext。
    /// <b>运行时真源</b>：设置窗经命令写入（<see cref="SetBattleBackendCommand"/> / <see cref="SetWreckHeatmapCommand"/>）；
    /// 后端由 <see cref="BattleDirectorSystem"/> 每局开局采样一次（局中改动下一局生效——模拟是一次性实例，不做热切），
    /// 热力图是纯表现、订阅即时生效。持久化走 <c>OutpostSettings</c> 快照（音量/语言同一心智：
    /// 真源在运行时对象、存档只是落盘快照），由 Load/SaveSettingsCommand 回灌与收集。
    /// </summary>
    public sealed class BattlePrefsModel : IModel
    {
        /// <summary>战斗模拟后端。默认 <see cref="BattleSimBackend.Ecs"/>（产品默认，与战斗场景序列化值一致）。</summary>
        public readonly RP<BattleSimBackend> Backend = new(BattleSimBackend.Ecs);

        /// <summary>泥地热力图开关（显示模拟侧残骸密度格；纯表现，切换即时生效）。默认关——教学/调试视图不打扰常规游玩。</summary>
        public readonly RP<bool> ShowWreckHeatmap = new(false);
    }
}
