using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Network;
using Game.Outpost.Flow;
using Game.Outpost.Save;

namespace Game.Outpost.Net
{
    /// <summary>
    /// 拉取全服排行榜 Top N（GET <c>api/leaderboard</c>）。失败按 §32 语义抛
    /// <see cref="NetworkException"/>——调用方（排行榜窗）自己决定怎么呈现错误与重试。
    /// </summary>
    public readonly struct FetchLeaderboardCommand : IAsyncCommand<LeaderboardResponse>
    {
        private readonly int _count;

        public FetchLeaderboardCommand(int count) => _count = count;

        public UniTask<LeaderboardResponse> ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
            => ctx.GetUtility<IHttpUtility>()
                .Get<LeaderboardResponse>(OutpostNet.LeaderboardPath(_count), cancellationToken);
    }

    /// <summary>
    /// 把一局成绩上传排行榜（POST <c>api/score</c>），署名取存档里的 Callsign，返回全服名次（1 起）。
    /// 网络失败抛 <see cref="NetworkException"/>——由 <c>ResultState</c> 兜住（离线也要能看结算，名次算可选装饰）。
    /// </summary>
    public readonly struct SubmitScoreCommand : IAsyncCommand<SubmitScoreResponse>
    {
        private readonly BattleResult _result;

        public SubmitScoreCommand(BattleResult result) => _result = result;

        public UniTask<SubmitScoreResponse> ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            var request = new SubmitScoreRequest
            {
                Player = ctx.GetModel<PlayerRecordModel>().Callsign.Value,
                Score = _result.Score,
                Wave = _result.Wave,
                Kills = _result.Kills,
            };
            return ctx.GetUtility<IHttpUtility>()
                .Post<SubmitScoreRequest, SubmitScoreResponse>(OutpostNet.ScorePath, request, cancellationToken);
        }
    }
}
