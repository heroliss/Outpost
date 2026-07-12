using System;
using System.Collections.Generic;
using System.Numerics;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Game.Outpost.Sim.Ecs
{
    // ── 组件（chunk 内 SoA 连续存储）────────────────────────────────────────

    /// <summary>敌人位置（XY 平面）——移动 job 并行迭代的主体数据。</summary>
    internal struct EnemyPos : IComponentData
    {
        public float2 Value;
    }

    /// <summary>敌人当前生命。</summary>
    internal struct EnemyHp : IComponentData
    {
        public float Value;
    }

    /// <summary>敌人出生即定的只读元数据（实例 id / 原型索引 / 出生波成长系数）。</summary>
    internal struct EnemyMeta : IComponentData
    {
        public int Id;
        public int ArchIndex;
        public float StatScale;
    }

    // ── job 与主线程之间的记录结构（托管委托不能进 Burst，事件以缓冲带回按序重放）──

    /// <summary>移动 job 里抵达接触距离的敌人（主线程按 id 升序结算：伤害 + 泥地记账 + 事件 + 销毁实体）。</summary>
    internal struct Detonation
    {
        public Entity Entity;
        public int Id;
        public int ArchIndex;
        public float2 Pos;
        public float StatScale;
    }

    /// <summary>弹丸 job 的一次弹着记录，主线程据此重放 EnemyHit 并写回幸存者血量。</summary>
    internal struct HitRecord
    {
        public Entity Entity;
        public byte Killed;
        public int EnemyId;
        public int ArchIndex;
        public float2 Impact;   // 弹着点（事件位置 = 弹着点，非敌人中心）
        public float Damage;
        public float Splash;
        public float HpAfter;
    }

    /// <summary>弹丸 job 读写的玩家侧聚合状态（打包成一个 NativeReference 进出 job；拦截溅射在 job 内扣血）。</summary>
    internal struct PlayerHitState
    {
        public float Hp;
        public int Kills;
        public int Score;
    }

    // ── Burst jobs ─────────────────────────────────────────────────────────

    /// <summary>
    /// 移动（含泥地减速）+ 抵达判定：并行遍历 chunk、原地写位置；抵达接触距离的入队待爆
    /// （伤害结算 / 事件 / 实体销毁回主线程做——结构变更与托管回调都不属于 job）。
    /// FloatMode.Strict：数学须与参考实现的托管 IEEE 运算逐位一致，禁 FMA / 重结合（对拍前提）。
    /// CompileSynchronously：编辑器里 Burst 默认异步编译、首次调度静默回退托管执行——性能度量会被污染，
    /// 强制同步编译换一次微小的进战斗停顿（玩家包全 AOT，无此差别）。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal struct MoveJob : IJobChunk
    {
        public float Dt;
        public float PlayerRadius;
        [ReadOnly] public NativeArray<EnemyArchetype> Archetypes;
        public ComponentTypeHandle<EnemyPos> PosHandle;
        [ReadOnly] public ComponentTypeHandle<EnemyMeta> MetaHandle;
        [ReadOnly] public EntityTypeHandle EntityHandle;
        public NativeQueue<Detonation>.ParallelWriter Detonations;

        // 泥地减速（规则见 WreckFieldSetup）：按所在密度格打折移速。只读采样——记账在主线程与弹丸 job。
        public byte WreckEnabled;
        public float WreckHalf;
        public float WreckCellSize;
        public int WreckDim;
        public float WreckSlowPer;
        public float WreckSlowFloor;
        [ReadOnly] public NativeArray<int> WreckCells;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var pos = chunk.GetNativeArray(ref PosHandle);
            var meta = chunk.GetNativeArray(ref MetaHandle);
            var entities = chunk.GetNativeArray(EntityHandle);
            for (int i = 0; i < chunk.Count; i++)
            {
                var m = meta[i];
                var arch = Archetypes[m.ArchIndex];
                float contact = arch.Radius + PlayerRadius;
                float2 p = pos[i].Value;
                float dist = math.length(p);
                if (dist > contact)
                {
                    float speed = arch.MoveSpeed;
                    if (WreckEnabled != 0)
                    {
                        int cell = SimMath.WreckCellIndex(p.x, p.y, WreckHalf, WreckCellSize, WreckDim);
                        float mult = 1f - WreckSlowPer * WreckCells[cell];
                        if (mult < WreckSlowFloor) mult = WreckSlowFloor;
                        speed *= mult;
                    }
                    // 径直冲向原点，不越过接触距离（dist > contact > 0 保证不除零）——与参考实现逐式一致。
                    float newDist = math.max(contact, dist - speed * Dt);
                    pos[i] = new EnemyPos { Value = p * (newDist / dist) };
                }
                else
                {
                    Detonations.Enqueue(new Detonation
                    {
                        Entity = entities[i],
                        Id = m.Id,
                        ArchIndex = m.ArchIndex,
                        Pos = p,
                        StatScale = m.StatScale,
                    });
                }
            }
        }
    }

    /// <summary>射程内最近敌人（快照数组的 Burst 线性扫描；找不到写 -1）。既是开火资格判定，也是炮塔回转的目标。</summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal struct NearestScanJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Pos;
        public int Count;
        public float Range;
        public NativeReference<int> BestIndex;

        public void Execute()
        {
            int best = -1;
            float bestSq = Range * Range;
            for (int i = 0; i < Count; i++)
            {
                float dsq = math.lengthsq(Pos[i]);
                if (dsq <= bestSq)
                {
                    bestSq = dsq;
                    best = i;
                }
            }
            BestIndex.Value = best;
        }
    }

    /// <summary>
    /// 弹丸推进 + 扫掠弹着结算（单线程 Burst）：逐弹把本 tick 位移段与快照数组里的全体存活敌人求交、
    /// 取最早交点——O(P×N) 与参考实现同一算法（刻意不加空间分区，保持「同算法、不同执行模型」的干净对比），
    /// 提速全靠 Burst 编译 + 连续内存。循环是顺序语义（前一发的击杀改变后一发的战场），事件以
    /// <see cref="Hits"/> 缓冲带回主线程按序重放；击杀的泥地记账在 job 内完成（与弹着同序）。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal struct ProjectileJob : IJob
    {
        public float Step;              // 本 tick 弹丸位移长度（速度 × dt）
        public float ProjRadius;
        public float DespawnSq;
        public float SplashRadius;
        public float SplashDamageScale;
        [ReadOnly] public NativeArray<EnemyArchetype> Archetypes;

        // 敌人快照数组（gather 后；击杀在此 swap-remove，即当帧终态）
        public NativeArray<float2> EnemyPos;
        public NativeArray<float> EnemyHp;
        public NativeArray<EnemyMeta> EnemyMeta;
        public NativeArray<Entity> EnemyEntities;
        public NativeReference<int> EnemyCount;

        // 在飞弹丸（SoA 三列表；命中 / 出界在此 swap-remove）
        public NativeList<float2> ProjPos;
        public NativeList<float2> ProjDir;
        public NativeList<float> ProjDamage;

        public NativeReference<PlayerHitState> Player;
        public NativeList<HitRecord> Hits;
        public NativeList<Entity> KilledEntities;

        // 残骸落定（击杀侧）：与主线程自爆侧共用同一批槽位容器，逻辑与参考实现 AddWreck 逐行同式。
        public byte WreckEnabled;
        public float WreckHalf;
        public float WreckCellSize;
        public int WreckDim;
        public NativeArray<int> WreckCells;
        public NativeArray<float2> WreckPos;
        public NativeArray<int> WreckArch;
        public NativeArray<float> WreckDrift;
        public NativeArray<int> WreckSeq;
        public NativeArray<int> WreckCellSlot;
        public NativeReference<int3> WreckState; // x = 环形游标, y = 已用槽位数, z = 下一创建序号

        public void Execute()
        {
            int count = EnemyCount.Value;
            var player = Player.Value;

            for (int i = 0; i < ProjPos.Length; )
            {
                float2 pos = ProjPos[i];
                float2 dir = ProjDir[i];
                float dx = dir.x * Step, dy = dir.y * Step;

                int best = -1;
                float bestT = float.MaxValue;
                for (int j = 0; j < count; j++)
                {
                    float2 ep = EnemyPos[j];
                    float t = SimMath.SegmentCircleHitT(pos.x, pos.y, dx, dy, ep.x, ep.y,
                        Archetypes[EnemyMeta[j].ArchIndex].Radius + ProjRadius);
                    if (t >= 0f && t < bestT)
                    {
                        bestT = t;
                        best = j;
                    }
                }

                if (best >= 0)
                {
                    var impact = new float2(pos.x + dx * bestT, pos.y + dy * bestT);
                    var m = EnemyMeta[best];
                    var arch = Archetypes[m.ArchIndex];
                    Entity ent = EnemyEntities[best];
                    float damage = ProjDamage[i];
                    float hp = EnemyHp[best] - damage;
                    bool killed = hp <= 0f;
                    float splash = 0f;
                    if (killed)
                    {
                        // 拦截溅射：按弹着点距离（与参考实现逐式一致）。
                        float dist = math.length(impact);
                        if (SplashRadius > 0f && dist < SplashRadius)
                        {
                            float proximity = 1f - dist / SplashRadius;
                            splash = arch.Attack * m.StatScale * SplashDamageScale * proximity;
                            player.Hp = math.max(0f, player.Hp - splash);
                        }
                        AddWreck(impact, m.ArchIndex);
                        KilledEntities.Add(ent);
                        // swap-remove：末位补位，保持工作数组紧凑（索引顺序变化已在接口契约声明）。
                        count--;
                        EnemyPos[best] = EnemyPos[count];
                        EnemyHp[best] = EnemyHp[count];
                        EnemyMeta[best] = EnemyMeta[count];
                        EnemyEntities[best] = EnemyEntities[count];
                        player.Kills++;
                        player.Score += arch.Score;
                    }
                    else
                    {
                        EnemyHp[best] = hp;
                    }
                    Hits.Add(new HitRecord
                    {
                        Entity = ent,
                        Killed = (byte)(killed ? 1 : 0),
                        EnemyId = m.Id,
                        ArchIndex = m.ArchIndex,
                        Impact = impact,
                        Damage = damage,
                        Splash = splash,
                        HpAfter = hp,
                    });
                    RemoveProjectileAt(i);
                    continue; // swap-remove：原位换入末位弹，不前进
                }

                pos = new float2(pos.x + dx, pos.y + dy);
                if (math.lengthsq(pos) >= DespawnSq)
                {
                    RemoveProjectileAt(i);
                    continue;
                }
                ProjPos[i] = pos;
                i++;
            }

            EnemyCount.Value = count;
            Player.Value = player;
        }

        private void RemoveProjectileAt(int i)
        {
            ProjPos.RemoveAtSwapBack(i);
            ProjDir.RemoveAtSwapBack(i);
            ProjDamage.RemoveAtSwapBack(i);
        }

        // 残骸落定：与参考实现 AddWreck / 主线程自爆侧逐行同式（事件点 + 确定性散布，写环形槽位并在静置格记账）。
        private void AddWreck(float2 eventPos, int archIndex)
        {
            if (WreckEnabled == 0) return;
            var st = WreckState.Value;
            int seq = st.z++;
            SimMath.WreckRestOffset(eventPos.x, eventPos.y, Archetypes[archIndex].Radius, seq, out float ox, out float oy);
            var pos = new float2(eventPos.x + ox, eventPos.y + oy);
            int cell = SimMath.WreckCellIndex(pos.x, pos.y, WreckHalf, WreckCellSize, WreckDim);

            int slot = st.x;
            if (st.y == WreckPos.Length) WreckCells[WreckCellSlot[slot]]--; // 环形复写：最老的出格
            else st.y++;
            WreckPos[slot] = pos;
            WreckArch[slot] = archIndex;
            WreckDrift[slot] = 0f;
            WreckSeq[slot] = seq;
            WreckCellSlot[slot] = cell;
            st.x = (st.x + 1) % WreckPos.Length;
            WreckCells[cell]++;
            WreckState.Value = st;
        }
    }

    /// <summary>
    /// 敌人占位网格重建（推挤查询用；CSR 三段式：计数 → 前缀和 → 填充，填充期间计数清零复用为写游标）。
    /// 单线程 Burst——O(敌人数) 极轻，确定性的分段布局是下游并行推挤的只读输入。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal struct BuildEnemyGridJob : IJob
    {
        [ReadOnly] public NativeArray<float2> EnemyPos;
        public int Count;
        public float GridHalf;
        public float GridCellSize;
        public int GridDim;
        public NativeArray<int> CellCount;
        public NativeArray<int> CellStart; // 长度 cells+1（前缀和）
        public NativeArray<int> Items;     // 按格分段的敌人索引（容量 ≥ Count）

        public void Execute()
        {
            for (int c = 0; c < CellCount.Length; c++) CellCount[c] = 0;
            for (int i = 0; i < Count; i++)
                CellCount[SimMath.WreckCellIndex(EnemyPos[i].x, EnemyPos[i].y, GridHalf, GridCellSize, GridDim)]++;

            int sum = 0;
            for (int c = 0; c < CellCount.Length; c++)
            {
                CellStart[c] = sum;
                sum += CellCount[c];
            }
            CellStart[CellCount.Length] = sum;

            for (int c = 0; c < CellCount.Length; c++) CellCount[c] = 0;
            for (int i = 0; i < Count; i++)
            {
                int c = SimMath.WreckCellIndex(EnemyPos[i].x, EnemyPos[i].y, GridHalf, GridCellSize, GridDim);
                Items[CellStart[c] + CellCount[c]++] = i;
            }
        }
    }

    /// <summary>
    /// 残骸推挤（<b>逐槽并行</b> Burst）：每具残骸找"重叠的最近敌人"（距离平方最小、平票取小实例 id——
    /// 顺序无关的归约，所以并行调度不破坏确定性）、沿"敌→残骸"方向推开。每槽只写自己（IJobParallelFor
    /// 的默认安全约束恰好如此）；跨密度格的记账变更经 <see cref="CellChanges"/> 队列带回主线程回放
    /// （加减法可交换，入队顺序无关）。这条规则的演算量随残骸留存累计增长——参考实现同一算法托管直写会
    /// 逐波变慢，本 job 并行摊平，是后端置换收益随战局拉大的落点（ADR-0032）。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal struct WreckPushJob : IJobParallelFor
    {
        public float MaxStep;   // PushSpeed × dt
        public float MaxDrift;
        public float RecoverDt; // DriftRecoverPerSecond × dt（车辙回淤；0 = 不回淤）
        [ReadOnly] public NativeArray<EnemyArchetype> Archetypes;

        // 敌人快照 + 占位网格（只读）
        [ReadOnly] public NativeArray<float2> EnemyPos;
        [ReadOnly] public NativeArray<EnemyMeta> EnemyMeta;
        [ReadOnly] public NativeArray<int> CellCount;
        [ReadOnly] public NativeArray<int> CellStart;
        [ReadOnly] public NativeArray<int> Items;
        public float GridHalf;
        public float GridCellSize;
        public int GridDim;

        // 残骸槽位（写仅限 Execute 的 index 槽）
        public NativeArray<float2> WreckPos;
        [ReadOnly] public NativeArray<int> WreckArch;
        public NativeArray<float> WreckDrift;
        public NativeArray<int> WreckCellSlot;

        // 密度格布局（重算所在格用；计数本体不在并行 job 内改）
        public float WreckHalf;
        public float WreckCellSize;
        public int WreckDim;
        public NativeQueue<int2>.ParallelWriter CellChanges; // x = 旧格, y = 新格

        public void Execute(int slot)
        {
            float drift = WreckDrift[slot];
            if (RecoverDt > 0f && drift > 0f)
            {
                // 车辙回淤：漂移预算随时间恢复——车流必须持续碾压才能保持通道（与参考实现逐式一致）。
                drift -= RecoverDt;
                if (drift < 0f) drift = 0f;
                WreckDrift[slot] = drift;
            }
            if (drift >= MaxDrift) return; // 漂移到顶：本 tick 拱不动了（回淤后下一 tick 又能动一点）

            float2 wpos = WreckPos[slot];
            float wreckR = Archetypes[WreckArch[slot]].Radius * BattleSimTuning.WreckBodyScale;
            int cx = (int)math.floor((wpos.x + GridHalf) / GridCellSize);
            int cy = (int)math.floor((wpos.y + GridHalf) / GridCellSize);
            if (cx < 0) cx = 0; else if (cx >= GridDim) cx = GridDim - 1;
            if (cy < 0) cy = 0; else if (cy >= GridDim) cy = GridDim - 1;

            int best = -1;
            float bestDsq = float.MaxValue;
            int bestId = int.MaxValue;
            float bestContact = 0f;
            for (int oy = -1; oy <= 1; oy++)
            {
                int gy = cy + oy;
                if (gy < 0 || gy >= GridDim) continue;
                for (int ox = -1; ox <= 1; ox++)
                {
                    int gx = cx + ox;
                    if (gx < 0 || gx >= GridDim) continue;
                    int cell = gy * GridDim + gx;
                    int start = CellStart[cell];
                    int end = start + CellCount[cell];
                    for (int k = start; k < end; k++)
                    {
                        int ei = Items[k];
                        float contact = Archetypes[EnemyMeta[ei].ArchIndex].Radius + wreckR;
                        float ddx = wpos.x - EnemyPos[ei].x, ddy = wpos.y - EnemyPos[ei].y;
                        float dsq = ddx * ddx + ddy * ddy;
                        if (dsq >= contact * contact) continue;
                        int id = EnemyMeta[ei].Id;
                        if (dsq < bestDsq || (dsq == bestDsq && id < bestId))
                        {
                            bestDsq = dsq;
                            bestId = id;
                            best = ei;
                            bestContact = contact;
                        }
                    }
                }
            }
            if (best < 0) return;

            float2 epos = EnemyPos[best];
            float dist = math.sqrt(bestDsq);
            float dirX, dirY;
            if (dist > 1e-4f)
            {
                dirX = (wpos.x - epos.x) / dist;
                dirY = (wpos.y - epos.y) / dist;
            }
            else
            {
                // 完全重合：沿远离哨站的径向让开（与参考实现逐式一致）。
                float wl = math.length(wpos);
                if (wl > 1e-4f) { dirX = wpos.x / wl; dirY = wpos.y / wl; }
                else { dirX = 1f; dirY = 0f; }
            }
            float move = bestContact - dist;
            if (move > MaxStep) move = MaxStep;
            float room = MaxDrift - drift;
            if (move > room) move = room;
            if (move <= 1e-4f) return;

            wpos = new float2(wpos.x + dirX * move, wpos.y + dirY * move);
            WreckPos[slot] = wpos;
            WreckDrift[slot] = drift + move;
            int newCell = SimMath.WreckCellIndex(wpos.x, wpos.y, WreckHalf, WreckCellSize, WreckDim);
            int oldCell = WreckCellSlot[slot];
            if (newCell != oldCell)
            {
                WreckCellSlot[slot] = newCell; // 记账跟随位置：车辙被踩穿、路边堆垄
                CellChanges.Enqueue(new int2(oldCell, newCell));
            }
        }
    }

    // ── 后端本体 ───────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IBattleSim"/> 的 DOTS 后端：与 <see cref="ReferenceBattleSim"/> 同一套规则规格（真弹道扫掠碰撞 +
    /// 残骸减速泥地，ADR-0031），存储换 Entities chunk（SoA 连续内存）、热路径换 Burst job——移动并行
    /// （<see cref="MoveJob"/>），弹丸碰撞单线程保顺序语义（<see cref="ProjectileJob"/>，O(P×N) 与参考实现同算法）。
    /// 规则数学与参考实现逐式对齐、判定阈值与几何公式同源（<see cref="BattleSimTuning"/> / <see cref="SimMath"/>），
    /// 同 Setup + 同 Tick 序列可与参考实现对拍。
    /// </summary>
    /// <remarks>
    /// <b>驱动形态</b>：自建独立 <see cref="World"/>、不进 player loop、不用 SystemGroup——接缝契约是外部逐帧
    /// <see cref="Tick"/>、事件在调用栈内同步返回，因此所有 job 当帧 Complete，事件以记录缓冲带回主线程按序重放
    /// （托管委托不能进 Burst）。实体只有敌人；弹丸生命周期短、无异构原型，直存 NativeList（SoA 三列表）不进 chunk；
    /// 玩家是单例状态，留在托管字段、进出 job 打包成 <see cref="PlayerHitState"/>。回转 / 炮口 / 击发循环留在托管侧
    /// （与参考实现同一 System.Math 路径，规避 Burst libm 与 .NET 在超越函数上的 ulp 级差异进对拍；击发本身 O(发数) 极轻）。<br/>
    /// <b>快照</b>：每 Tick 从 chunk 收集一次工作数组（位置/血量/元数据/实体句柄），弹丸 job 在其上 swap-remove 后
    /// 即为当帧终态——<see cref="GetEnemy"/> 直接读它（O(1)，表现层逐帧全量遍历友好），
    /// 不 Tick 的阶段（WaveCleared / Defeat / 波间）快照与在飞弹丸保持有效（全场冻结语义）。
    /// chunk 仍是权威存储：血量稀疏写回、击杀批量销毁。<br/>
    /// <b>与参考实现的可观察差异</b>（对拍以聚合值为准）：<see cref="GetEnemy"/> 的索引顺序是 chunk 序而非列表序
    /// （接口本就声明索引顺序不稳定，仅影响极端浮点全等时的选靶平票）；开 Burst 后原生码与 Mono JIT 存在 ulp 级
    /// 浮点差异（ADR-0030 结论）。同帧自爆结算顺序（id 升序）、泥地记账顺序、RNG 消耗顺序均已是两后端共同契约。
    /// </remarks>
    public sealed class EcsBattleSim : IBattleSim
    {
        // 当前波次一条刷怪流的推进状态（托管推进，与参考实现同款）。
        private struct SpawnState
        {
            public int ArchIndex;
            public int Remaining;
            public float Interval;
            public float Timer;
        }

        private const double Rad2Deg = 180.0 / Math.PI;

        private BattleSetup _setup;
        private NativeArray<EnemyArchetype> _archetypes; // 原型表进 NativeArray 供 job 查表（EnemyArchetype 本就是 blittable struct）
        private Dictionary<int, int> _archIndexById;
        private System.Random _rng;

        private World _world;
        private EntityManager _em;
        private EntityArchetype _enemyEntityArchetype;
        private EntityQuery _query;

        private SpawnState[] _spawns = Array.Empty<SpawnState>();
        private readonly List<SpawnState> _streamScratch = new(4);

        private PlayerSetup _player;   // 可变副本：升级修正直接改这份
        private float _playerHp;
        private float _playerAttackCooldown;
        private float _turretAngleDeg;
        private float _waveStatScale = 1f;
        private int _nextEnemyId = 1;

        // 快照工作数组（Persistent，容量随波次增长复用）：语义见类型注释「快照」段。
        private NativeList<float2> _snapPos;
        private NativeList<float> _snapHp;
        private NativeList<EnemyMeta> _snapMeta;
        private NativeList<Entity> _snapEntities;
        private int _aliveCount;

        // 在飞弹丸（SoA 三列表，Persistent）：击发在托管侧 Add、弹丸 job 内 swap-remove；GetProjectile 直读。
        private NativeList<float2> _projPos;
        private NativeList<float2> _projDir;
        private NativeList<float> _projDamage;

        private NativeQueue<Detonation> _detonations;
        private readonly List<Detonation> _detScratch = new();
        private NativeList<HitRecord> _hits;
        private NativeList<Entity> _killedEntities;
        private NativeReference<int> _countRef;
        private NativeReference<PlayerHitState> _playerRef;
        private NativeReference<int> _bestRef;

        // 泥地（密度格 + 逐实体 SoA 槽位）：主线程（自爆）与弹丸 job（击杀）共写落定、推挤 job 并行改位置，
        // job 全部当帧 Complete 无竞争。
        private NativeArray<int> _wreckCells;
        private NativeArray<float2> _wreckPos;
        private NativeArray<int> _wreckArch;
        private NativeArray<float> _wreckDrift;
        private NativeArray<int> _wreckSeq;
        private NativeArray<int> _wreckCellSlot;
        private NativeReference<int3> _wreckState; // x=环形游标, y=已用槽位数, z=下一创建序号
        private byte _wreckEnabled;
        private float _wreckGridHalf;
        private int _wreckGridDim;

        // 敌人占位网格（推挤查询，每 tick 重建）+ 跨格记账队列。
        private float _enemyGridCellSize;
        private float _enemyGridHalf;
        private int _enemyGridDim;
        private NativeArray<int> _enemyCellCount;
        private NativeArray<int> _enemyCellStart;
        private NativeList<int> _enemyCellItems;
        private NativeQueue<int2> _wreckCellChanges;

        private bool _disposed;

        public BattlePhase Phase { get; private set; } = BattlePhase.Idle;
        public int WaveIndex { get; private set; }
        public float PlayerHp => _playerHp;
        public float PlayerMaxHp => _player.MaxHp;
        public float PlayerRange => _player.Range;
        public float PlayerAttack => _player.Attack;
        public float PlayerAttackInterval => _player.AttackInterval;
        public float PlayerRegen => _player.RegenPerSecond;
        public float PlayerRotationSpeed => _player.RotationSpeed;
        public float TurretAngle => _turretAngleDeg;
        public int Kills { get; private set; }
        public int Score { get; private set; }
        public int EnemyCount => _aliveCount;
        public int ProjectileCount => _projPos.IsCreated ? _projPos.Length : 0;

        public event Action<EnemySpawnedEvent> EnemySpawned;
        public event Action<EnemyHitEvent> EnemyHit;
        public event Action<TurretFiredEvent> TurretFired;
        public event Action<EnemyDetonatedEvent> EnemyDetonated;
        public event Action<int> WaveStarted;
        public event Action<int> WaveCleared;

        public EnemySnapshot GetEnemy(int index)
        {
            var m = _snapMeta[index];
            var p = _snapPos[index];
            var arch = _archetypes[m.ArchIndex];
            return new EnemySnapshot(m.Id, arch.Id, new Vector2(p.x, p.y), _snapHp[index], arch.MaxHp * m.StatScale);
        }

        public ProjectileSnapshot GetProjectile(int index)
        {
            var p = _projPos[index];
            var d = _projDir[index];
            return new ProjectileSnapshot(new Vector2(p.x, p.y), new Vector2(d.x, d.y));
        }

        public WreckGridInfo WreckGrid
            => _wreckEnabled == 0 ? default : new WreckGridInfo(_wreckGridDim, _setup.WreckField.CellSize, _wreckGridHalf);

        public int GetWreckCellCount(int index) => _wreckCells[index];

        public int WreckSlotCount => _wreckState.IsCreated ? _wreckState.Value.y : 0;

        public WreckSnapshot GetWreckSlot(int slot)
        {
            var p = _wreckPos[slot];
            return new WreckSnapshot(_wreckSeq[slot], _archetypes[_wreckArch[slot]].Id, new Vector2(p.x, p.y));
        }

        public void Start(BattleSetup setup)
        {
            if (Phase != BattlePhase.Idle)
                throw new InvalidOperationException("[EcsBattleSim] Start 只能调一次——重开一局请 new 新实例（与 FlowState 同心智）。");
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (setup.Enemies == null || setup.Enemies.Length == 0)
                throw new ArgumentException("[EcsBattleSim] Setup 缺少敌人原型。", nameof(setup));

            _setup = setup;
            _archetypes = new NativeArray<EnemyArchetype>(setup.Enemies, Allocator.Persistent);
            _archIndexById = new Dictionary<int, int>(setup.Enemies.Length);
            for (int i = 0; i < setup.Enemies.Length; i++)
                _archIndexById[setup.Enemies[i].Id] = i;

            _rng = new System.Random(setup.Seed);
            _player = setup.Player;
            _playerHp = _player.MaxHp;
            _playerAttackCooldown = 0f;
            _turretAngleDeg = 0f;

            _world = new World("OutpostBattle (ECS)");
            _em = _world.EntityManager;
            _enemyEntityArchetype = _em.CreateArchetype(typeof(EnemyPos), typeof(EnemyHp), typeof(EnemyMeta));
            _query = _em.CreateEntityQuery(typeof(EnemyPos), typeof(EnemyHp), typeof(EnemyMeta));

            _snapPos = new NativeList<float2>(1024, Allocator.Persistent);
            _snapHp = new NativeList<float>(1024, Allocator.Persistent);
            _snapMeta = new NativeList<EnemyMeta>(1024, Allocator.Persistent);
            _snapEntities = new NativeList<Entity>(1024, Allocator.Persistent);
            _projPos = new NativeList<float2>(512, Allocator.Persistent);
            _projDir = new NativeList<float2>(512, Allocator.Persistent);
            _projDamage = new NativeList<float>(512, Allocator.Persistent);
            _detonations = new NativeQueue<Detonation>(Allocator.Persistent);
            _hits = new NativeList<HitRecord>(256, Allocator.Persistent);
            _killedEntities = new NativeList<Entity>(256, Allocator.Persistent);
            _countRef = new NativeReference<int>(Allocator.Persistent);
            _playerRef = new NativeReference<PlayerHitState>(Allocator.Persistent);
            _bestRef = new NativeReference<int>(Allocator.Persistent);

            var wf = setup.WreckField;
            _wreckEnabled = (byte)(wf.SimCap > 0 ? 1 : 0);
            if (_wreckEnabled != 0)
            {
                _wreckGridHalf = setup.ArenaRadius + 1f;
                _wreckGridDim = Math.Max(1, (int)Math.Ceiling(_wreckGridHalf * 2f / wf.CellSize));
                _wreckCells = new NativeArray<int>(_wreckGridDim * _wreckGridDim, Allocator.Persistent);
                _wreckPos = new NativeArray<float2>(wf.SimCap, Allocator.Persistent);
                _wreckArch = new NativeArray<int>(wf.SimCap, Allocator.Persistent);
                _wreckDrift = new NativeArray<float>(wf.SimCap, Allocator.Persistent);
                _wreckSeq = new NativeArray<int>(wf.SimCap, Allocator.Persistent);
                _wreckCellSlot = new NativeArray<int>(wf.SimCap, Allocator.Persistent);

                // 敌人占位网格布局与参考实现同式推导（格边长 = 最大接触距离的上界，3×3 邻域必覆盖接触对）。
                float maxRadius = 0f;
                for (int i = 0; i < setup.Enemies.Length; i++)
                    if (setup.Enemies[i].Radius > maxRadius) maxRadius = setup.Enemies[i].Radius;
                _enemyGridCellSize = Math.Max(1f, maxRadius * (1f + BattleSimTuning.WreckBodyScale));
                _enemyGridHalf = setup.ArenaRadius + 3f;
                _enemyGridDim = Math.Max(1, (int)Math.Ceiling(_enemyGridHalf * 2f / _enemyGridCellSize));
                _enemyCellCount = new NativeArray<int>(_enemyGridDim * _enemyGridDim, Allocator.Persistent);
                _enemyCellStart = new NativeArray<int>(_enemyGridDim * _enemyGridDim + 1, Allocator.Persistent);
                _enemyCellItems = new NativeList<int>(1024, Allocator.Persistent);
            }
            else
            {
                // 机制关闭：给 job 挂 1 长度的占位容器（job 字段必须有效）。
                _wreckGridHalf = 1f;
                _wreckGridDim = 1;
                _wreckCells = new NativeArray<int>(1, Allocator.Persistent);
                _wreckPos = new NativeArray<float2>(1, Allocator.Persistent);
                _wreckArch = new NativeArray<int>(1, Allocator.Persistent);
                _wreckDrift = new NativeArray<float>(1, Allocator.Persistent);
                _wreckSeq = new NativeArray<int>(1, Allocator.Persistent);
                _wreckCellSlot = new NativeArray<int>(1, Allocator.Persistent);
                _enemyGridDim = 1;
                _enemyCellCount = new NativeArray<int>(1, Allocator.Persistent);
                _enemyCellStart = new NativeArray<int>(2, Allocator.Persistent);
                _enemyCellItems = new NativeList<int>(1, Allocator.Persistent);
            }
            _wreckState = new NativeReference<int3>(Allocator.Persistent);
            _wreckState.Value = new int3(0, 0, 1); // 空场、创建序号从 1 起（0 = 表现层"从未见过"哨兵）
            _wreckCellChanges = new NativeQueue<int2>(Allocator.Persistent);

            BeginWave(1);
        }

        public void BeginNextWave()
        {
            if (Phase != BattlePhase.WaveCleared)
                throw new InvalidOperationException($"[EcsBattleSim] BeginNextWave 仅在 WaveCleared 阶段可调（当前 {Phase}）。");
            BeginWave(WaveIndex + 1);
        }

        public void ApplyModifier(in PlayerModifier modifier)
        {
            if (Phase == BattlePhase.Defeat || _setup == null) return;
            // 与参考实现逐行一致（全部成长封顶，见 PlayerSetup 各 Max* 字段）。
            _player.Attack += modifier.AttackAdd;
            if (_player.MaxAttack > 0f && _player.Attack > _player.MaxAttack)
                _player.Attack = _player.MaxAttack;
            float minInterval = _player.MinAttackInterval > 0f ? _player.MinAttackInterval : 0.0008f;
            _player.AttackInterval = Math.Max(minInterval, _player.AttackInterval * modifier.AttackIntervalScale);
            _player.Range += modifier.RangeAdd;
            if (_player.MaxRange > 0f && _player.Range > _player.MaxRange)
                _player.Range = _player.MaxRange;
            _player.RegenPerSecond += modifier.RegenAdd;
            if (_player.MaxRegen > 0f && _player.RegenPerSecond > _player.MaxRegen)
                _player.RegenPerSecond = _player.MaxRegen;
            _player.RotationSpeed += modifier.RotationSpeedAdd;
            if (_player.MaxRotationSpeed > 0f && _player.RotationSpeed > _player.MaxRotationSpeed)
                _player.RotationSpeed = _player.MaxRotationSpeed;
            if (modifier.MaxHpAdd != 0f)
            {
                float newMax = _player.MaxHp + modifier.MaxHpAdd;
                if (_player.MaxHpCap > 0f && newMax > _player.MaxHpCap) newMax = _player.MaxHpCap;
                float gained = newMax - _player.MaxHp;
                _player.MaxHp = newMax;
                _playerHp = Math.Min(_player.MaxHp, _playerHp + Math.Max(0f, gained));
            }
        }

        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f || Phase != BattlePhase.WaveActive) return;

            TickSpawns(deltaTime);
            MoveAndDetonate(deltaTime);   // 移动（含泥地减速）+ 抵达自爆（id 序结算）
            if (CheckDefeat())
            {
                GatherSnapshot(); // 终局帧也要刷新快照：表现层还要画残余敌人海
                return;
            }
            TickPlayer(deltaTime);        // 回血 + gather + 回转 + 击发（生成弹丸，托管）
            TickProjectiles(deltaTime);   // 弹丸 job（推进 + 扫掠弹着 + 残骸落定）+ 事件回放
            if (CheckDefeat()) return;    // 溅射致死：快照已是当帧终态
            TickWreckPush(deltaTime);     // 敌人拱开残骸（并行 job）——负载随残骸留存累计增长
            CheckWaveEnd();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 清空事件防止终局后订阅方被迟到回调（与参考实现同约定）。
            EnemySpawned = null;
            EnemyHit = null;
            TurretFired = null;
            EnemyDetonated = null;
            WaveStarted = null;
            WaveCleared = null;

            // 所有 job 都在 Tick 内当帧 Complete，此处无在途依赖；容器可能因 Start 前被 Dispose 而未创建，逐一守卫。
            if (_archetypes.IsCreated) _archetypes.Dispose();
            if (_snapPos.IsCreated) _snapPos.Dispose();
            if (_snapHp.IsCreated) _snapHp.Dispose();
            if (_snapMeta.IsCreated) _snapMeta.Dispose();
            if (_snapEntities.IsCreated) _snapEntities.Dispose();
            if (_projPos.IsCreated) _projPos.Dispose();
            if (_projDir.IsCreated) _projDir.Dispose();
            if (_projDamage.IsCreated) _projDamage.Dispose();
            if (_detonations.IsCreated) _detonations.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
            if (_killedEntities.IsCreated) _killedEntities.Dispose();
            if (_countRef.IsCreated) _countRef.Dispose();
            if (_playerRef.IsCreated) _playerRef.Dispose();
            if (_bestRef.IsCreated) _bestRef.Dispose();
            if (_wreckCells.IsCreated) _wreckCells.Dispose();
            if (_wreckPos.IsCreated) _wreckPos.Dispose();
            if (_wreckArch.IsCreated) _wreckArch.Dispose();
            if (_wreckDrift.IsCreated) _wreckDrift.Dispose();
            if (_wreckSeq.IsCreated) _wreckSeq.Dispose();
            if (_wreckCellSlot.IsCreated) _wreckCellSlot.Dispose();
            if (_wreckState.IsCreated) _wreckState.Dispose();
            if (_enemyCellCount.IsCreated) _enemyCellCount.Dispose();
            if (_enemyCellStart.IsCreated) _enemyCellStart.Dispose();
            if (_enemyCellItems.IsCreated) _enemyCellItems.Dispose();
            if (_wreckCellChanges.IsCreated) _wreckCellChanges.Dispose();
            if (_world != null && _world.IsCreated) _world.Dispose();
            _world = null;
        }

        // ── 波次与刷怪（托管，公式与参考实现逐行一致）───────────────────────

        private void BeginWave(int waveIndex)
        {
            WaveIndex = waveIndex;
            var sc = _setup.Scaling;
            _waveStatScale = (float)Math.Pow(Math.Max(1f, sc.StatGrowth), waveIndex - 1);
            if (sc.MaxStatScale > 0f && _waveStatScale > sc.MaxStatScale)
                _waveStatScale = sc.MaxStatScale;

            _streamScratch.Clear();
            var roles = sc.Roles;
            if (roles != null)
            {
                for (int r = 0; r < roles.Length; r++)
                {
                    var role = roles[r];
                    if (waveIndex < role.UnlockWave) continue;
                    int step = waveIndex - role.UnlockWave;
                    double growth = role.CountGrowth > 1f ? Math.Pow(role.CountGrowth, step) : 1.0;
                    double rawCount = Math.Floor(role.BaseCount * growth) + Math.Floor(step * role.PerWave);
                    if (role.MaxCount > 0 && rawCount > role.MaxCount) rawCount = role.MaxCount;
                    int count = rawCount >= int.MaxValue ? int.MaxValue : (int)rawCount;
                    float interval = (float)(role.Interval0 / growth) - step * role.IntervalDecay;
                    if (interval < role.IntervalMin) interval = role.IntervalMin;
                    AddStream(role.EnemyId, count, interval);
                }
            }

            _spawns = _streamScratch.ToArray();
            Phase = BattlePhase.WaveActive;
            WaveStarted?.Invoke(waveIndex);
        }

        private void AddStream(int archId, int count, float interval)
        {
            if (count <= 0) return;
            if (!_archIndexById.TryGetValue(archId, out var archIndex)) return;
            _streamScratch.Add(new SpawnState
            {
                ArchIndex = archIndex,
                Remaining = count,
                Interval = Math.Max(0.001f, interval),
                Timer = 0f,
            });
        }

        private void TickSpawns(float dt)
        {
            for (int i = 0; i < _spawns.Length; i++)
            {
                ref var s = ref _spawns[i];
                if (s.Remaining <= 0) continue;
                s.Timer -= dt;
                while (s.Timer <= 0f && s.Remaining > 0)
                {
                    SpawnEnemy(s.ArchIndex);
                    s.Remaining--;
                    s.Timer += s.Interval;
                }
            }
        }

        private void SpawnEnemy(int archIndex)
        {
            // 出生角走托管 System.Random + System.Math——与参考实现共享同一 RNG 语义与三角实现
            // （消耗顺序：刷怪 → 击发散布，两后端一致，对拍前提）。
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            var pos = new float2(
                (float)Math.Cos(angle) * _setup.ArenaRadius,
                (float)Math.Sin(angle) * _setup.ArenaRadius);
            var arch = _archetypes[archIndex];
            int id = _nextEnemyId++;
            var e = _em.CreateEntity(_enemyEntityArchetype);
            _em.SetComponentData(e, new EnemyPos { Value = pos });
            _em.SetComponentData(e, new EnemyHp { Value = arch.MaxHp * _waveStatScale });
            _em.SetComponentData(e, new EnemyMeta { Id = id, ArchIndex = archIndex, StatScale = _waveStatScale });
            EnemySpawned?.Invoke(new EnemySpawnedEvent(id, arch.Id, new Vector2(pos.x, pos.y)));
        }

        // ── 每帧演算 ───────────────────────────────────────────────────────

        private void MoveAndDetonate(float dt)
        {
            var wf = _setup.WreckField;
            var job = new MoveJob
            {
                Dt = dt,
                PlayerRadius = _player.Radius,
                Archetypes = _archetypes,
                PosHandle = _em.GetComponentTypeHandle<EnemyPos>(false),
                MetaHandle = _em.GetComponentTypeHandle<EnemyMeta>(true),
                EntityHandle = _em.GetEntityTypeHandle(),
                Detonations = _detonations.AsParallelWriter(),
                WreckEnabled = _wreckEnabled,
                WreckHalf = _wreckGridHalf,
                WreckCellSize = wf.CellSize,
                WreckDim = _wreckGridDim,
                WreckSlowPer = wf.SlowPerCount,
                WreckSlowFloor = wf.SlowFloor,
                WreckCells = _wreckCells,
            };
            job.ScheduleParallel(_query, default).Complete();

            if (_detonations.IsEmpty()) return;
            _detScratch.Clear();
            while (_detonations.TryDequeue(out var d)) _detScratch.Add(d);
            // 同帧自爆按实例 id 升序结算（两后端共同契约：事件流与泥地记账顺序可复现；
            // 每次扣血独立 max(0,·) 收敛，聚合结果与次序无关）。
            _detScratch.Sort(static (a, b) => a.Id.CompareTo(b.Id));

            var toDestroy = new NativeArray<Entity>(_detScratch.Count, Allocator.Temp);
            for (int i = 0; i < _detScratch.Count; i++)
            {
                var d = _detScratch[i];
                var arch = _archetypes[d.ArchIndex];
                float dmg = arch.Attack * d.StatScale;
                _playerHp = Math.Max(0f, _playerHp - dmg);
                AddWreckManaged(d.Pos, d.ArchIndex);
                toDestroy[i] = d.Entity;
                EnemyDetonated?.Invoke(new EnemyDetonatedEvent(d.Id, arch.Id, new Vector2(d.Pos.x, d.Pos.y), dmg, _playerHp));
            }
            _em.DestroyEntity(toDestroy);
            toDestroy.Dispose();
        }

        // 残骸落定（自爆侧，主线程）：与 ProjectileJob.AddWreck / 参考实现 AddWreck 逐行同式。
        private void AddWreckManaged(float2 eventPos, int archIndex)
        {
            if (_wreckEnabled == 0) return;
            var st = _wreckState.Value;
            int seq = st.z++;
            SimMath.WreckRestOffset(eventPos.x, eventPos.y, _archetypes[archIndex].Radius, seq, out float ox, out float oy);
            var pos = new float2(eventPos.x + ox, eventPos.y + oy);
            int cell = SimMath.WreckCellIndex(pos.x, pos.y, _wreckGridHalf, _setup.WreckField.CellSize, _wreckGridDim);

            int slot = st.x;
            if (st.y == _wreckPos.Length) _wreckCells[_wreckCellSlot[slot]]--;
            else st.y++;
            _wreckPos[slot] = pos;
            _wreckArch[slot] = archIndex;
            _wreckDrift[slot] = 0f;
            _wreckSeq[slot] = seq;
            _wreckCellSlot[slot] = cell;
            st.x = (st.x + 1) % _wreckPos.Length;
            _wreckCells[cell]++;
            _wreckState.Value = st;
        }

        // 推挤相位：重建敌人占位网格（单线程 job）→ 逐槽并行推挤 → 主线程回放跨格记账（加减可交换，顺序无关）。
        private void TickWreckPush(float dt)
        {
            if (_wreckEnabled == 0 || _aliveCount == 0) return;
            var wf = _setup.WreckField;
            if (wf.PushSpeed <= 0f) return;
            int slots = _wreckState.Value.y;
            if (slots == 0) return;

            _enemyCellItems.ResizeUninitialized(_aliveCount);
            new BuildEnemyGridJob
            {
                EnemyPos = _snapPos.AsArray(),
                Count = _aliveCount,
                GridHalf = _enemyGridHalf,
                GridCellSize = _enemyGridCellSize,
                GridDim = _enemyGridDim,
                CellCount = _enemyCellCount,
                CellStart = _enemyCellStart,
                Items = _enemyCellItems.AsArray(),
            }.Schedule().Complete();

            new WreckPushJob
            {
                MaxStep = wf.PushSpeed * dt,
                MaxDrift = wf.PushMaxDrift,
                RecoverDt = wf.DriftRecoverPerSecond > 0f ? wf.DriftRecoverPerSecond * dt : 0f,
                Archetypes = _archetypes,
                EnemyPos = _snapPos.AsArray(),
                EnemyMeta = _snapMeta.AsArray(),
                CellCount = _enemyCellCount,
                CellStart = _enemyCellStart,
                Items = _enemyCellItems.AsArray(),
                GridHalf = _enemyGridHalf,
                GridCellSize = _enemyGridCellSize,
                GridDim = _enemyGridDim,
                WreckPos = _wreckPos,
                WreckArch = _wreckArch,
                WreckDrift = _wreckDrift,
                WreckCellSlot = _wreckCellSlot,
                WreckHalf = _wreckGridHalf,
                WreckCellSize = wf.CellSize,
                WreckDim = _wreckGridDim,
                CellChanges = _wreckCellChanges.AsParallelWriter(),
            }.Schedule(slots, 128).Complete();

            while (_wreckCellChanges.TryDequeue(out var ch))
            {
                _wreckCells[ch.x]--;
                _wreckCells[ch.y]++;
            }
        }

        private void TickPlayer(float dt)
        {
            if (_player.RegenPerSecond > 0f)
                _playerHp = Math.Min(_player.MaxHp, _playerHp + _player.RegenPerSecond * dt);

            GatherSnapshot();

            // 射程内最近敌人（Burst 扫描）：既是开火资格判定，也是炮塔回转的目标。
            var scan = new NearestScanJob
            {
                Pos = _snapPos.AsArray(),
                Count = _aliveCount,
                Range = _player.Range,
                BestIndex = _bestRef,
            };
            scan.Schedule().Complete();
            int target = _bestRef.Value;
            if (target < 0)
            {
                // 射程内无目标：冷却不往负累（不积欠账），朝向保持不动。
                if (_playerAttackCooldown < 0f) _playerAttackCooldown = 0f;
                return;
            }

            // 回转与炮口三角函数留在托管侧（理由见类型注释）；击发循环 O(发数) 极轻，与参考实现逐行一致。
            var tpos = _snapPos[target];
            float desired = (float)(Math.Atan2(tpos.y, tpos.x) * Rad2Deg);
            _turretAngleDeg = SimMath.MoveTowardsAngleDeg(_turretAngleDeg, desired, _player.RotationSpeed * dt);

            double muzzleRad = _turretAngleDeg / Rad2Deg;
            var muzzleDir = new float2((float)Math.Cos(muzzleRad), (float)Math.Sin(muzzleRad));
            bool aligned = Math.Abs(SimMath.DeltaAngleDeg(_turretAngleDeg, desired)) <= BattleSimTuning.AimToleranceDeg;
            float effInterval = _player.AttackInterval;
            bool firehose = effInterval < BattleSimTuning.FirehoseFireInterval;

            _playerAttackCooldown -= dt;
            int shots = 0;
            while (_playerAttackCooldown <= 0f && shots < BattleSimTuning.MaxShotsPerTick)
            {
                if (!firehose && !aligned) break; // 点射未对准：本 tick 静默（冷却由下方 clamp 兜住）
                var dir = muzzleDir;
                if (firehose)
                {
                    float off = (float)((_rng.NextDouble() * 2.0 - 1.0) * BattleSimTuning.FirehoseSpreadDeg);
                    double rad = (_turretAngleDeg + off) / Rad2Deg;
                    dir = new float2((float)Math.Cos(rad), (float)Math.Sin(rad));
                }
                _projPos.Add(default);
                _projDir.Add(dir);
                _projDamage.Add(_player.Attack);
                TurretFired?.Invoke(new TurretFiredEvent(new Vector2(dir.x, dir.y)));
                _playerAttackCooldown += effInterval;
                shots++;
            }
            if (_playerAttackCooldown < 0f) _playerAttackCooldown = 0f;
        }

        private void TickProjectiles(float dt)
        {
            if (_projPos.Length == 0) return;

            _hits.Clear();
            _killedEntities.Clear();
            _countRef.Value = _aliveCount;
            _playerRef.Value = new PlayerHitState { Hp = _playerHp, Kills = Kills, Score = Score };

            var wf = _setup.WreckField;
            var job = new ProjectileJob
            {
                Step = _player.ProjectileSpeed * dt,
                ProjRadius = _player.ProjectileRadius,
                DespawnSq = _player.ProjectileDespawnRadius * _player.ProjectileDespawnRadius,
                SplashRadius = _player.SplashRadius,
                SplashDamageScale = _player.SplashDamageScale,
                Archetypes = _archetypes,
                EnemyPos = _snapPos.AsArray(),
                EnemyHp = _snapHp.AsArray(),
                EnemyMeta = _snapMeta.AsArray(),
                EnemyEntities = _snapEntities.AsArray(),
                EnemyCount = _countRef,
                ProjPos = _projPos,
                ProjDir = _projDir,
                ProjDamage = _projDamage,
                Player = _playerRef,
                Hits = _hits,
                KilledEntities = _killedEntities,
                WreckEnabled = _wreckEnabled,
                WreckHalf = _wreckGridHalf,
                WreckCellSize = wf.CellSize,
                WreckDim = _wreckGridDim,
                WreckCells = _wreckCells,
                WreckPos = _wreckPos,
                WreckArch = _wreckArch,
                WreckDrift = _wreckDrift,
                WreckSeq = _wreckSeq,
                WreckCellSlot = _wreckCellSlot,
                WreckState = _wreckState,
            };
            job.Schedule().Complete();

            _aliveCount = _countRef.Value;
            var st = _playerRef.Value;
            _playerHp = st.Hp;
            Kills = st.Kills;
            Score = st.Score;

            // 结构写回：击杀批量销毁（chunk 仍是权威存储，快照数组只是本帧工作/只读视图）。
            if (_killedEntities.Length > 0)
                _em.DestroyEntity(_killedEntities.AsArray());

            // 事件按 job 记录顺序重放（弹着次序 = 弹丸序，与参考实现一致）；幸存者血量稀疏写回 chunk——
            // 同一敌人同帧多次中弹按记录顺序覆盖、后死于后续弹的用 Exists 挡掉（实体已销毁）。
            for (int i = 0; i < _hits.Length; i++)
            {
                var h = _hits[i];
                if (h.Killed == 0 && _em.Exists(h.Entity))
                    _em.SetComponentData(h.Entity, new EnemyHp { Value = h.HpAfter });
                var arch = _archetypes[h.ArchIndex];
                EnemyHit?.Invoke(new EnemyHitEvent(h.EnemyId, arch.Id,
                    new Vector2(h.Impact.x, h.Impact.y), h.Damage, h.Killed != 0, h.Splash));
            }
        }

        private bool CheckDefeat()
        {
            if (_playerHp > 0f) return false;
            _playerHp = 0f;
            Phase = BattlePhase.Defeat;
            return true;
        }

        private void CheckWaveEnd()
        {
            if (_aliveCount > 0) return;
            for (int i = 0; i < _spawns.Length; i++)
                if (_spawns[i].Remaining > 0) return;

            // 波间维修（语义见参考实现 CheckWaveEnd 注释）。在飞弹丸不阻塞清波、随全场冻结，续波后继续飞。
            _playerHp = _player.MaxHp;
            Phase = BattlePhase.WaveCleared;
            WaveCleared?.Invoke(WaveIndex);
        }

        // 从 chunk 收集本帧快照工作数组。ToComponentDataArray 是按 chunk 的批量拷贝；
        // 组件类型与快照元素同尺寸，Reinterpret 零拷贝改视图后整块复制进持久列表。
        private void GatherSnapshot()
        {
            var pos = _query.ToComponentDataArray<EnemyPos>(Allocator.Temp);
            var hp = _query.ToComponentDataArray<EnemyHp>(Allocator.Temp);
            var meta = _query.ToComponentDataArray<EnemyMeta>(Allocator.Temp);
            var ents = _query.ToEntityArray(Allocator.Temp);
            int n = pos.Length;

            _snapPos.ResizeUninitialized(n);
            _snapHp.ResizeUninitialized(n);
            _snapMeta.ResizeUninitialized(n);
            _snapEntities.ResizeUninitialized(n);
            if (n > 0)
            {
                NativeArray<float2>.Copy(pos.Reinterpret<float2>(), _snapPos.AsArray(), n);
                NativeArray<float>.Copy(hp.Reinterpret<float>(), _snapHp.AsArray(), n);
                NativeArray<EnemyMeta>.Copy(meta, _snapMeta.AsArray(), n);
                NativeArray<Entity>.Copy(ents, _snapEntities.AsArray(), n);
            }
            _aliveCount = n;

            pos.Dispose();
            hp.Dispose();
            meta.Dispose();
            ents.Dispose();
        }
    }
}
