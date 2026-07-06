using Game.Framework.Command;
using Game.Framework.Flow;
using Game.Outpost.Flow;

namespace Game.Outpost.Commands
{
    // 阶段流转统一经 Command 而不是 View 直接 GoTo：写路径全部可被 CommandSystem 装饰器
    // 统一拦截（诊断面板命令流水），View 保持「外发只 ExecuteCommand」的读写分离。

    /// <summary>开始一局：→ 战斗。</summary>
    public readonly struct StartBattleCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => FlowNav.Go(ctx.GetUtility<IGameFlow>(), new BattleState());
    }

    /// <summary>结束战斗进结算。M0 分数由按钮占位传入；M1 起来自战斗 Model。</summary>
    public readonly struct EndBattleCommand : ICommand
    {
        public readonly int Score;

        public EndBattleCommand(int score) => Score = score;

        public void Execute(ICommandContext ctx) => FlowNav.Go(ctx.GetUtility<IGameFlow>(), new ResultState(Score));
    }

    /// <summary>回标题。</summary>
    public readonly struct GoToTitleCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => FlowNav.Go(ctx.GetUtility<IGameFlow>(), new TitleState());
    }
}
