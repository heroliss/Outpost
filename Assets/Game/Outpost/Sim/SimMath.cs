using System;

namespace Game.Outpost.Sim
{
    /// <summary>
    /// 炮塔回转的角度数学（度制、标准数学角：0 = +X、逆时针为正）。
    /// 属于规则规格、两个后端共享——回转的边界行为（归一化、最短角差、不过冲）各写一份迟早漂移。
    /// 刻意走 <see cref="Math"/>（托管路径）：回转结果参与对拍，不能引入 Burst libm 与 .NET 的实现差异。
    /// </summary>
    public static class SimMath
    {
        /// <summary>归一化到 [0, 360)。</summary>
        public static float NormalizeDeg(float a)
        {
            a %= 360f;
            if (a < 0f) a += 360f;
            return a;
        }

        /// <summary>from→to 的最短带符号角差，落在 [-180, 180]。</summary>
        public static float DeltaAngleDeg(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d < -180f) d += 360f;
            else if (d > 180f) d -= 360f;
            return d;
        }

        /// <summary>以 maxDelta 为步长把 cur 朝 target 转（不过冲），返回归一化角。</summary>
        public static float MoveTowardsAngleDeg(float cur, float target, float maxDelta)
        {
            float d = DeltaAngleDeg(cur, target);
            if (maxDelta >= Math.Abs(d)) return NormalizeDeg(target);
            return NormalizeDeg(cur + Math.Sign(d) * maxDelta);
        }

        // ── 弹道 / 泥地几何（标量入参：托管与 Burst 两侧逐式一致，无向量类型实现差异）────

        /// <summary>
        /// 扫掠线段 vs 圆求交：起点 (px,py)、位移 (dx,dy)、圆心 (cx,cy)、半径 r。
        /// 返回首个交点参数 t ∈ [0,1]（弹着点 = 起点 + t×位移）；无交返回 -1。起点已在圆内视为 t=0 命中。
        /// 弹丸单 tick 位移可大于小型敌人半径（隧穿），逐点判定不可用——这是弹着判定的规格公式。
        /// </summary>
        public static float SegmentCircleHitT(float px, float py, float dx, float dy, float cx, float cy, float r)
        {
            float mx = px - cx, my = py - cy;
            float c = mx * mx + my * my - r * r;
            if (c <= 0f) return 0f;                 // 起点已在圆内
            float a = dx * dx + dy * dy;
            if (a <= 0f) return -1f;                // 零位移
            float b = mx * dx + my * dy;            // 半 b（m·d）
            if (b >= 0f) return -1f;                // 在圆外且正在远离
            float disc = b * b - a * c;
            if (disc < 0f) return -1f;
            float t = (-b - (float)Math.Sqrt(disc)) / a;
            return t <= 1f ? t : -1f;
        }

        /// <summary>残骸密度格索引：坐标 (x,y) 落进覆盖 ±half、边长 cellSize、维度 dim×dim 的网格（越界钳到边缘格）。</summary>
        public static int WreckCellIndex(float x, float y, float half, float cellSize, int dim)
        {
            int ix = (int)Math.Floor((x + half) / cellSize);
            int iy = (int)Math.Floor((y + half) / cellSize);
            if (ix < 0) ix = 0; else if (ix >= dim) ix = dim - 1;
            if (iy < 0) iy = 0; else if (iy >= dim) iy = dim - 1;
            return iy * dim + ix;
        }

        /// <summary>
        /// 整数哈希 → [0, 1)（xxhash 风格雪崩混合）。规则里"每具残骸各不相同但完全确定"的散布系数来源——
        /// 纯整数运算，Burst 与托管逐位一致，且不消耗种子 RNG 流（不动两后端的 RNG 消耗顺序契约）。
        /// </summary>
        public static float Hash01(uint h)
        {
            h ^= h >> 16;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;
            return (h >> 8) * (1f / 16777216f); // 取高 24 位当尾数，恰落 [0,1)
        }

        // ── 敌人体型 / 击发散射 / 曳光弹（规则规格，两后端共享同一确定性算法）────

        /// <summary>
        /// 敌人随机体型系数：在 [<paramref name="sizeMin"/>, <paramref name="sizeMax"/>] 内按实例 id 取一个确定性值
        /// （<see cref="Hash01"/> 混合，<b>不消耗种子 RNG</b>——出生角序列不受影响，两后端逐位一致）。
        /// min/max &le; 0 视为 1、max &lt; min 收敛为 min（= 体型固定）。乘上原型的<b>渲染体型 / 碰撞半径 / 生命</b>。
        /// <para>分布按 <see cref="BattleSimTuning.SizeBias"/> 向下限偏置——多数常规、偶尔巨怪，
        /// 故体型上限可以开得夸张而不推高平均体型与平均血量。</para>
        /// </summary>
        public static float EnemySizeScale(float sizeMin, float sizeMax, int id)
        {
            float smin = sizeMin > 0f ? sizeMin : 1f;
            float smax = sizeMax > smin ? sizeMax : smin;
            if (smax <= smin) return smin;
            // id × Knuth 乘性哈希常量再过 Hash01：相邻 id 也充分去相关（避免"按刷怪序渐变体型"）。
            float h = Hash01((uint)id * 2654435761u);
            return smin + (smax - smin) * (float)Math.Pow(h, BattleSimTuning.SizeBias);
        }

        /// <summary>
        /// 体型 → 生命的换算：<b>按面积（平方）</b>，即生命 ∝ 体型²。两后端共用本函数，杜绝各写一份漂移。
        /// <para><b>为什么是平方而不是线性</b>：碰撞半径已随体型放大，而弹幕里半径 r 的圆每秒接到的子弹数 ∝ r
        /// （它对弹流呈现的是直径宽度、不是面积）。线性血量下 TTK ∝ r/r = 常数——巨怪与小怪死得一样快，
        /// 体型完全不转化成硬度；平方血量下 TTK ∝ r²/r = r，体型才真正等于耐久。</para>
        /// </summary>
        public static float SizeHpFactor(float sizeScale) => sizeScale * sizeScale;

        /// <summary>体型系数上界（guard 同 <see cref="EnemySizeScale"/>）——占位网格边长按它推导，保证 3×3 邻域覆盖最大接触对。</summary>
        public static float MaxSizeScale(float sizeMin, float sizeMax)
        {
            float smin = sizeMin > 0f ? sizeMin : 1f;
            return sizeMax > smin ? sizeMax : smin;
        }

        /// <summary>
        /// 射速联动的确定性散射偏移（度）：按累计发序 <paramref name="shotIndex"/> 取 [-spread, +spread] 内一点，
        /// spread 随射速（1/<paramref name="attackInterval"/>）从 0 线性张开到 <see cref="BattleSimTuning.SpreadMaxDeg"/>
        /// （区间见 <see cref="BattleSimTuning.SpreadRateLo"/>/<see cref="BattleSimTuning.SpreadRateHi"/>）。
        /// 用 <see cref="Hash01"/> 不碰种子 RNG——射速越快散得越开，且完全确定、两后端一致。
        /// </summary>
        public static float SpreadOffsetDeg(float attackInterval, long shotIndex)
        {
            float rate = attackInterval > 0f ? 1f / attackInterval : 0f;
            float t = (rate - BattleSimTuning.SpreadRateLo)
                      / (BattleSimTuning.SpreadRateHi - BattleSimTuning.SpreadRateLo);
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float spread = BattleSimTuning.SpreadMaxDeg * t;
            return (Hash01((uint)shotIndex) * 2f - 1f) * spread;
        }

        /// <summary>曳光弹档位：第 10/100/1000 发分别升为 1/2/3，其余为 0（表现层据此换色，直观展示已射子弹量）。</summary>
        public static byte TracerTier(long shotIndex)
        {
            if (shotIndex % 1000 == 0) return 3;
            if (shotIndex % 100 == 0) return 2;
            if (shotIndex % 10 == 0) return 1;
            return 0;
        }

        /// <summary>
        /// 残骸静置偏移：击杀/自爆点 (px,py) 沿远离哨站（原点）的径向滑出、带侧向抖动——
        /// 死点即弹道来向的延长线，残骸被"打飞"一小段（幅度按原型半径 <paramref name="radius"/> 缩放）。
        /// 系数由 <see cref="Hash01"/> 按创建序号 <paramref name="seq"/> 取：确定性、无三角函数
        /// （方向经 normalize 得到，sqrt 是 IEEE 正确舍入运算，两后端逐位一致）。幅度常量见 BattleSimTuning。
        /// </summary>
        public static void WreckRestOffset(float px, float py, float radius, int seq, out float ox, out float oy)
        {
            float lenSq = px * px + py * py;
            float dx = 1f, dy = 0f;
            if (lenSq > 1e-8f)
            {
                float inv = 1f / (float)Math.Sqrt(lenSq);
                dx = px * inv;
                dy = py * inv;
            }
            float radial = radius * (BattleSimTuning.WreckRestRadialMin
                + (BattleSimTuning.WreckRestRadialMax - BattleSimTuning.WreckRestRadialMin) * Hash01((uint)seq * 2u));
            float side = radius * BattleSimTuning.WreckRestSideMax * (Hash01((uint)seq * 2u + 1u) * 2f - 1f);
            // 垂线 (-dy, dx)：径向滑出 + 侧向抖动，堆积不呈严格放射线。
            ox = dx * radial - dy * side;
            oy = dy * radial + dx * side;
        }
    }
}
