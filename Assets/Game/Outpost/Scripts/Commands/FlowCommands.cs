using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Flow;
using Game.Outpost.Flow;

namespace Game.Outpost.Commands
{
    // View 发起的阶段流转经异步 Command（写路径可被 CommandSystem 装饰器统一拦截 / 诊断），
    // 由 Command 直接观察 GoTo 的终态；View 退出只取消它自己的反馈，不撤回已经交给全局 Flow 的业务意图。
    // BattleDirectorSystem 等同步 System 入口不能 ExecuteCommand / await，才使用项目 FlowNav Adapter 收口结果。

    /// <summary>开始一局：→ 战斗。标题页「开始游戏」。</summary>
    public readonly struct StartBattleCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ctx.GetSystem<IGameFlow>().GoTo(new BattleState());
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>回标题。结算页「回标题」。</summary>
    public readonly struct GoToTitleCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ctx.GetSystem<IGameFlow>().GoTo(new TitleState());
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
