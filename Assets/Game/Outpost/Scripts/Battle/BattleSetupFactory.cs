using System.Collections.Generic;
using Game.Outpost.Sim;
using OutpostCfg;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 把 Luban 配置表翻译成战斗模拟的纯 C# 输入 <see cref="BattleSetup"/>——配置后端（Luban 类型）到接缝的唯一桥。
    /// <see cref="Sim"/> 程序集零依赖，不认识 <c>OutpostCfg</c>；这层转换住在业务侧，后端置换（ECS）时原样复用。
    /// </summary>
    public static class BattleSetupFactory
    {
        // 波次成长的三个角色 → 敌人原型 id 的约定映射（住在业务侧，与 enemy.json 的 id 对齐；
        // 改敌人 id / 增删种类时同步这里）。Sim 只认 id，不认"炮灰/突击/重甲"这些角色语义。
        private const int FodderArchId = 1;  // 无人机（慢弱靠量的炮灰）
        private const int StrikerArchId = 2; // 突袭者（快速突击）
        private const int HeavyArchId = 3;   // 装甲兵（慢厚重甲）

        public static BattleSetup Build(Tables cfg, int seed)
        {
            var g = cfg.TbBattleGlobal.Data;

            var enemies = new List<EnemyArchetype>(cfg.TbEnemy.DataList.Count);
            foreach (var e in cfg.TbEnemy.DataList)
                enemies.Add(new EnemyArchetype
                {
                    Id = e.Id,
                    MaxHp = e.Hp,
                    MoveSpeed = e.MoveSpeed,
                    Attack = e.Attack,
                    AttackInterval = e.AttackInterval,
                    Radius = e.Radius,
                    Score = e.Score,
                });

            var sc = cfg.TbWaveScaling.Data;

            return new BattleSetup
            {
                Seed = seed,
                ArenaRadius = g.ArenaRadius,
                Player = new PlayerSetup
                {
                    MaxHp = g.PlayerMaxHp,
                    Attack = g.PlayerAttack,
                    AttackInterval = g.PlayerAttackInterval,
                    Range = g.PlayerRange,
                    MaxRange = g.PlayerMaxRange,
                    RegenPerSecond = g.PlayerRegen,
                    Radius = g.PlayerRadius,
                    RotationSpeed = g.PlayerRotationSpeed,
                    // 拦截溅射（近防炮张力）暂用原型常量、未进配置表——数值调优 / 进表是后续项。
                    SplashRadius = 2.2f,
                    SplashDamageScale = 0.6f,
                },
                Enemies = enemies.ToArray(),
                Scaling = new Sim.WaveScaling
                {
                    FodderArchId = FodderArchId,
                    StrikerArchId = StrikerArchId,
                    HeavyArchId = HeavyArchId,
                    FodderBase = sc.FodderBase,
                    FodderPerWave = sc.FodderPerWave,
                    FodderInterval0 = sc.FodderInterval0,
                    FodderIntervalMin = sc.FodderIntervalMin,
                    FodderIntervalDecay = sc.FodderIntervalDecay,
                    StrikerUnlockWave = sc.StrikerUnlockWave,
                    StrikerPerWave = sc.StrikerPerWave,
                    StrikerInterval = sc.StrikerInterval,
                    HeavyUnlockWave = sc.HeavyUnlockWave,
                    HeavyPerWave = sc.HeavyPerWave,
                    HeavyInterval = sc.HeavyInterval,
                    StatGrowth = sc.StatGrowth,
                },
            };
        }

        /// <summary>把一条升级配置翻译成模拟的玩家属性修正（波间三选一消费；提前放这里让映射与配置同处）。</summary>
        public static PlayerModifier ToModifier(Upgrade u) => u.Kind switch
        {
            UpgradeKind.Attack => new PlayerModifier(attackAdd: u.Value),
            UpgradeKind.AttackSpeed => new PlayerModifier(attackIntervalScale: u.Value),
            UpgradeKind.Range => new PlayerModifier(rangeAdd: u.Value),
            UpgradeKind.MaxHp => new PlayerModifier(maxHpAdd: u.Value),
            UpgradeKind.Regen => new PlayerModifier(regenAdd: u.Value),
            UpgradeKind.RotationSpeed => new PlayerModifier(rotationSpeedAdd: u.Value),
            _ => default,
        };
    }
}
