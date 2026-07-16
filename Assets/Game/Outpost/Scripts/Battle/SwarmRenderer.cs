using System.Collections.Generic;
using Game.Outpost.Sim;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 敌人海的实例化渲染器：每帧直接从模拟快照批量绘制全部存活敌人（<c>Graphics.DrawMeshInstanced</c>，
    /// 单批上限 1023、按原型网格分批），<b>不为敌人创建任何 GameObject</b>——数千同屏时 per-敌人 GameObject
    /// 的 Transform / 组件开销是主要瓶颈，实例化绘制让表现层与敌人数量近似解耦。
    /// <para>与对象池的分工：<b>海量常驻单位走实例化渲染，少量瞬时特效（脉冲/曳光/飘字）走对象池</b>——
    /// 前者数量大且逐帧全量重算，后者数量小且有独立生命周期。两个 Sim 后端共用本渲染层（对比才公平）。</para>
    /// <para>保留原 per-敌人视觉语义，改为逐实例数值计算：出生弹出、呼吸错相、受击白闪、血量变暗、
    /// 有向形状转向来袭方向。动画状态（出生/白闪时刻）由 <see cref="BattleDirectorSystem"/> 按模拟事件喂入。</para>
    /// <para><b>残骸层 = 模拟槽位镜像</b>（ADR-0032）：残骸是逐实体模拟状态（位置会被敌人拱开、密度记账跟随），
    /// 本层逐帧对照 <see cref="IBattleSim.GetWreckSlot"/> 增量维护静态实例批次——<c>Seq</c> 变 = 槽位换血
    /// （新残骸落定，起一段原地收缩/滚转/冷却动画后定格），<c>Position</c> 变 = 被犁动（原位重写矩阵 + 滚转扰动）。
    /// 矩阵/颜色只在变化时写，每帧直接提交缓存数组、零 CPU 重建；无头快进的战场历史也由镜像自动发现，无需专门烘焙入口。</para>
    /// <para><b>弹丸层</b>：在飞弹丸每帧直接从模拟快照实例化绘制（真弹道下同屏数百上千，逐弹 GameObject 不可行），
    /// 拖尾菱形按飞行方向定向、HDR 亮色触发 Bloom 读成光痕。</para>
    /// <para><b>泥地热力图</b>：按开关叠加绘制<b>模拟侧</b>密度格（读 <see cref="IBattleSim.WreckGrid"/>，
    /// 残骸越密该格越亮），是"残骸是防御地形"这条规则的直读可视化——推挤入模拟后，车辙被踩穿在热力图上直接可见。</para>
    /// </summary>
    public sealed class SwarmRenderer : MonoBehaviour
    {
        [SerializeField, Tooltip("实例化无光照 shader（Outpost/SwarmUnlit，支持 per-instance 颜色）。场景引用保证进包。")]
        private Shader _shader;

        [SerializeField, Tooltip("出生弹出时长（秒），从 0 缩放到目标体型。")]
        private float _popDuration = 0.18f;

        [SerializeField, Tooltip("受击白闪回落时长（秒）。")]
        private float _flashDuration = 0.12f;

        private const int BatchSize = 1023; // DrawMeshInstanced 单批上限
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        // ── 弹丸层 ──────────────────────────────────────────────────────────

        private const float ProjectileZ = -0.3f;       // 单位(0)之前、脉冲(-0.2)之后——弹流叠在敌人海上但不压特效
        private const float ProjectileLength = 0.55f;  // 普通弹拖尾总长（世界单位）：约为单帧位移的 1.4 倍，读成运动光痕。曳光弹在此基础上按档位拉长

        // 曳光弹配色（索引 = ProjectileSnapshot.Tracer 档位）：普通弹青白，第 10/100/1000 发换色，直观展示已射子弹量。
        // 亮度逐级拉高（HDR 值越大 Bloom 溢得越狠）——曳光弹要在满屏弹流里一眼可辨，故明显亮过普通弹与敌人本体色。
        private static readonly Color[] TracerColors =
        {
            new(0.9f, 3.2f, 3.0f, 1f),  // 0 普通：青白（保持原样）
            new(6.0f, 3.2f, 0.7f, 1f),  // 1 每十：炽暖琥珀
            new(1.8f, 7.0f, 1.0f, 1f),  // 2 每百：亮绿金里程碑
            new(8.0f, 1.5f, 7.5f, 1f),  // 3 每千：灼亮品红（全场最醒目）
        };

        // 拖尾长度 / 宽度分开缩放（网格本体：长 1、宽 0.14，尖头朝本地 +X = 飞行方向）。
        // 刻意不等比：等比放大会让曳光弹变成"又长又胖的菱形"，只拉长 + 略加宽才读成一道光痕。
        private static readonly float[] TracerLengthScale = { 1f, 2.6f, 4.2f, 6.0f };
        private static readonly float[] TracerWidthScale = { 1f, 1.5f, 1.9f, 2.3f };

        // ── 泥地热力图 ──────────────────────────────────────────────────────

        /// <summary>泥地热力图开关（战斗 HUD 即时切换；纯表现，读模拟侧密度格绘制）。</summary>
        public bool WreckHeatmapVisible { get; set; }

        private const float HeatmapZ = 0.42f;                                   // 地板(0.5)与地面环(0.3)之间：垫底不遮内容
        private static readonly Color HeatCold = new(0.10f, 0.045f, 0.02f, 1f); // 低密度：暗琥珀（略亮于地板）
        private static readonly Color HeatHot = new(1.8f, 0.55f, 0.12f, 1f);    // 高密度：HDR 橙红（Bloom 发热感）
        private float _heatmapFullCount = 50f; // 亮度饱和密度（director 按配置传入 = 减速到下限所需残骸数）

        // 每敌人的动画状态（出生 / 白闪时刻 + 呼吸相位）——director 按模拟事件增删，键为敌人实例 id。
        private struct UnitAnim
        {
            public float SpawnTime;
            public float FlashUntil;
            public float BreathPhase;
        }

        private readonly Dictionary<int, UnitAnim> _anims = new(4096);
        private Dictionary<int, EnemyVisual> _visuals;
        private Material _material;
        private MaterialPropertyBlock _mpb;

        // ── 残骸层（模拟槽位镜像）───────────────────────────────────────────

        // 落定表现参数（纯 cosmetic）：新残骸原地收缩 / 滚转 / 余烬冷却后定格为静态实例。
        private const float WreckSettleDuration = 0.55f;
        private const float WreckScale = 0.85f;   // 落定后比活体略小，读成塌缩的空壳
        private const float WreckZ = 0.23f;       // 残骸 z 区间起点：活敌(0)之下、地面环(0.3)之上
        private const float WreckZSpread = 0.05f; // 每具随机加深，避免共面残骸 z-fighting 闪烁

        // 已烘焙残骸的静态批次（按原型分组）：矩阵/颜色只在"槽位换血 / 被犁动 / 落定动画"时写，
        // 每帧把缓存数组原样交给 DrawMeshInstanced。Slots 是批内序 → 模拟槽位的反查（换血 swap-remove 时补位用）。
        private sealed class WreckBuffer
        {
            public readonly List<Matrix4x4[]> Matrices = new();
            public readonly List<Vector4[]> Colors = new();
            public readonly List<int> Slots = new();
            public int Count; // 当前批内实例数
        }

        // 槽位镜像（与模拟环形槽位一一对应，按需扩容）：缓存 Seq/Pos 做变化检测，视觉元数据（朝向/深度）随槽存放。
        private int[] _slotSeq = System.Array.Empty<int>();       // 0 = 从未见过（模拟序号从 1 起）
        private Vector2[] _slotPos = System.Array.Empty<Vector2>();
        private int[] _slotArchId = System.Array.Empty<int>();
        private int[] _slotBatch = System.Array.Empty<int>();     // 槽位在其原型批次里的批内序
        private float[] _slotAngle = System.Array.Empty<float>();
        private float[] _slotZ = System.Array.Empty<float>();

        // 落定中的残骸（原地收缩/滚转/冷却动画，逐帧写回批次；Seq 失配 = 槽位已被复写，动画作废）。
        private struct SettlingWreck
        {
            public int Slot;
            public int Seq;
            public float StartTime;
            public float Spin; // 落定全程的总旋转量（度，随动画一起衰减到 0）
        }

        private readonly List<SettlingWreck> _settling = new(256);
        private readonly Dictionary<int, WreckBuffer> _wrecks = new();

        // 分批绘制缓冲（跨帧复用，零逐帧分配）。
        private readonly Matrix4x4[] _matrices = new Matrix4x4[BatchSize];
        private readonly Vector4[] _colors = new Vector4[BatchSize];

        /// <summary>
        /// 战斗开始时由 director 调用：注入表现参数表并准备实例化材质。
        /// <paramref name="heatmapFullCount"/> 是热力图亮度饱和的每格残骸数
        /// （按配置传"减速到下限所需的密度"，热力图亮度与减速规则同刻度）。
        /// </summary>
        public void Init(Dictionary<int, EnemyVisual> visuals, float heatmapFullCount)
        {
            _visuals = visuals;
            _heatmapFullCount = Mathf.Max(1f, heatmapFullCount);

            var shader = _shader != null ? _shader : Shader.Find("Outpost/SwarmUnlit");
            if (shader == null)
            {
                Debug.LogError("[SwarmRenderer] 找不到 Outpost/SwarmUnlit shader（场景引用未接线且 Find 失败），敌人将不可见。");
                return;
            }
            _material = new Material(shader) { enableInstancing = true };
            _mpb = new MaterialPropertyBlock();
        }

        /// <summary>敌人刷出（记录出生时刻，驱动弹出动画；呼吸相位按 id 错开防"齐吸"）。</summary>
        public void OnSpawned(int enemyId)
            => _anims[enemyId] = new UnitAnim
            {
                SpawnTime = Time.time,
                FlashUntil = 0f,
                BreathPhase = enemyId * 2.399963f, // 黄金角错相
            };

        /// <summary>敌人受击（白闪一拍）。</summary>
        public void OnFlash(int enemyId)
        {
            if (!_anims.TryGetValue(enemyId, out var a)) return;
            a.FlashUntil = Time.time + _flashDuration;
            _anims[enemyId] = a;
        }

        /// <summary>敌人离场（击杀 / 自爆），清掉动画状态。残骸不用报——模拟槽位镜像自动发现新落定的残骸。</summary>
        public void OnRemoved(int enemyId) => _anims.Remove(enemyId);

        /// <summary>
        /// 绘制当前帧的战场（director 每帧调用）：泥地热力图（按开关）→ 残骸槽位镜像同步 + 落定动画 → 残骸批次
        /// → 在飞弹丸 → 存活敌人。敌人按原型分组遍历模拟快照、逐实例算矩阵与颜色、满批即提交——
        /// 绘制次数 ≈ 实例数 / 1023 × 网格种数。
        /// </summary>
        public void Render(IBattleSim sim)
        {
            if (_material == null || _visuals == null || sim == null) return;

            float now = Time.time;
            if (WreckHeatmapVisible) DrawWreckHeatmap(sim);
            SyncWrecks(sim, now);
            AdvanceSettling(now);
            DrawWrecks();
            DrawProjectiles(sim);
            int total = sim.EnemyCount;

            // 按原型分批：外层枚举原型（种类少），内层扫全量快照挑出该原型——O(种类×n) 纯数值遍历，
            // 换来每批同网格同材质、无逐帧集合分配。
            foreach (var pair in _visuals)
            {
                int archId = pair.Key;
                var v = pair.Value;
                if (v.Mesh == null) continue;

                int batch = 0;
                for (int i = 0; i < total; i++)
                {
                    var snap = sim.GetEnemy(i);
                    if (snap.ArchetypeId != archId) continue;

                    if (!_anims.TryGetValue(snap.Id, out var anim))
                    {
                        // 理论上 spawn 事件先于快照出现；兜底当作刚出生，避免闪现满尺寸。
                        anim = new UnitAnim { SpawnTime = now, BreathPhase = snap.Id * 2.399963f };
                        _anims[snap.Id] = anim;
                    }

                    _matrices[batch] = BuildMatrix(snap, v, anim, now);
                    _colors[batch] = BuildColor(snap, v, anim, now);
                    batch++;

                    if (batch == BatchSize)
                    {
                        Flush(v.Mesh, batch);
                        batch = 0;
                    }
                }
                if (batch > 0) Flush(v.Mesh, batch);
            }
        }

        private void Flush(Mesh mesh, int count)
        {
            _mpb.SetVectorArray(BaseColorId, _colors);
            Graphics.DrawMeshInstanced(mesh, 0, _material, _matrices, count, _mpb,
                ShadowCastingMode.Off, receiveShadows: false);
        }

        // ── 残骸层内部（模拟槽位镜像）───────────────────────────────────────

        // 逐槽对照模拟快照做增量维护：Seq 变 = 换血（旧居民摘出批次、新残骸入批 + 起落定动画）；
        // Pos 变 = 被敌人犁动（原位重写矩阵 + 槽位交替滚转扰动，读成被碾开）。
        // 全量遍历是纯数值比对（20000 槽 ≪ 1ms），矩阵/颜色只在变化时写。
        private void SyncWrecks(IBattleSim sim, float now)
        {
            int n = sim.WreckSlotCount;
            if (n == 0) return;
            EnsureSlotCapacity(n);

            for (int slot = 0; slot < n; slot++)
            {
                var w = sim.GetWreckSlot(slot);
                if (_slotSeq[slot] != w.Seq)
                {
                    ReplaceSlot(slot, w, now);
                    continue;
                }

                var p = new Vector2(w.Position.X, w.Position.Y);
                var old = _slotPos[slot];
                if (p == old) continue;

                // 被犁动：滚转量随位移走（槽位奇偶定向，读成被碾开而非平移）。
                float moved = Vector2.Distance(old, p);
                _slotPos[slot] = p;
                _slotAngle[slot] += ((slot & 1) == 0 ? 1f : -1f) * moved * 90f;
                if (_visuals.TryGetValue(_slotArchId[slot], out var v))
                    WriteWreckMatrix(slot, v, WreckScale);
            }
        }

        // 槽位换血：摘出旧居民（若有），新残骸写入其原型批次并登记落定动画。
        private void ReplaceSlot(int slot, in WreckSnapshot w, float now)
        {
            if (_slotSeq[slot] != 0) RemoveFromBatch(slot);

            _slotSeq[slot] = w.Seq;
            _slotPos[slot] = new Vector2(w.Position.X, w.Position.Y);
            _slotArchId[slot] = w.ArchetypeId;
            _slotAngle[slot] = Random.Range(0f, 360f);
            _slotZ[slot] = WreckZ + Random.value * WreckZSpread;

            if (!_wrecks.TryGetValue(w.ArchetypeId, out var buf))
                _wrecks[w.ArchetypeId] = buf = new WreckBuffer();
            int b = buf.Count;
            int page = b / BatchSize;
            if (page == buf.Matrices.Count)
            {
                buf.Matrices.Add(new Matrix4x4[BatchSize]);
                buf.Colors.Add(new Vector4[BatchSize]);
            }
            if (b == buf.Slots.Count) buf.Slots.Add(slot);
            else buf.Slots[b] = slot;
            buf.Count++;
            _slotBatch[slot] = b;

            if (_visuals.TryGetValue(w.ArchetypeId, out var v))
            {
                WriteWreckMatrix(slot, v, WreckScale);
                buf.Colors[page][b % BatchSize] = AshColor(v.Color);
            }
            _settling.Add(new SettlingWreck
            {
                Slot = slot,
                Seq = w.Seq,
                StartTime = now,
                Spin = Random.Range(100f, 280f) * (Random.value < 0.5f ? -1f : 1f),
            });
        }

        // 把槽位从其原型批次摘出（末位补位 swap-remove，维护补位者的批内序反查）。
        private void RemoveFromBatch(int slot)
        {
            var buf = _wrecks[_slotArchId[slot]];
            int b = _slotBatch[slot];
            int last = buf.Count - 1;
            if (b != last)
            {
                buf.Matrices[b / BatchSize][b % BatchSize] = buf.Matrices[last / BatchSize][last % BatchSize];
                buf.Colors[b / BatchSize][b % BatchSize] = buf.Colors[last / BatchSize][last % BatchSize];
                int lastSlot = buf.Slots[last];
                buf.Slots[b] = lastSlot;
                _slotBatch[lastSlot] = b;
            }
            buf.Count--;
        }

        // 落定动画推进：原地收缩（活体尺寸 → 残骸尺寸）+ 滚转衰减 + 余烬冷却成灰，写回批次；到时定格为静态。
        private void AdvanceSettling(float now)
        {
            for (int i = _settling.Count - 1; i >= 0; i--)
            {
                var s = _settling[i];
                if (_slotSeq[s.Slot] != s.Seq || !_visuals.TryGetValue(_slotArchId[s.Slot], out var v))
                {
                    // 槽位已被环形复写：动画作废（新居民有自己的动画条目）。
                    _settling[i] = _settling[^1];
                    _settling.RemoveAt(_settling.Count - 1);
                    continue;
                }

                float t = (now - s.StartTime) / WreckSettleDuration;
                var buf = _wrecks[_slotArchId[s.Slot]];
                int b = _slotBatch[s.Slot];
                if (t >= 1f)
                {
                    WriteWreckMatrix(s.Slot, v, WreckScale);
                    buf.Colors[b / BatchSize][b % BatchSize] = AshColor(v.Color);
                    _settling[i] = _settling[^1];
                    _settling.RemoveAt(_settling.Count - 1);
                    continue;
                }

                float ease = 1f - (1f - t) * (1f - t); // easeOutQuad：滚转 / 收缩同步减速
                float baseAngle = _slotAngle[s.Slot];  // 最终朝向不变，动画期以滚转偏移画（被犁动的扰动也叠在其上）
                var pos = _slotPos[s.Slot];
                buf.Matrices[b / BatchSize][b % BatchSize] = Matrix4x4.TRS(
                    new Vector3(pos.x, pos.y, _slotZ[s.Slot]),
                    Quaternion.Euler(0f, 0f, baseAngle - s.Spin * (1f - ease)),
                    Vector3.one * (v.Diameter * Mathf.Lerp(1f, WreckScale, t)));
                var ember = v.Color * 0.55f; // 余烬起色：明显暗于活体、亮于最终灰
                ember.a = 1f;
                buf.Colors[b / BatchSize][b % BatchSize] = Color.Lerp(ember, AshColor(v.Color), t);
            }
        }

        // 残骸绘制：静态批次直接提交缓存数组（零重建；落定中的少数实例由 AdvanceSettling 原位改写同一批次）。
        private void DrawWrecks()
        {
            foreach (var pair in _wrecks)
            {
                if (!_visuals.TryGetValue(pair.Key, out var v) || v.Mesh == null) continue;
                var buf = pair.Value;
                for (int b = 0; b * BatchSize < buf.Count; b++)
                {
                    int n = Mathf.Min(BatchSize, buf.Count - b * BatchSize);
                    _mpb.SetVectorArray(BaseColorId, buf.Colors[b]);
                    Graphics.DrawMeshInstanced(v.Mesh, 0, _material, buf.Matrices[b], n, _mpb,
                        ShadowCastingMode.Off, receiveShadows: false);
                }
            }
        }

        // 按槽位镜像数据写静态矩阵（位置 / 朝向 / 深度随槽存放）。
        private void WriteWreckMatrix(int slot, in EnemyVisual v, float scale)
        {
            var buf = _wrecks[_slotArchId[slot]];
            int b = _slotBatch[slot];
            var pos = _slotPos[slot];
            buf.Matrices[b / BatchSize][b % BatchSize] = Matrix4x4.TRS(
                new Vector3(pos.x, pos.y, _slotZ[slot]),
                Quaternion.Euler(0f, 0f, _slotAngle[slot]),
                Vector3.one * (v.Diameter * scale));
        }

        private void EnsureSlotCapacity(int n)
        {
            if (_slotSeq.Length >= n) return;
            int cap = Mathf.Max(4096, Mathf.NextPowerOfTwo(n));
            System.Array.Resize(ref _slotSeq, cap);
            System.Array.Resize(ref _slotPos, cap);
            System.Array.Resize(ref _slotArchId, cap);
            System.Array.Resize(ref _slotBatch, cap);
            System.Array.Resize(ref _slotAngle, cap);
            System.Array.Resize(ref _slotZ, cap);
        }

        // ── 弹丸层内部 ──────────────────────────────────────────────────────

        // 在飞弹丸的实例化绘制：拖尾菱形按飞行方向定向，全弹同色同网格（分批只按 1023 上限切）。
        private void DrawProjectiles(IBattleSim sim)
        {
            int count = sim.ProjectileCount;
            if (count == 0) return;
            var mesh = OutpostMeshes.Projectile;
            int batch = 0;
            for (int i = 0; i < count; i++)
            {
                var p = sim.GetProjectile(i);
                int tier = p.Tracer; // 0..3；曳光弹换色 + 拉长拖尾
                float len = ProjectileLength * TracerLengthScale[tier];
                float ang = Mathf.Atan2(p.Direction.Y, p.Direction.X) * Mathf.Rad2Deg;

                // 拖尾往后长、弹头钉在弹丸真实位置：网格是中心对称的，等中心摆放会让长曳光的尖头戳到弹丸前方
                // （视觉上先于弹着点命中）。故把网格中心沿飞行方向回退半个"超出普通弹的长度"——
                // tier 0 回退量恰为 0，普通弹与改动前逐像素一致。
                float back = (len - ProjectileLength) * 0.5f;
                var pos = new Vector3(p.Position.X - p.Direction.X * back, p.Position.Y - p.Direction.Y * back, ProjectileZ);
                _matrices[batch] = Matrix4x4.TRS(pos, Quaternion.Euler(0f, 0f, ang),
                    new Vector3(len, ProjectileLength * TracerWidthScale[tier], 1f)); // z 任意：网格是平的
                _colors[batch] = TracerColors[tier];
                batch++;
                if (batch == BatchSize)
                {
                    Flush(mesh, batch);
                    batch = 0;
                }
            }
            if (batch > 0) Flush(mesh, batch);
        }

        // ── 泥地热力图内部 ──────────────────────────────────────────────────

        // 模拟侧密度格直读绘制：非零格画一块贴地色块，密度越高越亮（√ 曲线抬低密度可见性），
        // 亮度在 _heatmapFullCount（按配置 = 减速到下限所需密度）处饱和——热力图刻度即减速刻度。
        private void DrawWreckHeatmap(IBattleSim sim)
        {
            var grid = sim.WreckGrid;
            if (grid.Dim <= 0) return;
            var mesh = OutpostMeshes.UnitQuad;
            var scale = new Vector3(grid.CellSize * 0.92f, grid.CellSize * 0.92f, 1f); // 留缝，读成网格而非色斑
            int cells = grid.Dim * grid.Dim;
            int batch = 0;
            for (int i = 0; i < cells; i++)
            {
                int c = sim.GetWreckCellCount(i);
                if (c <= 0) continue;
                float x = (i % grid.Dim + 0.5f) * grid.CellSize - grid.Half;
                float y = (i / grid.Dim + 0.5f) * grid.CellSize - grid.Half;
                float t = Mathf.Sqrt(Mathf.Clamp01(c / _heatmapFullCount));
                _matrices[batch] = Matrix4x4.TRS(new Vector3(x, y, HeatmapZ), Quaternion.identity, scale);
                _colors[batch] = Color.Lerp(HeatCold, HeatHot, t);
                batch++;
                if (batch == BatchSize)
                {
                    Flush(mesh, batch);
                    batch = 0;
                }
            }
            if (batch > 0) Flush(mesh, batch);
        }

        // 余烬色：原型色压向灰再整体压暗——保留一点色相供辨认"这片死的是什么"，亮度垫在地板与活体之间。
        private static Color AshColor(Color c)
        {
            float luma = c.r * 0.3f + c.g * 0.6f + c.b * 0.1f;
            var ash = Color.Lerp(c, new Color(luma, luma, luma, 1f), 0.55f) * 0.16f;
            ash.a = 1f;
            return ash;
        }

        // 位置 + 朝向（有向形状指向哨站）+ 缩放（体型 × 出生弹出 × 呼吸）。
        private Matrix4x4 BuildMatrix(in EnemySnapshot snap, in EnemyVisual v, in UnitAnim anim, float now)
        {
            var pos = new Vector3(snap.Position.X, snap.Position.Y, 0f);

            var rot = Quaternion.identity;
            if (v.FaceTravel && (snap.Position.X != 0f || snap.Position.Y != 0f))
            {
                // 敌人径直冲原点：来袭方向 = -pos。
                float angle = Mathf.Atan2(-snap.Position.Y, -snap.Position.X) * Mathf.Rad2Deg;
                rot = Quaternion.Euler(0f, 0f, angle);
            }

            float pop = 1f;
            float age = now - anim.SpawnTime;
            if (age < _popDuration)
            {
                float t = Mathf.Clamp01(age / _popDuration);
                pop = 1f + 1.7f * Mathf.Pow(t - 1f, 3f) + 0.7f * Mathf.Pow(t - 1f, 2f); // easeOutBack 轻微过冲
                if (pop < 0f) pop = 0f;
            }
            float breath = 1f + 0.035f * Mathf.Sin(now * 3.2f + anim.BreathPhase);

            // 体型 = 原型直径 × 该实例随机体型系数（模拟侧碰撞半径/血量已按同一系数放大，视觉与判定一致）。
            return Matrix4x4.TRS(pos, rot, Vector3.one * (v.Diameter * snap.SizeScale * pop * breath));
        }

        // 血量越低越暗（保底约 1/4 亮度），受击白闪向亮白抬升（HDR 配合 Bloom 出闪光感）。
        private Vector4 BuildColor(in EnemySnapshot snap, in EnemyVisual v, in UnitAnim anim, float now)
        {
            float hpRatio = snap.MaxHp > 0f ? Mathf.Clamp01(snap.Hp / snap.MaxHp) : 0f;
            var c = Color.Lerp(v.Color * 0.35f, v.Color, 0.25f + 0.75f * hpRatio);
            float flash = anim.FlashUntil > now ? (anim.FlashUntil - now) / _flashDuration : 0f;
            if (flash > 0f) c = Color.Lerp(c, new Color(2f, 2f, 2f, 1f), flash * 0.85f);
            c.a = 1f;
            return c;
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
