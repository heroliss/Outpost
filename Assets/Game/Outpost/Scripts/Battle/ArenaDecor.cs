using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 竞技场地面装饰：程序生成的三个圆环——玩家射程圈（升级加射程时实时外扩）、敌人出生环（危险边界）、
    /// 中间一圈淡刻度。全部用 LineRenderer 画（零贴图资产），几何参数由 <see cref="BattleDirector"/>
    /// 在模拟就绪后注入。让"多远会被打到 / 敌人从哪来"这两个玩法事实直接可见。
    /// </summary>
    public sealed class ArenaDecor : MonoBehaviour
    {
        [SerializeField, Tooltip("圆环共用材质（透明发光 Unlit）。颜色经 MaterialPropertyBlock 逐环覆盖。")]
        private Material _ringMaterial;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private LineRenderer _rangeRing;
        private LineRenderer _boundaryRing;
        private LineRenderer _guideRing;
        private float _currentRange = -1f;

        /// <summary>模拟就绪后调用：按竞技场半径生成边界环与刻度环。重复调用只更新半径。</summary>
        public void Init(float arenaRadius)
        {
            _boundaryRing ??= CreateRing("BoundaryRing", 0.07f, new Color(1.0f, 0.42f, 0.30f, 0.38f));
            SetRadius(_boundaryRing, arenaRadius);

            _guideRing ??= CreateRing("GuideRing", 0.03f, new Color(0.55f, 0.75f, 0.95f, 0.10f));
            SetRadius(_guideRing, arenaRadius * 0.62f);
        }

        /// <summary>更新玩家射程圈半径（首次调用时创建）。升级加射程后外扩，玩家能直观看到成长。</summary>
        public void SetRange(float range)
        {
            if (Mathf.Approximately(range, _currentRange)) return;
            _currentRange = range;
            _rangeRing ??= CreateRing("RangeRing", 0.05f, new Color(0.25f, 1.5f, 1.4f, 0.30f));
            SetRadius(_rangeRing, range);
        }

        private LineRenderer CreateRing(string ringName, float width, Color color)
        {
            var go = new GameObject(ringName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, 0.3f); // 地板(0.5)之上、战斗单位(0)之下

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.widthMultiplier = width;
            lr.sharedMaterial = _ringMaterial;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;

            var mpb = new MaterialPropertyBlock();
            mpb.SetColor(BaseColorId, color);
            lr.SetPropertyBlock(mpb);
            return lr;
        }

        private static void SetRadius(LineRenderer lr, float radius)
        {
            const int segments = 96;
            lr.positionCount = segments;
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f));
            }
        }
    }
}
