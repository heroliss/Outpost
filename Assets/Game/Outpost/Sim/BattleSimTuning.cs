namespace Game.Outpost.Sim
{
    /// <summary>
    /// 两个后端（<see cref="ReferenceBattleSim"/> / ECS 后端）共享的内核调校常量。
    /// 它们是规则规格的一部分——对拍的前提是双方跑同一套判定阈值，各自定义迟早漂移，
    /// 故收口在 Sim 程序集、两个后端同源引用。
    /// </summary>
    public static class BattleSimTuning
    {
        /// <summary>单帧最多发数（射速无上限，此值防单帧病态循环；远超玩法所需）。</summary>
        public const int MaxShotsPerTick = 64;

        // ── 敌人随机体型 ────────────────────────────────────────────────────

        /// <summary>
        /// 体型随机的偏置指数（作用在 [0,1) 随机数上再映射到 [SizeMin, SizeMax]）：
        /// <b>&gt; 1 把分布压向下限——多数个体接近常规体型、偶尔才蹦出一只巨无霸</b>；1 = 均匀分布。
        /// 均匀分布下"很大"会变成平均水平（体型区间开得越宽、平均体型/血量越膨胀），
        /// 偏置让体型上限可以开得夸张而不推高平均值——巨怪是惊喜，不是常态。
        /// 取 3：随机数落在上四分位的概率约 9%，接近上限的更稀有。
        /// </summary>
        public const float SizeBias = 3f;

        // ── 击发散射（射速联动，确定性——每发偏移由 SimMath.Hash01 按发序取，不消耗种子 RNG）──

        /// <summary>散射角上限（度，±）：射速拉满时每发偏离炮口方向的最大幅度。</summary>
        public const float SpreadMaxDeg = 3f;

        /// <summary>散射起始射速（发/秒）：低于此射速几乎不散（点射精准），高于此才随射速线性张开。</summary>
        public const float SpreadRateLo = 8f;

        /// <summary>散射满幅射速（发/秒）：达到此射速散射到 <see cref="SpreadMaxDeg"/>。介于两者间线性插值。</summary>
        public const float SpreadRateHi = 60f;

        // ── 残骸实体（推挤规则，见 WreckFieldSetup）─────────────────────────

        /// <summary>残骸碰撞半径 = 原型半径 × 此值（含散落的碎片带——敌人蹭到边缘即开始拱开，不必正踩中心）。</summary>
        public const float WreckBodyScale = 1.0f;

        /// <summary>静置径向偏移下限（× 原型半径）：残骸被"打飞"离死点至少这么远。</summary>
        public const float WreckRestRadialMin = 0.8f;

        /// <summary>静置径向偏移上限（× 原型半径）。</summary>
        public const float WreckRestRadialMax = 2.2f;

        /// <summary>静置侧向抖动幅度（× 原型半径，±）：堆积不呈严格放射线。</summary>
        public const float WreckRestSideMax = 0.6f;
    }
}
