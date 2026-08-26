using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Logging;
using Game.Framework.Storage;
using Game.Outpost.Flow;
using R3;

namespace Game.Outpost.Save
{
    /// <summary>
    /// 启动载入：从存档读历史战绩灌进 <see cref="PlayerRecordModel"/>。无存档（新玩家）= Model 保持零值；
    /// 载入失败（存档损坏等）只记日志、不阻断启动（按新档继续，游戏能玩）。由 <c>BootState</c> 在进标题前 await。
    /// </summary>
    public readonly struct LoadPlayerRecordCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            var model = ctx.GetModel<PlayerRecordModel>();
            try
            {
                var record = await ctx.GetUtility<IStorageUtility>().Load<OutpostRecord>(StorageKeys.Record, cancellationToken);
                if (record != null) model.LoadFrom(record);

                // 排行榜署名首次启动生成一次后随档持久（新档 / 旧档缺字段都在这里补齐）——
                // 上传成绩要有稳定身份，dev server 按署名"每玩家一条最好成绩"合并。
                if (string.IsNullOrEmpty(model.Callsign.Value))
                {
                    model.Callsign.Value = $"OP-{UnityEngine.Random.Range(0, 0x10000):X4}";
                    await ctx.GetUtility<IStorageUtility>().Save(StorageKeys.Record, model.ToRecord(), cancellationToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Error("玩家战绩载入失败，将按新档继续。", e, nameof(LoadPlayerRecordCommand));
            }
        }
    }

    /// <summary>
    /// 每局结束把成绩并入历史存档：先更新 <see cref="PlayerRecordModel"/>（内存立即生效、展示不依赖落盘），
    /// 再 <b>await</b> 回写落盘（§26：别 fire-and-forget Save）。返回是否刷新了最高分——结算页据此显示"新纪录"。
    /// 由 <c>ResultState</c> 在开结算页前 await。
    /// </summary>
    public readonly struct SubmitRunResultCommand : IAsyncCommand<bool>
    {
        private readonly BattleResult _result;

        public SubmitRunResultCommand(BattleResult result) => _result = result;

        public async UniTask<bool> ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            var model = ctx.GetModel<PlayerRecordModel>();
            bool newBest = model.ApplyRunResult(_result.Score, _result.Wave);
            try
            {
                await ctx.GetUtility<IStorageUtility>().Save(StorageKeys.Record, model.ToRecord(), cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Error("玩家战绩落盘失败；本局内存状态仍保留。", e, nameof(SubmitRunResultCommand));
            }
            return newBest;
        }
    }

    /// <summary>只读查询：历史战绩读模型（供标题 / 结算页订阅或即时读，订阅即得当前值）。同 <c>Battle.BattleReadModel</c> 束模式。</summary>
    public readonly struct GetPlayerRecordCommand : ICommand<PlayerRecordReadModel>
    {
        public PlayerRecordReadModel Execute(ICommandContext ctx) => new(ctx.GetModel<PlayerRecordModel>());
    }

    /// <summary>
    /// 历史战绩只读读模型束：把 <see cref="PlayerRecordModel"/> 的响应式状态打成一束只读视图，供 View 一次拿齐。
    /// 每字段是 <c>ReadOnlyReactiveProperty</c>（RP 的只读面）——View 看得到、改不了，写只能走命令。
    /// </summary>
    public readonly struct PlayerRecordReadModel
    {
        public readonly ReadOnlyReactiveProperty<int> BestScore;
        public readonly ReadOnlyReactiveProperty<int> BestWave;
        public readonly ReadOnlyReactiveProperty<int> Runs;
        public readonly ReadOnlyReactiveProperty<string> Callsign;

        public PlayerRecordReadModel(PlayerRecordModel m)
        {
            BestScore = m.BestScore;
            BestWave = m.BestWave;
            Runs = m.Runs;
            Callsign = m.Callsign;
        }
    }
}
