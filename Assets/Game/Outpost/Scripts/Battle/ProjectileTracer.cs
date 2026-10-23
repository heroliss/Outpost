using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 池化的短程光痕 / 击毁碎片。真实在飞弹丸由 SwarmRenderer 读取模拟快照绘制；
    /// 本组件只做事件之后的表现，不参与命中判定。指定时长的碎片带减速、收缩与余烬淡出。
    /// </summary>
    public sealed class ProjectileTracer : MonoBehaviour, ITimedEffect
    {
        [SerializeField, Tooltip("飞行速度（世界单位/秒）。够快才不会与同帧结算的命中特效脱节。")]
        private float _speed = 55f;

        [SerializeField, Tooltip("发光短棒的渲染体（拉长的几何体，沿本地 +X 朝向飞行方向）。")]
        private Renderer _renderer;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static MaterialPropertyBlock _mpb;

        private Vector3 _from;
        private Vector3 _to;
        private float _duration;
        private float _elapsed;
        private bool _debris;
        private Color _color;
        private Vector3 _baseScale;

        public bool IsDone => _elapsed >= _duration;

        private void Awake() => _baseScale = transform.localScale;

        /// <summary>重置并发射一次（从池借出后调用）。两点之间按固定速度直飞。</summary>
        public void Play(Vector3 from, Vector3 to, Color color) => Play(from, to, color, -1f);

        /// <summary>
        /// 同上，但可指定飞行时长（秒）。传 &gt; 0 时忽略速度、直接用该时长——
        /// 供"击毁碎片"复用曳光：短距离(&lt;2 单位)按速度算时长会快到只有一帧，指定时长才能拉出可见的向外飞溅弧。
        /// </summary>
        public void Play(Vector3 from, Vector3 to, Color color, float durationOverride)
        {
            _from = from;
            _to = to;
            _elapsed = 0f;
            _debris = durationOverride > 0f;
            _color = color;
            _duration = _debris ? durationOverride : Mathf.Max(0.02f, Vector3.Distance(from, to) / Mathf.Max(0.01f, _speed));

            var dir = to - from;
            transform.SetPositionAndRotation(from, dir.sqrMagnitude > 0f
                ? Quaternion.FromToRotation(Vector3.right, dir.normalized) : Quaternion.identity);
            transform.localScale = _baseScale;

            _mpb ??= new MaterialPropertyBlock();
            _mpb.SetColor(BaseColorId, color);
            _renderer.SetPropertyBlock(_mpb);
        }

        private void Update()
        {
            if (IsDone) return;
            _elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_elapsed / _duration);
            float travel = _debris ? 1f - (1f - t) * (1f - t) : t;
            transform.position = Vector3.Lerp(_from, _to, travel);
            if (!_debris) return;

            transform.localScale = _baseScale * Mathf.Lerp(1f, 0.25f, t);
            var c = _color * Mathf.Lerp(1f, 0.30f, t);
            c.a = _color.a * (1f - t) * (1f - t);
            _mpb.SetColor(BaseColorId, c);
            _renderer.SetPropertyBlock(_mpb);
        }
    }
}
