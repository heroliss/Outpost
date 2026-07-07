using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Framework.UI;
using Game.Outpost.Windows;
using R3;

namespace Game.Outpost.Flow
{
    /// <summary>结算阶段：展示本局成绩。结果走构造参数——一次性实例天然无跨局脏状态。</summary>
    public sealed class ResultState : FlowState
    {
        private readonly BattleResult _result;

        public ResultState(BattleResult result) => _result = result;

        public override string ToString() => $"结算({(_result.Victory ? "胜" : "败")} {_result.Score}分)";

        protected override async UniTask OnEnter(CancellationToken ct)
        {
            var ui = Context.GetUtility<IUIUtility>();
            await ui.Open<ResultWindow>(_result, ct);
            Bag.Add(Disposable.Create(() => ui.Close<ResultWindow>()));
        }
    }
}
