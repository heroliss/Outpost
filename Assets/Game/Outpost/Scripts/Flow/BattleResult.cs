namespace Game.Outpost.Flow
{
    /// <summary>
    /// 一局战斗的结果，由 <c>BattleDirectorSystem</c> 在终局构造、经 <see cref="ResultState"/> 构造参数传给结算页。
    /// 纯值对象——流程阶段的输入走构造参数（一次性、无残留脏状态）。
    /// </summary>
    public readonly struct BattleResult
    {
        /// <summary>坚持到的波次（无限模式的核心战绩）。</summary>
        public readonly int Wave;
        public readonly int Score;
        public readonly int Kills;

        /// <summary>是否主动撤离（true = 玩家按撤离键收束一局；false = 哨站被摧毁）。只影响结算文案，成绩口径相同。</summary>
        public readonly bool Retreated;

        public BattleResult(int wave, int score, int kills, bool retreated = false)
        {
            Wave = wave;
            Score = score;
            Kills = kills;
            Retreated = retreated;
        }
    }

    /// <summary>
    /// 结算页的打开参数：本局结果 + 是否刷新了历史最高分。
    /// <see cref="NewBest"/> 由 <c>SubmitRunResultCommand</c> 在把成绩并入存档时算出（结算页无从自行判断"这一局是否创纪录"，
    /// 故随结果一起传入），结算页据此显示"新纪录"。
    /// </summary>
    public readonly struct ResultArgs
    {
        public readonly BattleResult Result;
        public readonly bool NewBest;

        public ResultArgs(BattleResult result, bool newBest)
        {
            Result = result;
            NewBest = newBest;
        }
    }
}
