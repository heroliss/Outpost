using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 伤害飘字：池化的世界空间数字，出生后上浮 + 淡出，到寿命由 <see cref="BattleDirector"/> 回收。
    /// 用引擎自带 <see cref="TextMesh"/>（MeshRenderer，无需 Canvas / TMP 依赖），契合"几何体 + 纯色"美术基调。
    /// 自身只管表现（动画），生命周期归 director（池的借还必须走同一个 Bag）。
    /// </summary>
    [RequireComponent(typeof(TextMesh))]
    public sealed class DamageFloater : MonoBehaviour
    {
        [SerializeField] private float _lifetime = 0.6f;
        [SerializeField] private float _riseSpeed = 1.6f;

        private TextMesh _text;
        private float _elapsed;
        private Color _baseColor;

        /// <summary>本次飘字是否已播完，可被 director 回收。</summary>
        public bool IsDone => _elapsed >= _lifetime;

        private void Awake() => _text = GetComponent<TextMesh>();

        /// <summary>重置并开始一次飘字（每次从池借出后调用；完整重置状态，不依赖 IPoolable）。</summary>
        public void Play(string content, Color color, Vector3 worldPos)
        {
            if (_text == null) _text = GetComponent<TextMesh>();
            _elapsed = 0f;
            _baseColor = color;
            _text.text = content;
            _text.color = color;
            transform.position = worldPos;
        }

        private void Update()
        {
            if (IsDone) return;
            _elapsed += Time.deltaTime;
            transform.position += Vector3.up * (_riseSpeed * Time.deltaTime);
            var c = _baseColor;
            c.a = Mathf.Clamp01(1f - _elapsed / _lifetime);
            if (_text != null) _text.color = c;
        }
    }
}
