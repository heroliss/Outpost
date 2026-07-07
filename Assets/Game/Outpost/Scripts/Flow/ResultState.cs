using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Framework.UI;
using Game.Outpost.Save;
using Game.Outpost.Windows;
using R3;

namespace Game.Outpost.Flow
{
    /// <summary>结算阶段：把本局成绩并入历史存档后展示。结果走构造参数——一次性实例天然无跨局脏状态。</summary>
    public sealed class ResultState : FlowState
    {
        private readonly BattleResult _result;

        public ResultState(BattleResult result) => _result = result;

        public override string ToString() => $"结算({(_result.Victory ? "胜" : "败")} {_result.Score}分)";

        protected override async UniTask OnEnter(CancellationToken ct)
        {
            // 先把成绩并入历史存档（await 落盘，§26），拿到是否刷新最高分；再开结算页展示（含"新纪录"提示）。
            bool newBest = await Context.ExecuteCommandAsync(new SubmitRunResultCommand(_result), ct);

            var ui = Context.GetUtility<IUIUtility>();
            await ui.Open<ResultWindow>(new ResultArgs(_result, newBest), ct);
            Bag.Add(Disposable.Create(() => ui.Close<ResultWindow>()));
        }
    }
}
