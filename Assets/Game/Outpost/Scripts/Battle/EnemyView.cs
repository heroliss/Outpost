using Game.Framework.Pool;
using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 敌人视觉：出生缩放弹出、持续轻微呼吸、受击白闪、血量越低颜色越暗；有向原型（箭头）逐帧转向来袭方向。
    /// 原型差异（颜色 / 体型 / 形状）由 <see cref="BattleDirectorSystem"/> 在刷出时经 <see cref="Init"/> 注入——同一个池化 prefab 服务所有原型。
    /// <para>位置由 director 逐帧经 <see cref="SetGroundPosition"/> 给定，本组件在 <c>LateUpdate</c> 只叠加缩放 / 朝向的表现动画——
    /// 表现动画与逻辑位置分离，互不覆盖。实现 <see cref="IPoolable"/> 由框架池在借还时机自动重置。</para>
    /// </summary>
    public sealed class EnemyView : MonoBehaviour, IPoolable
    {
        [SerializeField, Tooltip("本体渲染体（着色 / 白闪都打在它身上）。")]
        private MeshRenderer _renderer;

        [SerializeField, Tooltip("本体 MeshFilter（原型可换形状：箭头 = 快速种、六边形 = 装甲种）。")]
        private MeshFilter _meshFilter;

        [SerializeField, Tooltip("出生弹出时长（秒），从 0 缩放到目标体型。")]
        private float _popDuration = 0.18f;

        [SerializeField, Tooltip("受击白闪回落时长（秒）。")]
        private float _flashDuration = 0.12f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static MaterialPropertyBlock _mpb;

        private Color _baseColor;
        private float _diameter = 1f;
        private float _hpRatio = 1f;
        private float _popElapsed;
        private float _flash;
        private bool _colorDirty;
        // 箭头原型逐帧转向来袭方向（指向哨站）；六边形等对称原型不转，保持固定朝向。
        private bool _faceTravel;

        // 逻辑地面位置（director 每帧写）——最终渲染位置直接取它，表现动画只叠加缩放 / 朝向。
        private Vector3 _groundPos;

        // 呼吸相位错开，避免整群敌人同频"齐吸"显得机械。
        private float _breathPhase;

        /// <summary>
        /// 刷出时由 director 调用：注入原型的颜色 / 体型 / 形状并从零弹出。
        /// <paramref name="faceTravel"/> = true 时（箭头等有向原型）逐帧把箭尖转向来袭方向。
        /// </summary>
        public void Init(Color color, float diameter, Mesh mesh, bool faceTravel)
        {
            _baseColor = color;
            _diameter = diameter;
            _hpRatio = 1f;
            _popElapsed = 0f;
            _flash = 0f;
            _faceTravel = faceTravel;
            _breathPhase = Random.value * Mathf.PI * 2f;
            if (mesh != null && _meshFilter.sharedMesh != mesh) _meshFilter.sharedMesh = mesh;
            transform.localScale = Vector3.zero;
            transform.localRotation = Quaternion.identity; // 对称原型固定朝向；有向原型下方 LateUpdate 逐帧覆盖
            ApplyColor();
        }

        /// <summary>设置逻辑地面位置（director 逐帧同步；实际渲染位置在 LateUpdate 叠加呼吸缩放 / 朝向）。</summary>
        public void SetGroundPosition(Vector3 pos) => _groundPos = pos;

        /// <summary>受击白闪（伤害数字之外的身体反馈）。</summary>
        public void Flash()
        {
            _flash = 1f;
            _colorDirty = true;
        }

        /// <summary>同步血量比例（颜色随血量变暗，濒死一眼可辨）。</summary>
        public void SetHpRatio(float ratio)
        {
            ratio = Mathf.Clamp01(ratio);
            if (Mathf.Approximately(ratio, _hpRatio)) return;
            _hpRatio = ratio;
            _colorDirty = true;
        }

        private void Update()
        {
            if (_popElapsed < _popDuration) _popElapsed += Time.deltaTime;

            if (_flash > 0f)
            {
                _flash = Mathf.Max(0f, _flash - Time.deltaTime / _flashDuration);
                _colorDirty = true;
            }
        }

        private void LateUpdate()
        {
            // ── 缩放：出生弹出 × 持续呼吸 ──
            float popScale;
            if (_popElapsed < _popDuration)
            {
                float t = Mathf.Clamp01(_popElapsed / _popDuration);
                // easeOutBack 轻微过冲，出生更有"弹"感。
                popScale = 1f + 1.7f * Mathf.Pow(t - 1f, 3f) + 0.7f * Mathf.Pow(t - 1f, 2f);
            }
            else
            {
                popScale = 1f;
            }
            float breath = 1f + 0.035f * Mathf.Sin(Time.time * 3.2f + _breathPhase);
            transform.localScale = Vector3.one * (_diameter * Mathf.Max(0f, popScale) * breath);

            // ── 位置：直接取逻辑地面位置（表现动画只叠加缩放 / 朝向，不再动位置）──
            transform.localPosition = _groundPos;

            // ── 朝向：有向原型（箭头）转向来袭方向 = 指向哨站（原点）。敌人径直冲原点，故朝向 = 指向 -groundPos ──
            if (_faceTravel)
            {
                var toOrigin = new Vector2(-_groundPos.x, -_groundPos.y);
                if (toOrigin.sqrMagnitude > 0.0001f)
                {
                    float angle = Mathf.Atan2(toOrigin.y, toOrigin.x) * Mathf.Rad2Deg;
                    transform.localRotation = Quaternion.Euler(0f, 0f, angle);
                }
            }

            if (_colorDirty) ApplyColor();
        }

        private void ApplyColor()
        {
            // 血量越低越暗（保底约 1/4 亮度），受击时向亮白抬升（HDR 值配合 Bloom 出闪光感）。
            var c = Color.Lerp(_baseColor * 0.35f, _baseColor, 0.25f + 0.75f * _hpRatio);
            c = Color.Lerp(c, new Color(2f, 2f, 2f, 1f), _flash * 0.85f);
            c.a = 1f;
            _mpb ??= new MaterialPropertyBlock();
            _mpb.SetColor(BaseColorId, c);
            _renderer.SetPropertyBlock(_mpb);
            _colorDirty = false;
        }

        void IPoolable.OnRent() { }

        void IPoolable.OnReturn()
        {
            // 清掉表现残留，避免下次借出前的一帧闪旧色 / 旧体型 / 旧朝向。
            _flash = 0f;
            _popElapsed = float.MaxValue;
            transform.localScale = Vector3.one;
            transform.localRotation = Quaternion.identity;
        }
    }
}
