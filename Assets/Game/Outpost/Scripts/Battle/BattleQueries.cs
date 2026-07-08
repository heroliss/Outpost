using Game.Framework.Command;
using R3;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 战斗 HUD 的只读读模型：把 <see cref="BattleModel"/> 的响应式状态打成一束只读视图，供 View 一次拿齐、逐个订阅。
    /// 每个字段是 <c>ReadOnlyReactiveProperty</c>（<c>RP</c> 的只读面）——View 看得到、改不了，写只能走 Command。
    /// <para>把 6 个值合成一个查询而非 6 个查询命令，是对"数据密集 HUD 逐值查询样板过多"的折中——
    /// 作为切片接缝观察记录：框架是否该提供官方"读模型束"模式，留待 ADR-0029。</para>
    /// </summary>
    public readonly struct BattleReadModel
    {
        public readonly ReadOnlyReactiveProperty<float> Hp;
        public readonly ReadOnlyReactiveProperty<float> MaxHp;
        public readonly ReadOnlyReactiveProperty<int> Wave;
        public readonly ReadOnlyReactiveProperty<int> Kills;
        public readonly ReadOnlyReactiveProperty<int> Score;

        public BattleReadModel(BattleModel m)
        {
            Hp = m.PlayerHp;
            MaxHp = m.PlayerMaxHp;
            Wave = m.Wave;
            Kills = m.Kills;
            Score = m.Score;
        }
    }

    /// <summary>只读查询：返回战斗读模型（各值的只读响应流），供 HUD 订阅（订阅即得当前值）。</summary>
    public readonly struct GetBattleReadModelCommand : ICommand<BattleReadModel>
    {
        public BattleReadModel Execute(ICommandContext ctx) => new(ctx.GetModel<BattleModel>());
    }
}
