using System;
using Cysharp.Threading.Tasks;
using Game.Framework.Flow;
using UnityEngine;

namespace Game.Outpost.Flow
{
    /// <summary>
    /// <c>GoTo</c> 的 fire-and-forget 收口。UI 导航不需要等转换完成，但三种结局不能无声丢弃：
    /// 完成正常返回；被更新的 GoTo 顶替（最新意图胜）静默吞掉；OnEnter 失败必须落日志——
    /// 否则游戏卡在旧阶段却没有任何线索。
    /// </summary>
    public static class FlowNav
    {
        public static void Go(IGameFlow flow, FlowState next)
        {
            GoAsync(flow, next).Forget();

            static async UniTaskVoid GoAsync(IGameFlow flow, FlowState next)
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
                    Debug.LogError($"[OutpostFlow] 进入「{next}」失败：{e}");
                }
            }
        }
    }
}
