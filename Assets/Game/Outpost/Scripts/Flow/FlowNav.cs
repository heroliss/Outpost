using System;
using Cysharp.Threading.Tasks;
using Game.Framework.Flow;
using Game.Framework.Logging;

namespace Game.Outpost.Flow
{
    /// <summary>
    /// <c>GoTo</c> 的 fire-and-forget 结果观察 Adapter。同步生命周期入口、System 内部推进和
    /// <see cref="FlowState.OnEnter"/> 内转向不一定能等待转换完成，但三种结局不能无声丢弃：
    /// 完成正常返回；被更新的 GoTo 顶替（最新意图胜）静默吞掉；OnEnter 失败必须落日志——
    /// 否则游戏卡在旧阶段却没有任何线索。
    /// </summary>
    public static class FlowNav
    {
        /// <summary>
        /// 请求一次流程转换并持续观察到终态。这里只收口 Outpost 的日志策略，不实现转换、排队或状态所有权；
        /// 真正的流程语义仍全部属于 <see cref="IGameFlow.GoTo"/>。
        /// </summary>
        public static void Request(IGameFlow flow, FlowState next)
        {
            Observe(flow, next).Forget();

            static async UniTask Observe(IGameFlow flow, FlowState next)
            {
                try
                {
                    await flow.GoTo(next);
                }
                catch (OperationCanceledException)
                {
                    // 被更新的 GoTo 顶替或宿主销毁——框架的正常转换语义，无需上报。
                }
                catch (Exception e)
                {
                    Log.Error($"进入流程状态 '{next}' 失败。", e, "OutpostFlow");
                }
            }
        }
    }
}
