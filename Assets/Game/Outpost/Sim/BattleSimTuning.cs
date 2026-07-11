using System;

namespace Game.Outpost.Sim
{
    /// <summary>
    /// 两个后端（<see cref="ReferenceBattleSim"/> / ECS 后端）共享的内核调校常量。
    /// 它们是规则规格的一部分——对拍的前提是双方跑同一套判定阈值，各自定义迟早漂移，
    /// 故收口在 Sim 程序集、两个后端同源引用。
    /// </summary>
    public static class BattleSimTuning
    {
        /// <summary>炮口角度差在此容差内即视为对准、可开火（度）。</summary>
        public const float AimToleranceDeg = 6f;

        /// <summary>
        /// 锥判定用 cos²(容差)：热路径以点积代替逐敌反三角——
        /// <c>dot ≥ cos(容差)·|p|</c> ⟺ <c>dot² ≥ cos²·|p|²</c>（且 <c>dot &gt; 0</c>），数学等价且零三角函数。
        /// </summary>
        public static readonly float AimToleranceCosSq =
            (float)Math.Pow(Math.Cos(AimToleranceDeg * Math.PI / 180.0), 2);

        /// <summary>无上限射速下单帧最多发数（防病态循环；远超玩法所需）。</summary>
        public const int MaxShotsPerTick = 64;

        /// <summary>有效射速间隔低于此即进「火墙」：炮口未对准也持续击发（边转边扫、空放不结算伤害）。</summary>
        public const float FirehoseFireInterval = 0.06f;
    }
}
