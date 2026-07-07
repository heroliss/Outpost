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

            // 波次按主键升序（表已 index=id，DataList 按录入序；显式排序保证与波次号一致）。
            var waveRows = new List<Wave>(cfg.TbWave.DataList);
            waveRows.Sort((a, b) => a.Id.CompareTo(b.Id));
            var waves = new WaveSetup[waveRows.Count];
            for (int i = 0; i < waveRows.Count; i++)
            {
                var spawns = new WaveSpawnEntry[waveRows[i].Spawns.Count];
                for (int j = 0; j < spawns.Length; j++)
                {
                    var s = waveRows[i].Spawns[j];
                    spawns[j] = new WaveSpawnEntry { ArchetypeId = s.EnemyId, Count = s.Count, Interval = s.Interval };
                }
                waves[i] = new WaveSetup { Spawns = spawns };
            }

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
                    RegenPerSecond = g.PlayerRegen,
                    Radius = g.PlayerRadius,
                },
                Enemies = enemies.ToArray(),
                Waves = waves,
            };
        }

        /// <summary>把一条升级配置翻译成模拟的玩家属性修正（M2 波间三选一消费；提前放这里让映射与配置同处）。</summary>
        public static PlayerModifier ToModifier(Upgrade u) => u.Kind switch
        {
            UpgradeKind.Attack => new PlayerModifier(attackAdd: u.Value),
            UpgradeKind.AttackSpeed => new PlayerModifier(attackIntervalScale: u.Value),
            UpgradeKind.Range => new PlayerModifier(rangeAdd: u.Value),
            UpgradeKind.MaxHp => new PlayerModifier(maxHpAdd: u.Value),
            UpgradeKind.Regen => new PlayerModifier(regenAdd: u.Value),
            _ => default,
        };
    }
}
