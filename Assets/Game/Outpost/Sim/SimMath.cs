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
    }
}
