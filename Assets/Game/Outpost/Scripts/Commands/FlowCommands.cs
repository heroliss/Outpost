using Game.Framework.Command;
using Game.Framework.Flow;
using Game.Outpost.Flow;

namespace Game.Outpost.Commands
{
    // View 发起的阶段流转经 Command（写路径可被 CommandSystem 装饰器统一拦截 / 诊断），
    // 保持 View「外发只 ExecuteCommand」。战斗自身的终局流转由 BattleDirector（System 层）
    // 直接驱动 IGameFlow——System 不能 ExecuteCommand（防环），也不该把内部推进当"用户意图"。

    /// <summary>开始一局：→ 战斗。标题页「开始游戏」。</summary>
    public readonly struct StartBattleCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => FlowNav.Go(ctx.GetUtility<IGameFlow>(), new BattleState());
    }

    /// <summary>回标题。结算页「回标题」。</summary>
    public readonly struct GoToTitleCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => FlowNav.Go(ctx.GetUtility<IGameFlow>(), new TitleState());
    }
}
