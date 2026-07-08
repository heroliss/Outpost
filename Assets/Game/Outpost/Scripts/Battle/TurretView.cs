using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 玩家炮塔表现：六边形工事底座 + 中心发光核心（呼吸脉动、开火时随后坐涨亮），炮管（Pivot 子节点）指向瞄准角、
    /// 开火后坐回弹。底座换形与核心均<b>运行时程序生成</b>（六边形是运行时网格、无资产，只在 Play 生效，不改场景磁盘资产）——
    /// 让它读成"防御工事"而非一个方块。
    /// <para>朝向与开火时机全由模拟内核决定：内核按回转速度逐帧算好炮口角，导演每帧经 <see cref="Face"/> 喂给本组件；
    /// 内核只在炮口对准目标时才发 <c>EnemyHit</c>，故本组件不再自行平滑转向或判断"是否对准"——只负责把内核给的角度画出来 + 演后坐。</para>
    /// </summary>
    public sealed class TurretView : MonoBehaviour
    {
        [SerializeField, Tooltip("炮管旋转轴（绕 Z 转向目标；本地 +X 为炮口朝向）。")]
        private Transform _pivot;

        [SerializeField, Tooltip("炮管渲染体（Pivot 子节点，后坐时沿本地 -X 位移回弹）。")]
        private Transform _barrelMesh;

        [SerializeField, Tooltip("炮口挂点（Pivot 子节点，曳光的发射起点）。")]
        private Transform _muzzle;

        [SerializeField, Tooltip("单次开火的后坐位移（世界单位）。")]
        private float _recoilKick = 0.16f;

        [SerializeField, Tooltip("发光核心颜色（HDR，分量 > 1 触发 Bloom 出光晕）。")]
        private Color _coreColor = new(0.4f, 3.0f, 2.8f, 1f);

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private const float CoreBaseScale = 0.5f;

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

        /// <summary>把炮口摆到模拟内核给定的朝向角（度，标准数学角：0 = +X、逆时针为正；即绕本地 Z）。</summary>
        public void Face(float angleDeg) => _pivot.localRotation = Quaternion.Euler(0f, 0f, angleDeg);

        /// <summary>播放一次开火后坐。</summary>
        public void Fire() => _recoil = _recoilKick;

        private void Update()
        {
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
