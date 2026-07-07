using Game.Framework.Command;
using Game.Framework.Systems;
using ObservableCollections;
using R3;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 升级面板的只读读模型：三选一候选集合 + 是否正在抉择，打成一束供 View 一次拿齐、各自订阅。
    /// 集合暴露为 <see cref="IReadOnlyObservableList{T}"/>（增量可观察、只读）、开关暴露为 <c>ReadOnlyReactiveProperty</c>——
    /// View 看得到、改不了，写只能走 <see cref="ChooseUpgradeCommand"/>。同 <see cref="BattleReadModel"/> 的束模式。
    /// </summary>
    public readonly struct UpgradeChoiceReadModel
    {
        public readonly IReadOnlyObservableList<UpgradeOption> Choices;
        public readonly ReadOnlyReactiveProperty<bool> IsChoosing;

        public UpgradeChoiceReadModel(UpgradeModel m)
        {
            Choices = m.Choices;
            IsChoosing = m.IsChoosing;
        }
    }

    /// <summary>只读查询：升级抉择读模型（供升级面板订阅；订阅即得当前值）。</summary>
    public readonly struct GetUpgradeChoiceCommand : ICommand<UpgradeChoiceReadModel>
    {
        public UpgradeChoiceReadModel Execute(ICommandContext ctx) => new(ctx.GetModel<UpgradeModel>());
    }

    /// <summary>
    /// 玩家选定一个升级：转交 <see cref="BattleDirector"/> 应用到模拟（<c>ApplyModifier</c>）并推进下一波。
    /// 命令自身不碰模拟/表现，只做「意图 → 导演」的一跳——导演是 System、View 不能直接调 System，故经命令中转。
    /// </summary>
    public readonly struct ChooseUpgradeCommand : ICommand
    {
        private readonly int _upgradeId;

        public ChooseUpgradeCommand(int upgradeId) => _upgradeId = upgradeId;

        public void Execute(ICommandContext ctx) => ctx.GetSystem<BattleDirector>().ChooseUpgrade(_upgradeId);
    }
}
