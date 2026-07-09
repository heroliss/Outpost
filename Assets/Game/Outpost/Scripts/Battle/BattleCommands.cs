using Game.Framework.Command;
using Game.Framework.Systems;

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
}
