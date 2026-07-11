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
    }
}
