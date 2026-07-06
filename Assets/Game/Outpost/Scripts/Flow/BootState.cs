using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Flow;

namespace Game.Outpost.Flow
{
    /// <summary>
    /// 启动阶段：基础设施就绪后进标题。
    /// M0 窗口全部纯代码搭建、无资源可等，直接流转；M1 起在这里 await 资源包初始化与配置表就绪。
    /// </summary>
    public sealed class BootState : FlowState
    {
        public override string ToString() => "启动";

        protected override UniTask OnEnter(CancellationToken ct)
        {
            // OnEnter 里转向别处：调 GoTo 后直接 return、不要 await 它（互等死锁，guide §20）。
            FlowNav.Go(Context.GetUtility<IGameFlow>(), new TitleState());
            return UniTask.CompletedTask;
        }
    }
}
