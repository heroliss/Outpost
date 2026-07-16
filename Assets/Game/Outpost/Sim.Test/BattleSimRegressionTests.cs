using Game.Outpost.Battle;
using Game.Outpost.Sim.Ecs;
using NUnit.Framework;
using Unity.Burst;
using UnityEngine;

namespace Game.Outpost.Sim.Test
{
    /// <summary>
    /// 战斗模拟内核的回归护栏——把 tech-notes 里手工做过多轮的「确定性 / 双后端对拍 / 平衡长跑」固化成测试。
    /// 全部无头（纯 C# 跑数、不进真实 Play 逻辑），秒级跑完；模拟内核零引擎依赖，这是它「可单测」承诺的兑现。
    /// </summary>
    /// <remarks>
    /// 四条护栏各抓不同的漂移面：
    /// <list type="bullet">
    /// <item>确定性自比对——同种子两局逐位相同（守 <c>ReferenceBattleSim</c> 自身无隐藏非确定性，如容器遍历序）。</item>
    /// <item>关 Burst 双后端对拍——<c>Reference</c> vs <c>Ecs</c> 逐位相同（守 ECS 移植零逻辑偏差；开 Burst 会因 ulp 混沌分叉，故对拍是关 Burst 契约，见 ADR-0030）。</item>
    /// <item>黄金快照——锚定固定跑数的位精确指纹（守两后端<b>共享</b>的规则/配置改动，这是对拍抓不到的面——共享代码改了两边一起变）。</item>
    /// <item>托管长跑不失守——稳态永续（守平衡改动，如开火统一 commit 316bc5c）。</item>
    /// </list>
    /// </remarks>
    public sealed class BattleSimRegressionTests
    {
        private const int Seed = 777;

        /// <summary>同种子两个参考实现逐 tick 逐位相同——确定性契约。</summary>
        [Test]
        public void Reference_IsDeterministic_BitIdentical()
        {
            using var a = new ReferenceBattleSim();
            using var b = new ReferenceBattleSim();
            BattleSimTestHarness.RunLockstepAndAssertEqual(a, b, Seed, waves: 12);
        }

        /// <summary>
        /// 关 Burst 下 Reference 与 Ecs 后端逐 tick 逐位相同——ECS 移植零逻辑偏差的可重复验证。
        /// 关 Burst 让 job 走托管、与参考实现同一 JIT 浮点语义（开 Burst 原生码有 ulp 级差异、被混沌放大，
        /// 那是「同一个游戏、不是逐位同一局」，不该在此断言）。跑完务必还原全局 Burst 开关。
        /// </summary>
        [Test]
        public void Backends_AreBitIdentical_WithBurstDisabled()
        {
            bool prev = BurstCompiler.Options.EnableBurstCompilation;
            BurstCompiler.Options.EnableBurstCompilation = false;
            try
            {
                using var reference = new ReferenceBattleSim();
                using var ecs = new EcsBattleSim();
                BattleSimTestHarness.RunLockstepAndAssertEqual(reference, ecs, Seed, waves: 12);
            }
            finally
            {
                BurstCompiler.Options.EnableBurstCompilation = prev;
            }
        }

        /// <summary>
        /// 黄金快照：seed 777 无头跑到第 12 波的位精确指纹。断言值 = 实测捕获（2026-07-17，随机体型（生命按面积）+ 射速联动散射 + 解锁波次前移后重捕）。
        /// <b>它失败 = 规则或配置改动改变了 1~11 波的行为</b>——先确认改动是有意的，再更新这里的数字（这是 tripwire，不是 bug）。
        /// 与对拍互补：对拍抓单后端偏移，本条抓两后端共享代码（<c>SimMath</c> / 共享 tuning / 配置）的改动。
        /// </summary>
        [Test]
        public void GoldenSnapshot_Seed777_Wave12()
        {
            var tables = BattleSimTestHarness.LoadTables();
            var setup = BattleSetupFactory.Build(tables, Seed);
            using var sim = new ReferenceBattleSim();
            long chk = BattleSimTestHarness.RunHeadlessTrajectoryChecksum(
                sim, setup, tables, targetWave: 12, out int kills, out int score, out int ticks);

            Assert.AreEqual(12, sim.WaveIndex, "did not reach wave 12");
            Assert.AreEqual(9445, ticks, "tick count drift");
            Assert.AreEqual(511, kills, "kills drift");
            Assert.AreEqual(2937, score, "score drift");
            Assert.AreEqual(-2892748732779235377L, chk, "trajectory checksum drift");
        }

        /// <summary>
        /// 无头托管贪心跑进平台期不失守——稳态永续的回归护栏（「开火统一」等平衡改动的守门）。
        /// 主断言 = 不失守 + 到达目标波；平台期单波最低血占比记进日志供调参，并加一条宽松下限防塌盘。
        /// <para><b>目标定在 w21（平台期约从 w20 起，覆盖前两个平台期波次）</b>：若开火/平衡改动会让托管撑不住，
        /// 早在平台期头几波就会失守，这里已能抓到。刻意不往深跑——Reference 后端在深残骸场的
        /// <c>O(残骸×邻域敌人)</c> 推挤演算<b>每波约翻倍</b>（w20≈21s、w22≈68s、w24 已破 NUnit 180s 超时），
        /// 那正是 M8 要演示的后端差距、不该拖进单测；更深的存活由游戏内托管永续观战 + <c>RunHeadlessSurvival</c>
        /// 手动跑数覆盖。两后端已由对拍证明逐位等价，故本护栏跑哪个后端结论一致。</para>
        /// </summary>
        [Test]
        public void HeadlessAutoManaged_SurvivesPlateau()
        {
            const int TargetWave = 21;
            var tables = BattleSimTestHarness.LoadTables();
            var setup = BattleSetupFactory.Build(tables, Seed);
            using var sim = new ReferenceBattleSim();
            var stats = BattleSimTestHarness.RunHeadlessSurvival(sim, setup, tables, TargetWave);

            // 平台期入口这个深度，健康平衡下托管通常零掉血（贪心升级足以全清、无漏怪）——PlateauMinWave<0 即此情况。
            string plateau = stats.PlateauMinWave < 0
                ? "plateauMin=full(no damage taken by plateau entry)"
                : $"plateauMin%={stats.PlateauMinFraction * 100f:F1}@w{stats.PlateauMinWave}";
            Debug.Log($"[Survival] reached w{sim.WaveIndex} kills={sim.Kills} peak={stats.PeakEnemies} {plateau}");

            Assert.IsFalse(stats.Defeated, $"auto-managed defeated at wave {sim.WaveIndex}");
            Assert.GreaterOrEqual(sim.WaveIndex, TargetWave, "did not reach target wave");
            Assert.Greater(stats.PlateauMinFraction, 0.2f,
                "plateau single-wave consumption too deep — balance regressed toward defeat");
        }
    }
}
