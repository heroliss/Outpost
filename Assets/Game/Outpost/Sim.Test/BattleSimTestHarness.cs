using System;
using Game.Outpost.Battle;
using Game.Outpost.Sim;
using NUnit.Framework;
using OutpostCfg;
using UnityEngine;

namespace Game.Outpost.Sim.Test
{
    /// <summary>
    /// Outpost 战斗模拟的无头测试骨架：加载真实配置、复现导演的托管贪心选牌、按帧驱动一局、逐位比对两局。
    /// 全部纯 C#（不进 Play、不建 GameObject）——模拟内核是零引擎依赖的确定性纯函数，这里把 tech-notes
    /// 里手工做过多轮的「跑数标定 / 双后端对拍」固化成可重复跑的断言。
    /// </summary>
    /// <remarks>
    /// 选牌逻辑刻意与 <see cref="BattleDirectorSystem"/> 的 <c>ApplyBestUpgrade</c>/<c>AutoPriority</c>/<c>IsUpgradeCapped</c>
    /// 保持一致（那三个是 private、无法直接复用），封顶阈值读自 <see cref="PlayerSetup"/> 的 Max* 字段——
    /// 与导演开局缓存自 <c>TbBattleGlobal</c> 的同一批上限同源。改了导演那套优先级/封顶，这里要同步。
    /// </remarks>
    internal static class BattleSimTestHarness
    {
        private const float Dt = 1f / 60f;

        /// <summary>从磁盘 <c>Res/Configs/*.bytes</c> 直接构造配置表（编辑器下 <see cref="Application.dataPath"/> 指向 Assets）。</summary>
        public static Tables LoadTables()
        {
            var dir = System.IO.Path.Combine(Application.dataPath, "Game/Outpost/Res/Configs");
            Func<string, Luban.ByteBuf> loader =
                name => new Luban.ByteBuf(System.IO.File.ReadAllBytes(System.IO.Path.Combine(dir, name + ".bytes")));
            return new Tables(loader);
        }

        /// <summary>浮点位精确取值——对拍/黄金快照要的是「逐位相同」，不是「近似相等」。</summary>
        public static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

        // ── 托管贪心选牌（复现导演，无面板直接从全部未封顶升级里按优先级取最优）─────────────────

        /// <summary>托管选卡优先级（数字越小越优先），与 <c>BattleDirectorSystem.AutoPriority</c> 一致。</summary>
        private static int AutoPriority(UpgradeKind kind) => kind switch
        {
            UpgradeKind.Range => 0,
            UpgradeKind.AttackSpeed => 1,
            UpgradeKind.Attack => 2,
            UpgradeKind.RotationSpeed => 3,
            UpgradeKind.MaxHp => 4,
            UpgradeKind.Regen => 5,
            _ => 6,
        };

        /// <summary>某类升级是否已封顶（阈值读自 setup 的 Max* 上限），与 <c>BattleDirectorSystem.IsUpgradeCapped</c> 一致。</summary>
        private static bool IsCapped(UpgradeKind kind, IBattleSim sim, in PlayerSetup caps) => kind switch
        {
            UpgradeKind.Range => caps.MaxRange > 0f && sim.PlayerRange >= caps.MaxRange - 0.001f,
            UpgradeKind.Attack => caps.MaxAttack > 0f && sim.PlayerAttack >= caps.MaxAttack - 0.001f,
            UpgradeKind.RotationSpeed => caps.MaxRotationSpeed > 0f && sim.PlayerRotationSpeed >= caps.MaxRotationSpeed - 0.001f,
            UpgradeKind.AttackSpeed => caps.MinAttackInterval > 0f && sim.PlayerAttackInterval <= caps.MinAttackInterval + 0.0001f,
            UpgradeKind.Regen => caps.MaxRegen > 0f && sim.PlayerRegen >= caps.MaxRegen - 0.001f,
            UpgradeKind.MaxHp => caps.MaxHpCap > 0f && sim.PlayerMaxHp >= caps.MaxHpCap - 0.001f,
            _ => false,
        };

        /// <summary>从升级表里挑当前未封顶、优先级最高的一张（全封顶返回 null）。首个命中最优 rank 者胜（与导演的 <c>&lt;</c> 比较同口径）。</summary>
        public static Upgrade PickBest(IBattleSim sim, in PlayerSetup caps, Tables tables)
        {
            Upgrade best = null;
            int bestRank = int.MaxValue;
            foreach (var u in tables.TbUpgrade.DataList)
            {
                if (IsCapped(u.Kind, sim, caps)) continue;
                int rank = AutoPriority(u.Kind);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = u;
                }
            }
            return best;
        }

        // ── 状态签名与逐位比对 ────────────────────────────────────────────────

        /// <summary>雪崩混合（murmur3 finalizer）——把逐字段拼出的原始值打散成高质量哈希，喂给顺序无关求和。</summary>
        private static long Mix(long h)
        {
            unchecked
            {
                h ^= (long)((ulong)h >> 33);
                h *= unchecked((long)0xff51afd7ed558ccdUL);
                h ^= (long)((ulong)h >> 33);
                h *= unchecked((long)0xc4ceb9fe1a85ec53UL);
                h ^= (long)((ulong)h >> 33);
                return h;
            }
        }

        /// <summary>
        /// 全场空间态的位精确校验和（敌人 / 弹丸 / 残骸 / 密度格）——聚合标量之外再抓位置漂移。
        /// <b>刻意顺序无关</b>（逐实体混合哈希后求和）：两个后端每帧从各自存储收集快照，敌人 / 弹丸的<b>索引顺序可能不同</b>，
        /// 即使游戏态位精确相同——顺序敏感的校验和会误报对拍失败。求和（非异或）保证同位同向的两颗弹丸不互相抵消。
        /// 每实体带唯一键（敌人 <c>Id</c> / 残骸 <c>Seq</c>）抗碰撞；密度格按稳定格号、只计非空格。
        /// </summary>
        public static long PositionChecksum(IBattleSim sim)
        {
            long sum = 0;
            unchecked
            {
                int enemies = sim.EnemyCount;
                for (int i = 0; i < enemies; i++)
                {
                    var e = sim.GetEnemy(i);
                    long h = 17;
                    h = h * 31 + e.Id;
                    h = h * 31 + Bits(e.Position.X);
                    h = h * 31 + Bits(e.Position.Y);
                    h = h * 31 + Bits(e.Hp);
                    sum += Mix(h);
                }
                int projectiles = sim.ProjectileCount;
                for (int i = 0; i < projectiles; i++)
                {
                    var p = sim.GetProjectile(i);
                    long h = 31;
                    h = h * 31 + Bits(p.Position.X);
                    h = h * 31 + Bits(p.Position.Y);
                    h = h * 31 + Bits(p.Direction.X);
                    h = h * 31 + Bits(p.Direction.Y);
                    sum += Mix(h);
                }
                int slots = sim.WreckSlotCount;
                for (int i = 0; i < slots; i++)
                {
                    var w = sim.GetWreckSlot(i);
                    long h = 131;
                    h = h * 31 + w.Seq;
                    h = h * 31 + Bits(w.Position.X);
                    h = h * 31 + Bits(w.Position.Y);
                    sum += Mix(h);
                }
                var grid = sim.WreckGrid;
                int cells = grid.Dim * grid.Dim;
                for (int i = 0; i < cells; i++)
                {
                    int c = sim.GetWreckCellCount(i);
                    if (c == 0) continue;
                    sum += Mix((long)i * 31 + c);
                }
            }
            return sum;
        }

        /// <summary>断言两个 sim 的全部对外可观测态逐位相同（聚合标量 + 炮塔角 + 玩家属性 + 空间校验和）。</summary>
        public static void AssertStateEqual(IBattleSim a, IBattleSim b, int tick)
        {
            string at = $" @tick {tick}";
            Assert.AreEqual(a.Phase, b.Phase, "Phase" + at);
            Assert.AreEqual(a.WaveIndex, b.WaveIndex, "WaveIndex" + at);
            Assert.AreEqual(a.Kills, b.Kills, "Kills" + at);
            Assert.AreEqual(a.Score, b.Score, "Score" + at);
            Assert.AreEqual(a.EnemyCount, b.EnemyCount, "EnemyCount" + at);
            Assert.AreEqual(a.ProjectileCount, b.ProjectileCount, "ProjectileCount" + at);
            Assert.AreEqual(a.WreckSlotCount, b.WreckSlotCount, "WreckSlotCount" + at);
            Assert.AreEqual(Bits(a.PlayerHp), Bits(b.PlayerHp), "PlayerHp" + at);
            Assert.AreEqual(Bits(a.TurretAngle), Bits(b.TurretAngle), "TurretAngle" + at);
            Assert.AreEqual(Bits(a.PlayerRange), Bits(b.PlayerRange), "PlayerRange" + at);
            Assert.AreEqual(Bits(a.PlayerAttack), Bits(b.PlayerAttack), "PlayerAttack" + at);
            Assert.AreEqual(Bits(a.PlayerAttackInterval), Bits(b.PlayerAttackInterval), "PlayerAttackInterval" + at);
            Assert.AreEqual(Bits(a.PlayerRegen), Bits(b.PlayerRegen), "PlayerRegen" + at);
            Assert.AreEqual(Bits(a.PlayerRotationSpeed), Bits(b.PlayerRotationSpeed), "PlayerRotationSpeed" + at);
            Assert.AreEqual(PositionChecksum(a), PositionChecksum(b), "PositionChecksum" + at);
        }

        /// <summary>
        /// 两个 sim 同 setup 同种子锁步推进，逐 tick 断言逐位相同，直到 <paramref name="waves"/> 波（或任一失守）。
        /// 波间用「从 a 算出的最优升级同时应用到两者」，保证输入完全一致——测的是模拟演化本身、不是选牌逻辑。
        /// </summary>
        public static void RunLockstepAndAssertEqual(IBattleSim a, IBattleSim b, int seed, int waves)
        {
            var tables = LoadTables();
            var setupA = BattleSetupFactory.Build(tables, seed);
            var setupB = BattleSetupFactory.Build(tables, seed);
            a.Start(setupA);
            b.Start(setupB);
            AssertStateEqual(a, b, 0);

            int tick = 0;
            int guard = 5_000_000;
            while (a.WaveIndex < waves && a.Phase != BattlePhase.Defeat && guard-- > 0)
            {
                Assert.AreEqual(a.Phase, b.Phase, $"Phase diverged @tick {tick}");
                if (a.Phase == BattlePhase.WaveCleared)
                {
                    var best = PickBest(a, setupA.Player, tables);
                    if (best != null)
                    {
                        var mod = BattleSetupFactory.ToModifier(best);
                        a.ApplyModifier(mod);
                        b.ApplyModifier(mod);
                    }
                    a.BeginNextWave();
                    b.BeginNextWave();
                    continue;
                }
                a.Tick(Dt);
                b.Tick(Dt);
                tick++;
                AssertStateEqual(a, b, tick);
            }
            Assert.Less(guard, 5_000_000, "loop never advanced (guard untouched)");
            Assert.GreaterOrEqual(a.WaveIndex, waves, "did not reach target wave");
        }

        /// <summary>
        /// 无头驱动一个 sim 到 <paramref name="targetWave"/>，返回「逐 tick 折叠聚合态」的滚动校验和（黄金快照锚点）。
        /// 折叠口径必须与本方法固定一致（改了会让所有黄金值失配）：每 Tick 后按 Kills / Score / PlayerHp位 /
        /// TurretAngle位 / EnemyCount / WreckSlotCount 顺序折进 FNV 常量——一条数即整条轨迹的位精确指纹。
        /// </summary>
        public static long RunHeadlessTrajectoryChecksum(
            IBattleSim sim, BattleSetup setup, Tables tables, int targetWave,
            out int kills, out int score, out int ticks)
        {
            sim.Start(setup);
            long chk = 17;
            int t = 0;
            int guard = 5_000_000;
            while (sim.WaveIndex < targetWave && sim.Phase != BattlePhase.Defeat && guard-- > 0)
            {
                if (sim.Phase == BattlePhase.WaveCleared)
                {
                    var best = PickBest(sim, setup.Player, tables);
                    if (best != null) sim.ApplyModifier(BattleSetupFactory.ToModifier(best));
                    sim.BeginNextWave();
                    continue;
                }
                sim.Tick(Dt);
                t++;
                unchecked
                {
                    chk = chk * 1099511628211L + sim.Kills;
                    chk = chk * 1099511628211L + sim.Score;
                    chk = chk * 1099511628211L + Bits(sim.PlayerHp);
                    chk = chk * 1099511628211L + Bits(sim.TurretAngle);
                    chk = chk * 1099511628211L + sim.EnemyCount;
                    chk = chk * 1099511628211L + sim.WreckSlotCount;
                }
            }
            kills = sim.Kills;
            score = sim.Score;
            ticks = t;
            return chk;
        }

        /// <summary>无头托管长跑的统计结果（存活验收用）。</summary>
        public struct SurvivalStats
        {
            public bool Defeated;
            /// <summary>平台期（波号 ≥ <see cref="PlateauFromWave"/>）单波最低血占比里的最小值——每波消耗的深度指标。</summary>
            public float PlateauMinFraction;
            public int PlateauMinWave;
            public int PeakEnemies;
        }

        /// <summary>平台期起始波号（各角色数量约在此波前后全部到 MaxCount 进入稳态）。</summary>
        public const int PlateauFromWave = 20;

        /// <summary>
        /// 无头托管贪心跑到 <paramref name="targetWave"/>，统计是否失守 + 平台期单波最低血。
        /// 用于「开火统一」（commit 316bc5c）等平衡改动的回归护栏：稳态下托管应永续不失守。
        /// </summary>
        public static SurvivalStats RunHeadlessSurvival(IBattleSim sim, BattleSetup setup, Tables tables, int targetWave)
        {
            sim.Start(setup);
            var stats = new SurvivalStats { PlateauMinFraction = 1f, PlateauMinWave = -1 };
            float waveMinFrac = 1f;
            int guard = 30_000_000;
            while (sim.WaveIndex < targetWave && sim.Phase != BattlePhase.Defeat && guard-- > 0)
            {
                if (sim.Phase == BattlePhase.WaveCleared)
                {
                    int w = sim.WaveIndex;
                    if (w >= PlateauFromWave && waveMinFrac < stats.PlateauMinFraction)
                    {
                        stats.PlateauMinFraction = waveMinFrac;
                        stats.PlateauMinWave = w;
                    }
                    waveMinFrac = 1f;
                    var best = PickBest(sim, setup.Player, tables);
                    if (best != null) sim.ApplyModifier(BattleSetupFactory.ToModifier(best));
                    sim.BeginNextWave();
                    continue;
                }
                sim.Tick(Dt);
                float frac = sim.PlayerMaxHp > 0f ? sim.PlayerHp / sim.PlayerMaxHp : 0f;
                if (frac < waveMinFrac) waveMinFrac = frac;
                if (sim.EnemyCount > stats.PeakEnemies) stats.PeakEnemies = sim.EnemyCount;
            }
            stats.Defeated = sim.Phase == BattlePhase.Defeat;
            return stats;
        }
    }
}
