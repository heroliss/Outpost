namespace Game.Outpost.Battle
{
    /// <summary>
    /// 一次性池化特效的统一回收契约：Play 后自行推进动画，<see cref="IsDone"/> 为 true 时由
    /// <see cref="BattleDirectorSystem"/> 的统一回收循环 Despawn 归还池（借还必须走同一个 Bag，故回收权在 director）。
    /// </summary>
    public interface ITimedEffect
    {
        /// <summary>本次演出是否已播完、可被回收。</summary>
        bool IsDone { get; }
    }
}
