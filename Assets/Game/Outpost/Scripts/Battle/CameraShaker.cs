using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 相机震动：玩家受击等强反馈时刻的小幅随机抖动，幅度随剩余时间线性衰减。
    /// 挂在战斗相机上；基准位置在 Awake 缓存，震动叠加在其上、结束时精确归位。
    /// </summary>
    public sealed class CameraShaker : MonoBehaviour
    {
        private Vector3 _basePos;
        private float _amplitude;
        private float _duration;
        private float _remaining;

        private void Awake() => _basePos = transform.localPosition;

        /// <summary>触发一次震动（覆盖进行中的震动——取更强者，不叠加）。</summary>
        public void Shake(float amplitude, float duration)
        {
            if (_remaining > 0f && _amplitude * (_remaining / _duration) > amplitude) return;
            _amplitude = amplitude;
            _duration = Mathf.Max(0.01f, duration);
            _remaining = _duration;
        }

        private void Update()
        {
            if (_remaining <= 0f) return;
            _remaining -= Time.deltaTime;
            if (_remaining <= 0f)
            {
                transform.localPosition = _basePos;
                return;
            }
            float falloff = _remaining / _duration;
            var offset = (Vector3)(Random.insideUnitCircle * (_amplitude * falloff));
            transform.localPosition = _basePos + offset;
        }
    }
}
