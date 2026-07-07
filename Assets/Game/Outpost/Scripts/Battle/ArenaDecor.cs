using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 竞技场地面装饰：把两个玩法事实做成"雷达/火控台"式的可读画面——
    /// <list type="bullet">
    /// <item>射程覆盖区 = 青色射程环 + 圈内极淡填充盘（"这块是我的火力范围"）+ 一条缓慢旋转的雷达扫描臂（"正在索敌"）；</item>
    /// <item>危险边界 = 竞技场外缘暖红警戒环（敌人从这条线外刷入），带轻微呼吸让它读成"危险线"。</item>
    /// </list>
    /// 全部程序生成（LineRenderer 画环 + 程序网格填充/扫描，零贴图资产），几何参数由 <see cref="BattleDirector"/>
    /// 在模拟就绪 / 升级加射程时注入。射程升级后填充盘与扫描臂一起外扩，成长直接可见。
    /// </summary>
    public sealed class ArenaDecor : MonoBehaviour
    {
        [SerializeField, Tooltip("环 / 填充 / 扫描共用材质（透明发光 Unlit）。颜色经 MaterialPropertyBlock 逐件覆盖。")]
        private Material _ringMaterial;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        // 表现层 Z（相机 -10 朝 +Z 看，z 越小越靠近相机）：地板 0.5 > 填充 0.46 > 扫描扇 0.44 > 扫描臂 0.42 > 环 0.30 > 单位 0。
        private const float FillZ = 0.46f;
        private const float WedgeZ = 0.44f;
        private const float SpokeZ = 0.42f;
        private const float RingZ = 0.30f;

        // 射程区配色（HDR 值 > 1 触发 Bloom 发光）。
        private static readonly Color RangeRingColor = new(0.25f, 1.5f, 1.4f, 0.55f);
        private static readonly Color FillColor = new(0.10f, 0.42f, 0.40f, 0.12f);
        private static readonly Color WedgeColor = new(0.20f, 0.95f, 0.90f, 0.10f);
        private static readonly Color SpokeColor = new(0.55f, 2.4f, 2.2f, 0.85f);
        private static readonly Color BoundaryColor = new(1.35f, 0.42f, 0.30f, 1f); // alpha 由呼吸调制

        // 扫描臂几何：从本地 -SweepSpan 到 0（角度递增 = CCW 正面），臂在 0 度处（旋转前沿），扇尾拖在后。
        private const float SweepSpan = 26f;
        private const int WedgeSegments = 10;
        private const float SweepSpeed = 42f; // 度/秒

        private LineRenderer _rangeRing;
        private LineRenderer _boundaryRing;
        private Transform _sweepPivot;
        private Transform _fill;        // 填充盘（UnitDisc 缩放到射程）
        private MeshFilter _wedgeFilter; // 扫描扇（随射程重建）
        private LineRenderer _spoke;     // 扫描臂前沿亮线（随射程重建）

        private float _currentRange = -1f;
        private float _boundaryBaseAlpha = 0.4f;

        /// <summary>模拟就绪后调用：按竞技场半径生成外缘危险警戒环。重复调用只更新半径。</summary>
        public void Init(float arenaRadius)
        {
            if (_boundaryRing == null)
            {
                _boundaryRing = CreateRing("BoundaryRing", 0.07f, RingZ, BoundaryColor);
                _boundaryBaseAlpha = BoundaryColor.a * 0.4f; // 存底色 alpha，呼吸在其上下浮动
            }
            SetRingRadius(_boundaryRing, arenaRadius);
        }

        /// <summary>更新玩家射程区（首次调用时创建环 + 填充盘 + 扫描臂）。升级加射程后整体外扩。</summary>
        public void SetRange(float range)
        {
            if (Mathf.Approximately(range, _currentRange)) return;
            _currentRange = range;

            if (_rangeRing == null) BuildRangeZone();

            SetRingRadius(_rangeRing, range);
            _fill.localScale = new Vector3(range, range, 1f);
            RebuildSweep(range);
        }

        private void Update()
        {
            // 边界警戒环呼吸：alpha 在底色附近缓慢起伏，读成"危险线"而非静态装饰。
            if (_boundaryRing != null)
            {
                float a = _boundaryBaseAlpha + 0.18f * (0.5f + 0.5f * Mathf.Sin(Time.time * 1.6f));
                var c = BoundaryColor; c.a = a;
                SetColor(_boundaryRing, c);
            }

            // 雷达扫描臂缓慢旋转（索敌感）。
            if (_sweepPivot != null)
                _sweepPivot.localRotation = Quaternion.Euler(0f, 0f, Time.time * SweepSpeed % 360f);
        }

        // 一次性搭好射程区的三件：射程环 + 填充盘 + 扫描臂（含亮线 + 扇形拖尾）。
        private void BuildRangeZone()
        {
            _rangeRing = CreateRing("RangeRing", 0.05f, RingZ, RangeRingColor);

            // 填充盘：单位圆网格，透过 localScale 缩放到射程；极淡青、读成"我的火力覆盖区"。
            _fill = CreateMeshChild("RangeFill", FillZ, OutpostMeshes.UnitDisc, FillColor).transform;

            // 扫描臂 pivot：只旋转不缩放（避免连带缩放亮线宽度）。扇网格与亮线随射程重建。
            var pivotGo = new GameObject("SweepPivot");
            pivotGo.transform.SetParent(transform, false);
            _sweepPivot = pivotGo.transform;

            _wedgeFilter = CreateMeshChild("SweepWedge", WedgeZ, null, WedgeColor);
            _wedgeFilter.transform.SetParent(_sweepPivot, false);
            _wedgeFilter.transform.localPosition = new Vector3(0f, 0f, WedgeZ);

            var spokeGo = new GameObject("SweepSpoke");
            spokeGo.transform.SetParent(_sweepPivot, false);
            spokeGo.transform.localPosition = new Vector3(0f, 0f, SpokeZ);
            _spoke = spokeGo.AddComponent<LineRenderer>();
            _spoke.useWorldSpace = false;
            _spoke.widthMultiplier = 0.06f;
            _spoke.positionCount = 2;
            _spoke.sharedMaterial = _ringMaterial;
            _spoke.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _spoke.receiveShadows = false;
            SetColor(_spoke, SpokeColor);
        }

        // 按当前射程重建扫描扇网格 + 前沿亮线（射程升级才调，频率低，直接重建）。
        private void RebuildSweep(float range)
        {
            // 扫描扇：中心 + 一段弧（-SweepSpan..0，角度递增 = CCW 正面），做成缓慢旋转的"雷达波"。
            var verts = new Vector3[WedgeSegments + 2];
            var tris = new int[WedgeSegments * 3];
            verts[0] = Vector3.zero;
            for (int i = 0; i <= WedgeSegments; i++)
            {
                float a = Mathf.Deg2Rad * (-SweepSpan + SweepSpan * (i / (float)WedgeSegments));
                verts[i + 1] = new Vector3(Mathf.Cos(a) * range, Mathf.Sin(a) * range, 0f);
            }
            for (int i = 0; i < WedgeSegments; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = 1 + i;
                tris[i * 3 + 2] = 2 + i;
            }
            var mesh = _wedgeFilter.sharedMesh;
            if (mesh == null) { mesh = new Mesh { name = "OutpostSweepWedge" }; _wedgeFilter.sharedMesh = mesh; }
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            _spoke.SetPosition(0, Vector3.zero);
            _spoke.SetPosition(1, new Vector3(range, 0f, 0f));
        }

        private LineRenderer CreateRing(string ringName, float width, float z, Color color)
        {
            var go = new GameObject(ringName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.widthMultiplier = width;
            lr.sharedMaterial = _ringMaterial;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            SetColor(lr, color);
            return lr;
        }

        // 建一个挂 MeshRenderer + MeshFilter 的子物体（填充盘 / 扫描扇），mesh 可后置。
        private MeshFilter CreateMeshChild(string childName, float z, Mesh mesh, Color color)
        {
            var go = new GameObject(childName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            var mf = go.AddComponent<MeshFilter>();
            if (mesh != null) mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _ringMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor(BaseColorId, color);
            mr.SetPropertyBlock(mpb);
            return mf;
        }

        private static void SetColor(Renderer r, Color color)
        {
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor(BaseColorId, color);
            r.SetPropertyBlock(mpb);
        }

        private static void SetRingRadius(LineRenderer lr, float radius)
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
