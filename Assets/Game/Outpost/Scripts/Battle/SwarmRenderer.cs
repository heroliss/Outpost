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
        /// 绘制当前帧的全部存活敌人（director 每帧调用）。按原型分组遍历模拟快照、
        /// 逐实例算矩阵与颜色、满批即提交——绘制次数 ≈ 敌人数 / 1023 × 原型种数。
        /// </summary>
        public void Render(IBattleSim sim)
        {
            if (_material == null || _visuals == null || sim == null) return;

            float now = Time.time;
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
