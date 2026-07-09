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

            // 波次角色表 → Sim 的成长定义（角色 = 敌人原型 id + 一条成长曲线；顺序无关，Sim 按各自 UnlockWave 生效）。
            var roleRows = cfg.TbWaveRole.DataList;
            var roles = new Sim.WaveRole[roleRows.Count];
            for (int i = 0; i < roleRows.Count; i++)
            {
                var r = roleRows[i];
                roles[i] = new Sim.WaveRole
                {
                    EnemyId = r.EnemyId,
                    UnlockWave = r.UnlockWave,
                    BaseCount = r.BaseCount,
                    PerWave = r.PerWave,
                    Interval0 = r.Interval0,
                    IntervalMin = r.IntervalMin,
                    IntervalDecay = r.IntervalDecay,
                };
            }

            return new BattleSetup
            {
                Seed = seed,
                ArenaRadius = g.ArenaRadius,
                Player = new PlayerSetup
                {
                    MaxHp = g.PlayerMaxHp,
                    Attack = g.PlayerAttack,
                    MaxAttack = g.PlayerMaxAttack,
                    AttackInterval = g.PlayerAttackInterval,
                    Range = g.PlayerRange,
                    MaxRange = g.PlayerMaxRange,
                    RegenPerSecond = g.PlayerRegen,
                    Radius = g.PlayerRadius,
                    RotationSpeed = g.PlayerRotationSpeed,
                    MaxRotationSpeed = g.PlayerMaxRotationSpeed,
                    // 拦截溅射（近防炮张力）暂用原型常量、未进配置表——数值调优 / 进表是后续项。
                    SplashRadius = 2.2f,
                    SplashDamageScale = 0.6f,
                },
                Enemies = enemies.ToArray(),
                Scaling = new Sim.WaveScaling
                {
                    Roles = roles,
                    StatGrowth = cfg.TbWaveScaling.Data.StatGrowth,
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
