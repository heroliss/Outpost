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
    /// <para><b>残骸层</b>：死亡不是消失——每具尸体短促落定后烘焙进<b>静态实例批次</b>永久留存（环形上限复写），
    /// 战场地面逐渐积出击杀分布的"历史地图"。这既是千级击杀率下的可读反馈（爆炸特效有每帧预算、残骸没有），
    /// 也是实例化渲染的持续压力源：数万静态实例的矩阵/颜色只在落定时写一次，每帧直接提交缓存数组、零 CPU 重建。</para>
    /// <para><b>弹丸层</b>：在飞弹丸每帧直接从模拟快照实例化绘制（真弹道下同屏数百上千，逐弹 GameObject 不可行），
    /// 拖尾菱形按飞行方向定向、HDR 亮色触发 Bloom 读成光痕。</para>
    /// <para><b>残骸互动（纯表现）</b>：推挤通道让存活敌人把身旁已烘焙残骸拱开——邻格查询走表现层残骸网格、
    /// 每帧推挤预算限流（超出轮转到下帧）、单具累计漂移有上限（小于模拟密度格边长，表现位移不动摇模拟侧记账所在格）；
    /// 泥地热力图按开关叠加绘制<b>模拟侧</b>密度格（读 <see cref="IBattleSim.WreckGrid"/>，残骸越密该格越亮），
    /// 是"残骸是防御地形"这条规则的直读可视化。</para>
    /// </summary>
    public sealed class SwarmRenderer : MonoBehaviour
    {
        [SerializeField, Tooltip("实例化无光照 shader（Outpost/SwarmUnlit，支持 per-instance 颜色）。场景引用保证进包。")]
        private Shader _shader;

        [SerializeField, Tooltip("出生弹出时长（秒），从 0 缩放到目标体型。")]
        private float _popDuration = 0.18f;

        [SerializeField, Tooltip("受击白闪回落时长（秒）。")]
        private float _flashDuration = 0.12f;

        [SerializeField, Tooltip("残骸留存上限（每原型一个环形缓冲，写满后从最老的开始覆盖；0 = 关闭残骸层）。")]
        private int _wreckCap = 30000;

        private const int BatchSize = 1023; // DrawMeshInstanced 单批上限
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        // ── 弹丸层 ──────────────────────────────────────────────────────────

        private const float ProjectileZ = -0.3f;       // 单位(0)之前、脉冲(-0.2)之后——弹流叠在敌人海上但不压特效
        private const float ProjectileLength = 0.55f;  // 拖尾总长（世界单位）：约为单帧位移的 1.4 倍，读成运动光痕
        private static readonly Color ProjectileColor = new(0.9f, 3.2f, 3.0f, 1f); // HDR 青白（Bloom 辉光）

        // ── 泥地热力图 ──────────────────────────────────────────────────────

        /// <summary>泥地热力图开关（设置窗即时生效；纯表现，读模拟侧密度格绘制）。</summary>
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

        // ── 残骸层 ──────────────────────────────────────────────────────────

        // 残骸落定参数（纯表现）：击毁瞬间沿弹道向外滑出、旋转衰减、余烬冷却成灰，之后烘焙为静态实例。
        private const float WreckSettleDuration = 0.55f;
        private const float WreckScale = 0.85f;   // 落定后比活体略小，读成塌缩的空壳
        private const float WreckZ = 0.23f;       // 残骸 z 区间起点：活敌(0)之下、地面环(0.3)之上
        private const float WreckZSpread = 0.05f; // 每具随机加深，避免共面残骸 z-fighting 闪烁

        // 落定中的残骸（击毁后 WreckSettleDuration 内逐帧插值，之后烘焙进静态批次不再计算）。
        private struct SettlingWreck
        {
            public int ArchetypeId;
            public Vector2 Pos;       // 死亡点
            public Vector2 SlideDir;  // 滑出方向（弹道方向 + 随机偏角）
            public float SlideDist;
            public float StartTime;
            public float BaseAngle;   // 死亡时的朝向（度，与活体一致）
            public float Spin;        // 落定全程的总旋转量（度，随滑出一起衰减）
            public float Z;
        }

        // 已落定残骸的静态烘焙批次：矩阵/颜色只在入队时写一次，逐帧把缓存数组原样交给 DrawMeshInstanced
        //（推挤通道是唯一的事后改写方：原位重写被拱动残骸的矩阵，数组对象不变、提交路径零改动）。
        // 环形复写：写满 _wreckCap 后从最老的槽位覆盖——个体被替换在数万残骸的战场上几乎不可察觉。
        // Pos/Angle/Z/Drift 是按槽位对齐的推挤工作数据（slot = 批次序 × BatchSize + 批内序）。
        private sealed class WreckBuffer
        {
            public readonly List<Matrix4x4[]> Matrices = new();
            public readonly List<Vector4[]> Colors = new();
            public readonly List<Vector2> Pos = new();
            public readonly List<float> Angle = new();
            public readonly List<float> Z = new();
            public readonly List<float> Drift = new(); // 累计漂移（≥ PushMaxDrift 后不再被推）
            public int Count; // 当前留存数（≤ 上限）
            public int Next;  // 环形写入游标
        }

        private readonly List<SettlingWreck> _settling = new(256);
        private readonly Dictionary<int, WreckBuffer> _wrecks = new();
        private int _bakedWreckCount; // 已落定总数（性能行展示；环形复写后停在上限）

        // ── 推挤通道（纯表现）───────────────────────────────────────────────

        // 漂移上限刻意小于模拟密度格边长（1.0）：残骸怎么被拱都不会离开模拟记账所在格的邻域，
        // 泥地减速的"看见尸堆=看见减速区"直觉不被表现位移破坏；也保证拱动只是让路、不会清出通道。
        private const float PushMaxDrift = 0.8f;
        private const float PushCellSize = 1.0f;   // 表现残骸网格边长（与模拟格无关，只服务邻格查询）
        // 每帧「检视预算」：残骸怎么处置的成本在逐具距离判定上（不在实际拱动上）——成熟战场里大量残骸已到漂移上限、
        // 只被判定不被推。预算按<b>检视一具残骸</b>扣（无论是否真推），才能真正封顶最坏开销；超出从轮转游标续到下帧，
        // 海量敌人下所有个体轮流获得推挤机会。8000 次距离判定/帧 ≪ 1ms，与「敌×邻格残骸」无界扫描相比恒定可控。
        private const int PushScanBudgetPerFrame = 8000;
        private const float PushStep = 0.45f;      // 每次拱动吃掉的重叠比例（<1：多帧渐推，读成挤开而非弹飞）

        // 表现残骸网格：cell → 该格内已烘焙残骸的 (原型 id, 槽位)。烘焙登记、环形复写换血、推挤跨格时迁移。
        private List<(int arch, int slot)>[] _pushGrid;
        private int _pushGridDim;
        private float _pushGridHalf;
        private int _pushCursor; // 敌人轮转起点：本帧预算耗尽时，下帧从这里继续（所有敌人轮流获得推挤机会）

        // 分批绘制缓冲（跨帧复用，零逐帧分配）。
        private readonly Matrix4x4[] _matrices = new Matrix4x4[BatchSize];
        private readonly Vector4[] _colors = new Vector4[BatchSize];

        /// <summary>
        /// 战斗开始时由 director 调用：注入表现参数表并准备实例化材质与推挤网格。
        /// <paramref name="arenaRadius"/> 定表现残骸网格的覆盖范围；<paramref name="heatmapFullCount"/> 是
        /// 热力图亮度饱和的每格残骸数（按配置传"减速到下限所需的密度"，热力图亮度与减速规则同刻度）。
        /// </summary>
        public void Init(Dictionary<int, EnemyVisual> visuals, float arenaRadius, float heatmapFullCount)
        {
            _visuals = visuals;
            _heatmapFullCount = Mathf.Max(1f, heatmapFullCount);

            // 表现残骸网格覆盖 ±(场地+3)：残骸出生点在场内，滑出与推挤都不会越过这个边距。
            _pushGridHalf = arenaRadius + 3f;
            _pushGridDim = Mathf.Max(1, Mathf.CeilToInt(_pushGridHalf * 2f / PushCellSize));
            _pushGrid = new List<(int, int)>[_pushGridDim * _pushGridDim];
            _pushCursor = 0;

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

        /// <summary>敌人离场（击杀 / 自爆），清掉动画状态。</summary>
        public void OnRemoved(int enemyId) => _anims.Remove(enemyId);

        /// <summary>
        /// 敌人死亡（拦截击毁 / 抵达自爆）：起一具落定中的残骸——沿弹道方向短促滑出、旋转衰减、余烬冷却，
        /// 之后烘焙进静态批次永久留存。残骸不占演出预算：千级击杀率下它就是"每次击杀都可见"的反馈本体。
        /// </summary>
        public void SpawnWreck(int archetypeId, System.Numerics.Vector2 position)
        {
            if (_wreckCap <= 0 || _visuals == null || !_visuals.TryGetValue(archetypeId, out var v)) return;
            var pos = new Vector2(position.X, position.Y);
            float travel = Mathf.Atan2(pos.y, pos.x);            // 弹道方向 = 从哨站（原点）指向死亡点
            float slideAng = travel + Random.Range(-0.6f, 0.6f); // ±34° 偏角：堆积不呈严格放射线
            _settling.Add(new SettlingWreck
            {
                ArchetypeId = archetypeId,
                Pos = pos,
                SlideDir = new Vector2(Mathf.Cos(slideAng), Mathf.Sin(slideAng)),
                SlideDist = v.Diameter * Random.Range(0.4f, 1.1f),
                StartTime = Time.time,
                BaseAngle = (travel + Mathf.PI) * Mathf.Rad2Deg, // 死亡瞬间仍朝向哨站（与活体一致）
                Spin = Random.Range(100f, 280f) * (Random.value < 0.5f ? -1f : 1f),
                Z = WreckZ + Random.value * WreckZSpread,
            });
        }

        /// <summary>直接烘焙一具已落定的残骸（跳过落定动画，朝向随机）。供无头快进把跳过波次的击杀铺成战场历史。</summary>
        public void BakeWreckInstant(int archetypeId, System.Numerics.Vector2 position)
        {
            if (_wreckCap <= 0 || _visuals == null || !_visuals.TryGetValue(archetypeId, out var v)) return;
            var pos = new Vector2(position.X, position.Y);
            float ang = Mathf.Atan2(pos.y, pos.x) + Random.Range(-0.6f, 0.6f);
            pos += new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * (v.Diameter * Random.Range(0.4f, 1.1f));
            BakeWreck(archetypeId, v, pos, WreckZ + Random.value * WreckZSpread, Random.Range(0f, 360f));
        }

        /// <summary>当前留存的残骸总数（含落定中的）。性能行展示——它是实例化渲染压力的主要持续来源。</summary>
        public int WreckCount => _bakedWreckCount + _settling.Count;

        /// <summary>
        /// 绘制当前帧的战场（director 每帧调用）：泥地热力图（按开关）→ 残骸 → 存活敌人 → 在飞弹丸，
        /// 随后跑一轮推挤（存活敌人拱开身旁残骸，预算限流）。敌人按原型分组遍历模拟快照、
        /// 逐实例算矩阵与颜色、满批即提交——绘制次数 ≈ 实例数 / 1023 × 网格种数。
        /// </summary>
        public void Render(IBattleSim sim)
        {
            if (_material == null || _visuals == null || sim == null) return;

            float now = Time.time;
            if (WreckHeatmapVisible) DrawWreckHeatmap(sim);
            PromoteSettledWrecks(now);
            DrawWrecks(now);
            DrawProjectiles(sim);
            PushWrecksByEnemies(sim);
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

        // ── 残骸层内部 ──────────────────────────────────────────────────────

        // 落定到时的残骸烘焙进静态批次并移出动画列表（倒序 swap-remove，顺序无意义）。
        private void PromoteSettledWrecks(float now)
        {
            for (int i = _settling.Count - 1; i >= 0; i--)
            {
                var w = _settling[i];
                if (now - w.StartTime < WreckSettleDuration) continue;
                if (_visuals.TryGetValue(w.ArchetypeId, out var v))
                    BakeWreck(w.ArchetypeId, v, w.Pos + w.SlideDir * w.SlideDist, w.Z, w.BaseAngle + w.Spin);
                _settling[i] = _settling[^1];
                _settling.RemoveAt(_settling.Count - 1);
            }
        }

        // 残骸绘制：静态批次直接提交缓存数组（零重建）；落定中的少量残骸逐帧插值（滑出/旋转/冷却同衰减）。
        private void DrawWrecks(float now)
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

            if (_settling.Count == 0) return;
            foreach (var pair in _visuals) // 按原型分批，与活敌同一套姿势
            {
                int archId = pair.Key;
                var v = pair.Value;
                if (v.Mesh == null) continue;

                int batch = 0;
                for (int i = 0; i < _settling.Count; i++)
                {
                    var w = _settling[i];
                    if (w.ArchetypeId != archId) continue;

                    float t = Mathf.Clamp01((now - w.StartTime) / WreckSettleDuration);
                    float ease = 1f - (1f - t) * (1f - t); // easeOutQuad：滑出 / 旋转同步减速
                    var p = w.Pos + w.SlideDir * (w.SlideDist * ease);
                    _matrices[batch] = Matrix4x4.TRS(new Vector3(p.x, p.y, w.Z),
                        Quaternion.Euler(0f, 0f, w.BaseAngle + w.Spin * ease),
                        Vector3.one * (v.Diameter * Mathf.Lerp(1f, WreckScale, t)));
                    var ember = v.Color * 0.55f; // 余烬起色：明显暗于活体、亮于最终灰
                    ember.a = 1f;
                    _colors[batch] = Color.Lerp(ember, AshColor(v.Color), t);
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

        // 把一具残骸写进该原型的环形批次（矩阵/颜色此后只被推挤通道原位改写），并登记进推挤网格。
        private void BakeWreck(int archetypeId, in EnemyVisual v, Vector2 pos, float z, float angleDeg)
        {
            if (!_wrecks.TryGetValue(archetypeId, out var buf))
                _wrecks[archetypeId] = buf = new WreckBuffer();

            int slot = buf.Next;
            int b = slot / BatchSize;
            int k = slot % BatchSize;
            if (b == buf.Matrices.Count)
            {
                buf.Matrices.Add(new Matrix4x4[BatchSize]);
                buf.Colors.Add(new Vector4[BatchSize]);
            }
            buf.Matrices[b][k] = Matrix4x4.TRS(new Vector3(pos.x, pos.y, z),
                Quaternion.Euler(0f, 0f, angleDeg), Vector3.one * (v.Diameter * WreckScale));
            buf.Colors[b][k] = AshColor(v.Color);

            // 推挤工作数据按槽位对齐；环形复写 = 旧残骸换血，先把旧登记摘出网格。
            if (slot < buf.Pos.Count)
            {
                PushGridRemove(PushCellOf(buf.Pos[slot]), archetypeId, slot);
                buf.Pos[slot] = pos;
                buf.Angle[slot] = angleDeg;
                buf.Z[slot] = z;
                buf.Drift[slot] = 0f;
            }
            else
            {
                buf.Pos.Add(pos);
                buf.Angle.Add(angleDeg);
                buf.Z.Add(z);
                buf.Drift.Add(0f);
            }
            PushGridAdd(PushCellOf(pos), archetypeId, slot);

            buf.Next = (buf.Next + 1) % _wreckCap;
            if (buf.Count < _wreckCap)
            {
                buf.Count++;
                _bakedWreckCount++;
            }
        }

        // ── 推挤通道内部 ────────────────────────────────────────────────────

        // 表现残骸网格的格索引（越界钳边；与模拟侧密度格同式但互不相关——这张网格只服务邻格查询）。
        private int PushCellOf(Vector2 p)
        {
            int ix = Mathf.Clamp(Mathf.FloorToInt((p.x + _pushGridHalf) / PushCellSize), 0, _pushGridDim - 1);
            int iy = Mathf.Clamp(Mathf.FloorToInt((p.y + _pushGridHalf) / PushCellSize), 0, _pushGridDim - 1);
            return iy * _pushGridDim + ix;
        }

        private void PushGridAdd(int cell, int arch, int slot)
        {
            var list = _pushGrid[cell];
            if (list == null) _pushGrid[cell] = list = new List<(int, int)>(8);
            list.Add((arch, slot));
        }

        private void PushGridRemove(int cell, int arch, int slot)
        {
            var list = _pushGrid[cell];
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].arch != arch || list[i].slot != slot) continue;
                list[i] = list[^1];
                list.RemoveAt(list.Count - 1);
                return;
            }
        }

        // 存活敌人拱开身旁残骸：从轮转游标起逐敌查所在格 ±1 的已烘焙残骸，体积重叠即沿"敌→残骸"方向
        // 推掉部分重叠并原位重写矩阵（带槽位交替的滚转扰动，读成被碾开）。每帧「检视残骸」次数有预算（见常量），
        // 耗尽停在当前敌人、下帧从游标续——海量敌人时所有个体轮流获得推挤机会，不会某一片永远推不动。
        private void PushWrecksByEnemies(IBattleSim sim)
        {
            if (_pushGrid == null || _bakedWreckCount == 0) return;
            int total = sim.EnemyCount;
            if (total == 0) return;

            int budget = PushScanBudgetPerFrame;
            if (_pushCursor >= total) _pushCursor = 0;
            int start = _pushCursor;
            for (int n = 0; n < total && budget > 0; n++)
            {
                int idx = start + n;
                if (idx >= total) idx -= total;
                var snap = sim.GetEnemy(idx);
                if (!_visuals.TryGetValue(snap.ArchetypeId, out var ev)) continue;
                float er = ev.Diameter * 0.5f;
                var epos = new Vector2(snap.Position.X, snap.Position.Y);

                int cx = Mathf.Clamp(Mathf.FloorToInt((epos.x + _pushGridHalf) / PushCellSize), 0, _pushGridDim - 1);
                int cy = Mathf.Clamp(Mathf.FloorToInt((epos.y + _pushGridHalf) / PushCellSize), 0, _pushGridDim - 1);
                for (int oy = -1; oy <= 1; oy++)
                {
                    int gy = cy + oy;
                    if (gy < 0 || gy >= _pushGridDim) continue;
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int gx = cx + ox;
                        if (gx < 0 || gx >= _pushGridDim) continue;
                        var list = _pushGrid[gy * _pushGridDim + gx];
                        if (list == null) continue;
                        for (int i = list.Count - 1; i >= 0 && budget > 0; i--)
                        {
                            budget--; // 检视一具残骸即一次距离判定＝成本单位，无论是否真推（封顶最坏扫描开销）
                            var (arch, slot) = list[i];
                            var buf = _wrecks[arch];
                            float drift = buf.Drift[slot];
                            if (drift >= PushMaxDrift) continue; // 漂移到顶：拱不动了（保持格位可信，见常量注释）
                            if (!_visuals.TryGetValue(arch, out var wv)) continue;

                            var wpos = buf.Pos[slot];
                            var d = wpos - epos;
                            float contact = er + wv.Diameter * WreckScale * 0.5f;
                            float distSq = d.sqrMagnitude;
                            if (distSq >= contact * contact) continue;

                            float dist = Mathf.Sqrt(distSq);
                            // 完全重合（弹着点就是死亡点的常见情形）：沿敌人来袭的径向让开。
                            var dir = dist > 1e-4f ? d / dist
                                : (epos.sqrMagnitude > 1e-4f ? epos.normalized : Vector2.right);
                            float move = Mathf.Min((contact - dist) * PushStep, PushMaxDrift - drift);
                            if (move <= 0.005f) continue;

                            wpos += dir * move;
                            buf.Pos[slot] = wpos;
                            buf.Drift[slot] = drift + move;
                            float ang = buf.Angle[slot] + ((slot & 1) == 0 ? 1f : -1f) * move * 90f;
                            buf.Angle[slot] = ang;
                            buf.Matrices[slot / BatchSize][slot % BatchSize] = Matrix4x4.TRS(
                                new Vector3(wpos.x, wpos.y, buf.Z[slot]),
                                Quaternion.Euler(0f, 0f, ang),
                                Vector3.one * (wv.Diameter * WreckScale));

                            // 跨格迁移登记（漂移上限 < 格边长，至多挪进邻格）。
                            int newCell = PushCellOf(wpos);
                            if (newCell != gy * _pushGridDim + gx)
                            {
                                list[i] = list[^1];
                                list.RemoveAt(list.Count - 1);
                                PushGridAdd(newCell, arch, slot);
                            }
                        }
                    }
                }
                _pushCursor = idx + 1;
            }
        }

        // ── 弹丸层内部 ──────────────────────────────────────────────────────

        // 在飞弹丸的实例化绘制：拖尾菱形按飞行方向定向，全弹同色同网格（分批只按 1023 上限切）。
        private void DrawProjectiles(IBattleSim sim)
        {
            int count = sim.ProjectileCount;
            if (count == 0) return;
            var mesh = OutpostMeshes.Projectile;
            var scale = Vector3.one * ProjectileLength;
            int batch = 0;
            for (int i = 0; i < count; i++)
            {
                var p = sim.GetProjectile(i);
                float ang = Mathf.Atan2(p.Direction.Y, p.Direction.X) * Mathf.Rad2Deg;
                _matrices[batch] = Matrix4x4.TRS(new Vector3(p.Position.X, p.Position.Y, ProjectileZ),
                    Quaternion.Euler(0f, 0f, ang), scale);
                _colors[batch] = ProjectileColor;
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

            return Matrix4x4.TRS(pos, rot, Vector3.one * (v.Diameter * pop * breath));
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
