using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 玩家炮塔表现：六边形工事底座 + 中心发光核心（呼吸脉动、开火时随后坐涨亮），炮管（Pivot 子节点）平滑转向瞄准目标、
    /// 开火后坐回弹。底座换形与核心均<b>运行时程序生成</b>（六边形是运行时网格、无资产，只在 Play 生效，不改场景磁盘资产）——
    /// 让它读成"防御工事"而非一个方块。瞄谁、何时开火全由 <see cref="BattleDirectorSystem"/> 驱动（模拟内核是 hitscan，本组件只负责"演"）。
    /// </summary>
    public sealed class TurretView : MonoBehaviour
    {
        [SerializeField, Tooltip("炮管旋转轴（绕 Z 转向目标；本地 +X 为炮口朝向）。")]
        private Transform _pivot;

        [SerializeField, Tooltip("炮管渲染体（Pivot 子节点，后坐时沿本地 -X 位移回弹）。")]
        private Transform _barrelMesh;

        [SerializeField, Tooltip("炮口挂点（Pivot 子节点，曳光的发射起点）。")]
        private Transform _muzzle;

        [SerializeField, Tooltip("转向速度（度/秒）。")]
        private float _turnSpeed = 720f;

        [SerializeField, Tooltip("单次开火的后坐位移（世界单位）。")]
        private float _recoilKick = 0.16f;

        [SerializeField, Tooltip("发光核心颜色（HDR，分量 > 1 触发 Bloom 出光晕）。")]
        private Color _coreColor = new(0.4f, 3.0f, 2.8f, 1f);

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private const float CoreBaseScale = 0.5f;

        private float _targetAngle;
        private float _currentAngle;
        private float _recoil;
        private float _barrelBaseX;

        private Transform _core;
        private Material _coreMat; // 运行时创建，OnDestroy 释放
        private Material _baseMat; // 六边形工事底座的专属 Unlit 材质（运行时创建，OnDestroy 释放）

        /// <summary>曳光发射起点（炮口当前世界坐标）。</summary>
        public Vector3 MuzzleWorldPos => _muzzle.position;

        private void Awake()
        {
            _barrelBaseX = _barrelMesh.localPosition.x;
            _currentAngle = _targetAngle = _pivot.localEulerAngles.z;
            BuildEmplacement();
        }

        // 底座换六边形工事 + 中心发光核心（都程序生成、仅运行时改，不动场景资产）。
        private void BuildEmplacement()
        {
            // 无 URP 时优雅跳过（不炸）——保留原始底座外观。
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return;

            var baseTf = transform.Find("Base");
            if (baseTf != null)
            {
                var mf = baseTf.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = OutpostMeshes.Hexagon;
                baseTf.localRotation = Quaternion.identity;
                baseTf.localScale = new Vector3(2.1f, 2.1f, 1f); // 平顶六边形工事平台
                var mrBase = baseTf.GetComponent<MeshRenderer>();
                if (mrBase != null)
                {
                    // 专属枪钢灰蓝 Unlit 材质：不靠场景材质的属性名，保证平台对比青色填充盘可见（非 HDR、不发光，读成实心工事）。
                    _baseMat = new Material(shader);
                    _baseMat.SetColor(BaseColorId, new Color(0.30f, 0.34f, 0.42f, 1f));
                    mrBase.sharedMaterial = _baseMat;
                }
            }

            // 核心：贴底座中心、朝相机一侧的发光圆盘。HDR 色经 Bloom 出辉光。
            _coreMat = new Material(shader);
            _coreMat.SetColor(BaseColorId, _coreColor);

            var go = new GameObject("Core");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.35f); // 炮管之前一层，核心不被炮管遮住
            go.transform.localScale = Vector3.one * CoreBaseScale;
            go.AddComponent<MeshFilter>().sharedMesh = OutpostMeshes.UnitDisc;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _coreMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _core = go.transform;
        }

        /// <summary>把炮口转向世界坐标目标（平滑追踪，不瞬转）。</summary>
        public void AimAt(Vector3 worldPos)
        {
            var d = worldPos - _pivot.position;
            if (d.sqrMagnitude < 0.0001f) return;
            _targetAngle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 炮口当前是否已对准该世界坐标（角度差在容差内）。director 据此把"开火演出"压到炮管转到位后才释放——
        /// 模拟内核是 hitscan、伤害早已结算，但曳光/炮口闪光要等炮管指向目标才发，避免"还没转过去就冒火"。
        /// </summary>
        public bool IsAimedAt(Vector3 worldPos, float toleranceDeg)
        {
            var d = worldPos - _pivot.position;
            if (d.sqrMagnitude < 0.0001f) return true;
            float target = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            return Mathf.Abs(Mathf.DeltaAngle(_currentAngle, target)) <= toleranceDeg;
        }

        /// <summary>播放一次开火后坐。</summary>
        public void Fire() => _recoil = _recoilKick;

        private void Update()
        {
            _currentAngle = Mathf.MoveTowardsAngle(_currentAngle, _targetAngle, _turnSpeed * Time.deltaTime);
            _pivot.localRotation = Quaternion.Euler(0f, 0f, _currentAngle);

            _recoil = Mathf.MoveTowards(_recoil, 0f, Time.deltaTime * 1.4f);
            var p = _barrelMesh.localPosition;
            p.x = _barrelBaseX - _recoil;
            _barrelMesh.localPosition = p;

            // 核心呼吸脉动；开火后坐未回落时随之涨大一点，呼应"刚开了一炮"。
            if (_core != null)
            {
                float breath = 1f + 0.12f * Mathf.Sin(Time.time * 4f);
                float kick = 1f + _recoil * 1.5f;
                _core.localScale = Vector3.one * (CoreBaseScale * breath * kick);
            }
        }

        private void OnDestroy()
        {
            if (_coreMat != null) Destroy(_coreMat);
            if (_baseMat != null) Destroy(_baseMat);
        }
    }
}
