using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 伤害飘字：池化的世界空间数字，出生瞬间放大弹出、随后上浮 + 淡出，带轻微随机水平漂移防止重叠成柱。
    /// 用引擎自带 <see cref="TextMesh"/>（MeshRenderer，无需 Canvas / TMP 依赖），契合"几何体 + 纯色"美术基调。
    /// 自身只管表现（动画），生命周期归 <see cref="BattleDirector"/>（池的借还必须走同一个 Bag）。
    /// </summary>
    [RequireComponent(typeof(TextMesh))]
    public sealed class DamageFloater : MonoBehaviour, ITimedEffect
    {
        [SerializeField] private float _lifetime = 0.7f;
        [SerializeField] private float _riseSpeed = 1.8f;

        [SerializeField, Tooltip("出生弹出：从该倍率缩到 1（前 0.1 秒内完成）。")]
        private float _popScale = 1.6f;

        private TextMesh _text;
        private float _elapsed;
        private Color _baseColor;
        private float _drift;
        private float _baseScale;

        /// <summary>本次飘字是否已播完，可被 director 回收。</summary>
        public bool IsDone => _elapsed >= _lifetime;

        private void Awake()
        {
            _text = GetComponent<TextMesh>();
            _baseScale = transform.localScale.x;
        }

        /// <summary>重置并开始一次飘字（每次从池借出后调用；完整重置状态，不依赖 IPoolable）。</summary>
        public void Play(string content, Color color, Vector3 worldPos)
        {
            if (_text == null) Awake();
            _elapsed = 0f;
            _baseColor = color;
            _drift = Random.Range(-0.6f, 0.6f); // 纯装饰随机，不进模拟
            _text.text = content;
            _text.color = color;
            transform.position = worldPos;
            transform.localScale = Vector3.one * (_baseScale * _popScale);
        }

        private void Update()
        {
            if (IsDone) return;
            _elapsed += Time.deltaTime;

            transform.position += new Vector3(_drift, _riseSpeed, 0f) * Time.deltaTime;

            float popT = Mathf.Clamp01(_elapsed / 0.1f);
            transform.localScale = Vector3.one * (_baseScale * Mathf.Lerp(_popScale, 1f, popT));

            var c = _baseColor;
            c.a = Mathf.Clamp01(1f - _elapsed / _lifetime);
            if (_text != null) _text.color = c;
        }
    }
}
