using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 脉冲圆环：从起始直径扩散到结束直径并淡出的发光环，命中闪光 / 死亡爆发 / 出生提示共用一个组件，
    /// 只靠参数区分强弱。环形网格在首次使用时程序生成并静态共享（零贴图、零美术资产，契合几何体美术基调）。
    /// </summary>
    public sealed class PulseEffect : MonoBehaviour, ITimedEffect
    {
        [SerializeField, Tooltip("环的渲染体（网格由代码生成注入，材质须为透明发光）。")]
        private MeshRenderer _renderer;

        [SerializeField, Tooltip("同一物体上的 MeshFilter，接收共享环形网格。")]
        private MeshFilter _meshFilter;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static MaterialPropertyBlock _mpb;
        private static Mesh _ringMesh;

        private Color _color;
        private float _fromScale;
        private float _toScale;
        private float _lifetime = 0.3f;
        private float _elapsed;

        public bool IsDone => _elapsed >= _lifetime;

        /// <summary>重置并播放一次脉冲（从池借出后调用）。直径从 fromDiameter 扩散到 toDiameter、alpha 线性归零。</summary>
        public void Play(Vector3 worldPos, Color color, float fromDiameter, float toDiameter, float lifetime)
        {
            if (_meshFilter.sharedMesh == null) _meshFilter.sharedMesh = GetRingMesh();
            _color = color;
            _fromScale = fromDiameter;
            _toScale = toDiameter;
            _lifetime = Mathf.Max(0.05f, lifetime);
            _elapsed = 0f;
            transform.position = worldPos;
            Apply(0f);
        }

        private void Update()
        {
            if (IsDone) return;
            _elapsed += Time.deltaTime;
            Apply(Mathf.Clamp01(_elapsed / _lifetime));
        }

        private void Apply(float t)
        {
            // 扩散用 easeOut（先快后慢），淡出保持线性——脉冲感主要来自前几帧的速度。
            float eased = 1f - (1f - t) * (1f - t);
            float s = Mathf.Lerp(_fromScale, _toScale, eased);
            transform.localScale = new Vector3(s, s, 1f);

            var c = _color;
            c.a *= 1f - t;
            _mpb ??= new MaterialPropertyBlock();
            _mpb.SetColor(BaseColorId, c);
            _renderer.SetPropertyBlock(_mpb);
        }

        /// <summary>共享环形网格（外径 0.5 = 直径 1，localScale 即最终直径）。</summary>
        private static Mesh GetRingMesh()
        {
            if (_ringMesh != null) return _ringMesh;

            const int segments = 48;
            const float outer = 0.5f;
            const float inner = 0.36f;
            var vertices = new Vector3[segments * 2];
            var triangles = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
                vertices[i * 2] = new Vector3(cos * inner, sin * inner, 0f);
                vertices[i * 2 + 1] = new Vector3(cos * outer, sin * outer, 0f);

                int next = (i + 1) % segments;
                int t = i * 6;
                triangles[t] = i * 2;
                triangles[t + 1] = i * 2 + 1;
                triangles[t + 2] = next * 2 + 1;
                triangles[t + 3] = i * 2;
                triangles[t + 4] = next * 2 + 1;
                triangles[t + 5] = next * 2;
            }

            _ringMesh = new Mesh { name = "OutpostPulseRing", vertices = vertices, triangles = triangles };
            _ringMesh.RecalculateBounds();
            return _ringMesh;
        }
    }
}
