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

        // 已落定残骸的静态烘焙批次：矩阵/颜色只在入队时写一次，逐帧把缓存数组原样交给 DrawMeshInstanced。
        // 环形复写：写满 _wreckCap 后从最老的槽位覆盖——个体被替换在数万残骸的战场上几乎不可察觉。
        private sealed class WreckBuffer
        {
            public readonly List<Matrix4x4[]> Matrices = new();
            public readonly List<Vector4[]> Colors = new();
            public int Count; // 当前留存数（≤ 上限）
            public int Next;  // 环形写入游标
        }

        private readonly List<SettlingWreck> _settling = new(256);
        private readonly Dictionary<int, WreckBuffer> _wrecks = new();
        private int _bakedWreckCount; // 已落定总数（性能行展示；环形复写后停在上限）

        // 分批绘制缓冲（跨帧复用，零逐帧分配）。
        private readonly Matrix4x4[] _matrices = new Matrix4x4[BatchSize];
        private readonly Vector4[] _colors = new Vector4[BatchSize];

        /// <summary>战斗开始时由 director 调用：注入表现参数表并准备实例化材质。</summary>
        public void Init(Dictionary<int, EnemyVisual> visuals)
        {
            _visuals = visuals;
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
        /// 绘制当前帧的全部存活敌人（director 每帧调用）。按原型分组遍历模拟快照、
        /// 逐实例算矩阵与颜色、满批即提交——绘制次数 ≈ 敌人数 / 1023 × 原型种数。
        /// </summary>
        public void Render(IBattleSim sim)
        {
            if (_material == null || _visuals == null || sim == null) return;

            float now = Time.time;
            PromoteSettledWrecks(now);
            DrawWrecks(now);
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

        // 把一具残骸写进该原型的环形批次（矩阵/颜色从此不再变化）。
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

            buf.Next = (buf.Next + 1) % _wreckCap;
            if (buf.Count < _wreckCap)
            {
                buf.Count++;
                _bakedWreckCount++;
            }
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
