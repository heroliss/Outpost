using Game.Framework.Model;
using R3;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 跨局的战斗偏好（模拟后端选择 + 泥地热力图开关 + 游戏速度），注册在根 OutpostContext。
    /// <b>运行时真源</b>：后端在设置窗改（<see cref="SetBattleBackendCommand"/>）、热力图与速度在战斗 HUD 改
    /// （<see cref="SetWreckHeatmapCommand"/> / <see cref="SetSimSpeedCommand"/>）。后端由 <see cref="BattleDirectorSystem"/>
    /// 每局开局采样一次（局中改动下一局生效——模拟是一次性实例，不做热切）；热力图与速度是纯表现、订阅即时生效。
    /// 后端 / 热力图持久化走 <c>OutpostSettings</c> 快照（真源在运行时对象、存档只是落盘快照），由 Load/SaveSettingsCommand 回灌与收集；
    /// 速度是临时演示旋钮、<b>不落盘</b>（会话内跨局保持、重启回 1×）。
    /// </summary>
    public sealed class BattlePrefsModel : IModel
    {
        /// <summary>战斗模拟后端。默认 <see cref="BattleSimBackend.Ecs"/>（产品默认，与战斗场景序列化值一致）。</summary>
        public readonly RP<BattleSimBackend> Backend = new(BattleSimBackend.Ecs);

        /// <summary>泥地热力图开关（显示模拟侧残骸密度格；纯表现，切换即时生效）。默认关——教学/调试视图不打扰常规游玩。</summary>
        public readonly RP<bool> ShowWreckHeatmap = new(false);

        /// <summary>
        /// 游戏速度倍率（<see cref="BattleDirectorSystem"/> 订阅它写 <c>Time.timeScale</c> 实时缩放整场——
        /// 弹丸/敌人/特效一起变速：慢放看清扫掠碰撞、快进看规模。默认 1×；离开战斗导演还原 <c>Time.timeScale = 1</c>。
        /// </summary>
        public readonly RP<float> SimSpeed = new(1f);

        /// <summary>
        /// 战斗 BGM 是否用扩展包变体「增援电台」（未安装扩展包时本开关无效、自动回落默认曲）。
        /// 默认开 = 与"下载即启用"的旧行为一致；设置窗可切，<c>OutpostAudioSystem</c> 订阅后即时换曲（交叉淡变）。
        /// 持久化走 <c>OutpostSettings.ExpansionBgm</c> 快照。
        /// </summary>
        public readonly RP<bool> ExpansionBgm = new(true);
    }
}
