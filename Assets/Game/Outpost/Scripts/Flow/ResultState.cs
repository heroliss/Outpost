using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Framework.Network;
using Game.Framework.UI;
using Game.Outpost.Net;
using Game.Outpost.Save;
using Game.Outpost.Windows;
using R3;
using UnityEngine;

namespace Game.Outpost.Flow
{
    /// <summary>结算阶段：把本局成绩并入历史存档后展示。结果走构造参数——一次性实例天然无跨局脏状态。</summary>
    public sealed class ResultState : FlowState
    {
        private readonly BattleResult _result;

        public ResultState(BattleResult result) => _result = result;

        public override string ToString() => $"结算(第{_result.Wave}波 {_result.Score}分)";

        protected override async UniTask OnEnter(CancellationToken ct)
        {
            // 先把成绩并入历史存档（await 落盘，§26），拿到是否刷新最高分；再开结算页展示（含"新纪录"提示）。
            bool newBest = await Context.ExecuteCommandAsync(new SubmitRunResultCommand(_result), ct);

            // 再上传排行榜拿全服名次（M4，仅 dev 环境有对端）。失败只降级掉名次行——离线也要能看结算，
            // 名次是装饰不是门槛（§32：非 2xx / 连不上都折叠在 NetworkException 里，这里统一兜）。
            int serverRank = 0;
            if (OutpostNet.Available)
            {
                try
                {
                    serverRank = (await Context.ExecuteCommandAsync(new SubmitScoreCommand(_result), ct)).Rank;
                }
                catch (NetworkException e)
                {
                    Debug.LogWarning($"[ResultState] 上传成绩失败（{e.Kind}），结算不展示全服名次：{e.Message}");
                }
            }

            var ui = Context.GetUtility<IUIUtility>();
            await ui.Open<ResultWindow>(new ResultArgs(_result, newBest, serverRank), ct);
            Bag.Add(Disposable.Create(() => ui.Close<ResultWindow>()));
        }
    }
}
