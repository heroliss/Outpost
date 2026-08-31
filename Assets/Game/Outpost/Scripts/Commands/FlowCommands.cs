using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Flow;
using Game.Framework.Logging;
using Game.Outpost.Flow;

namespace Game.Outpost.Commands
{
    // View 发起的阶段流转经异步 Command（写路径可被 CommandSystem 装饰器统一拦截 / 诊断），
    // 由 Command 直接观察 GoTo 的终态；View token 只在提交前阻止陈旧点击。一旦 GoTo 接受意图，Command
    // 就持续观察真实流程结局，不会因发起它的旧 View 随状态退出而把成功导航误报成“命令已取消”。
    // BattleDirectorSystem 等同步 System 入口不能 ExecuteCommand / await，才使用项目 FlowNav Adapter 收口结果。

    /// <summary>开始一局：→ 战斗。标题页「开始游戏」。</summary>
    public readonly struct StartBattleCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var flow = ctx.GetSystem<IGameFlow>();
            try
            {
                await flow.GoTo(new BattleState());
            }
            catch (OperationCanceledException)
            {
                // 最新意图顶替或宿主释放已有明确 owner，不得用旧点击擅自恢复标题。
                throw;
            }
            catch (Exception startupError)
            {
                // BattleState 的主页面不变量是“场景 + director 已可交互”。真实启动失败时 GameFlow
                // 会稳定落到无状态；仅在仍无其它导航接手时恢复标题，避免玩家留在空场景/黑屏。
                // 恢复成功也继续抛原错误，让 Command 流水准确记录“开始战斗失败”而非假成功。
                var captured = ExceptionDispatchInfo.Capture(startupError);
                if (flow.Current == null && !flow.IsTransitioning)
                {
                    try
                    {
                        await flow.GoTo(new TitleState());
                    }
                    catch (OperationCanceledException)
                    {
                        // 恢复被更新意图顶替，说明已有更晚导航接手。
                    }
                    catch (Exception recoveryError)
                    {
                        Log.Error("战斗启动失败后恢复标题页也失败。", recoveryError, "OutpostFlow");
                    }
                }
                captured.Throw();
                throw;
            }
        }
    }

    /// <summary>回标题。结算页「回标题」。</summary>
    public readonly struct GoToTitleCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ctx.GetSystem<IGameFlow>().GoTo(new TitleState());
        }
    }
}
