using Game.Framework.Command;
using Game.Framework.Model;
using Game.Framework.Systems;
using R3;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 主动撤离：立即收束本局、按当前战绩进结算。转交 <see cref="BattleDirectorSystem.Retreat"/>——
    /// 全成长封顶 + 波间维修的稳态下失守可能永不发生，撤离是一局的<b>常规结束方式</b>（把分数落袋）。
    /// 同其余写意图走「View → 命令 → System」一跳（View 不能直调 System）。
    /// </summary>
    public readonly struct RetreatCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetSystem<BattleDirectorSystem>().Retreat();
    }

    /// <summary>
    /// 选择战斗模拟后端（写入 <see cref="BattlePrefsModel"/>，<b>下一局开局生效</b>——模拟是一次性实例、不做局中热切）。
    /// 设置窗经此命令写偏好（View 不写 Model），落盘随关窗的设置快照。
    /// </summary>
    public readonly struct SetBattleBackendCommand : ICommand
    {
        public readonly BattleSimBackend Backend;

        public SetBattleBackendCommand(BattleSimBackend backend) => Backend = backend;

        public void Execute(ICommandContext ctx) => ctx.GetModel<BattlePrefsModel>().Backend.Value = Backend;
    }

    /// <summary>当前战斗后端偏好的只读订阅源（设置窗高亮回显用；View 读状态走查询命令，§1.1）。</summary>
    public readonly struct GetBattleBackendCommand : ICommand<ReadOnlyReactiveProperty<BattleSimBackend>>
    {
        public ReadOnlyReactiveProperty<BattleSimBackend> Execute(ICommandContext ctx)
            => ctx.GetModel<BattlePrefsModel>().Backend;
    }

    /// <summary>
    /// 开关泥地热力图（写入 <see cref="BattlePrefsModel"/>；纯表现开关，<b>即时生效</b>——
    /// 战斗导演订阅该偏好直通渲染层）。落盘随关窗的设置快照。
    /// </summary>
    public readonly struct SetWreckHeatmapCommand : ICommand
    {
        public readonly bool Show;

        public SetWreckHeatmapCommand(bool show) => Show = show;

        public void Execute(ICommandContext ctx) => ctx.GetModel<BattlePrefsModel>().ShowWreckHeatmap.Value = Show;
    }

    /// <summary>泥地热力图开关状态的只读订阅源（HUD 按钮回显用）。</summary>
    public readonly struct GetWreckHeatmapCommand : ICommand<ReadOnlyReactiveProperty<bool>>
    {
        public ReadOnlyReactiveProperty<bool> Execute(ICommandContext ctx)
            => ctx.GetModel<BattlePrefsModel>().ShowWreckHeatmap;
    }

    /// <summary>
    /// 设置游戏速度倍率（写 <see cref="BattlePrefsModel"/>；导演订阅它写 <c>Time.timeScale</c>，<b>即时生效</b>）。
    /// HUD 速度按钮经此命令写；不落盘（会话内跨局保持、重启回 1×）。
    /// </summary>
    public readonly struct SetSimSpeedCommand : ICommand
    {
        public readonly float Speed;

        public SetSimSpeedCommand(float speed) => Speed = speed;

        public void Execute(ICommandContext ctx) => ctx.GetModel<BattlePrefsModel>().SimSpeed.Value = Speed;
    }

    /// <summary>游戏速度倍率的只读订阅源（HUD 速度按钮回显当前倍率用）。</summary>
    public readonly struct GetSimSpeedCommand : ICommand<ReadOnlyReactiveProperty<float>>
    {
        public ReadOnlyReactiveProperty<float> Execute(ICommandContext ctx)
            => ctx.GetModel<BattlePrefsModel>().SimSpeed;
    }
}
