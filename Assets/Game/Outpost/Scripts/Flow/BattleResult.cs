namespace Game.Outpost.Flow
{
    /// <summary>
    /// 一局战斗的结果，由 <c>BattleDirector</c> 在终局构造、经 <see cref="ResultState"/> 构造参数传给结算页。
    /// 纯值对象——流程阶段的输入走构造参数（一次性、无残留脏状态）。
    /// </summary>
    public readonly struct BattleResult
    {
        public readonly bool Victory;
        public readonly int Score;
        public readonly int Wave;
        public readonly int WaveCount;
        public readonly int Kills;

        public BattleResult(bool victory, int score, int wave, int waveCount, int kills)
        {
            Victory = victory;
            Score = score;
            Wave = wave;
            WaveCount = waveCount;
            Kills = kills;
        }
    }
}
