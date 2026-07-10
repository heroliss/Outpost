using Game.Framework.Model;
using R3;

namespace Game.Outpost.Save
{
    /// <summary>
    /// 玩家历史战绩的展示状态：把存档（<see cref="OutpostRecord"/>）镜像成可订阅的 <c>RP</c>，供标题页 / 结算页只读绑定。
    /// 注册在根 <see cref="OutpostContext"/>——跨局常驻（标题↔战斗↔结算都活着，战斗子 Context 撤了它还在）。
    /// </summary>
    /// <remarks>
    /// 写路径单一：启动时 <c>LoadPlayerRecordCommand</c> 从存档灌入、每局结束 <c>SubmitRunResultCommand</c> 并入并回写；
    /// 读路径只读：View 经 <c>GetPlayerRecordCommand</c> 拿只读束订阅（同 <see cref="Battle.BattleModel"/> 的读写分离）。
    /// 存档的<b>序列化映射收在本类</b>（<see cref="LoadFrom"/> / <see cref="ToRecord"/>），命令只做编排不碰字段搬运。
    /// </remarks>
    public sealed class PlayerRecordModel : IModel
    {
        public readonly RP<int> BestScore = new(0);
        public readonly RP<int> BestWave = new(0);
        public readonly RP<int> Runs = new(0);

        /// <summary>排行榜署名（随存档持久；空 = 尚未生成，<c>LoadPlayerRecordCommand</c> 启动时补齐）。</summary>
        public readonly RP<string> Callsign = new("");

        /// <summary>从存档整体灌入（启动载入用；无存档时不调，保持零值即新玩家）。</summary>
        public void LoadFrom(OutpostRecord r)
        {
            BestScore.Value = r.BestScore;
            BestWave.Value = r.BestWave;
            Runs.Value = r.Runs;
            Callsign.Value = r.Callsign ?? "";
        }

        /// <summary>把当前状态导出成存档对象（回写落盘用）。</summary>
        public OutpostRecord ToRecord() => new()
        {
            BestScore = BestScore.Value,
            BestWave = BestWave.Value,
            Runs = Runs.Value,
            Callsign = Callsign.Value,
        };

        /// <summary>
        /// 并入一局结果：累计对局、刷新历史最高分与最高波次。
        /// 返回<b>是否刷新了最高分</b>（结算页据此报"新纪录"）。纯内存更新，落盘由调用方 <c>SubmitRunResultCommand</c> 负责。
        /// </summary>
        public bool ApplyRunResult(int score, int wave)
        {
            Runs.Value += 1;
            if (wave > BestWave.Value) BestWave.Value = wave;

            bool newBest = score > BestScore.Value;
            if (newBest) BestScore.Value = score;
            return newBest;
        }
    }
}
