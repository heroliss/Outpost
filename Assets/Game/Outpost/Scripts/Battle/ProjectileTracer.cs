using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 弹道曳光：一段发光短棒从炮口飞向命中点后消失。模拟内核是 hitscan（伤害在事件瞬间已结算），
    /// 曳光纯属演出——飞行速度快到不掩盖"命中点特效已同帧播放"的事实，只负责把"谁在开火、打向哪"画出来。
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

        public bool IsDone => _elapsed >= _duration;

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
            _duration = durationOverride > 0f ? durationOverride : Mathf.Max(0.02f, Vector3.Distance(from, to) / _speed);

            var dir = to - from;
            transform.SetPositionAndRotation(from, Quaternion.FromToRotation(Vector3.right, dir.normalized));

            _mpb ??= new MaterialPropertyBlock();
            _mpb.SetColor(BaseColorId, color);
            _renderer.SetPropertyBlock(_mpb);
        }

        private void Update()
        {
            if (IsDone) return;
            _elapsed += Time.deltaTime;
            transform.position = Vector3.Lerp(_from, _to, Mathf.Clamp01(_elapsed / _duration));
        }
    }
}
