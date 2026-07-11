using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 战斗单位与地面装饰的程序网格库（零美术贴图，契合切片"全几何体"基调）。
    /// 网格建在顶视 XY 平面。<b>一律做成双面</b>（正反两组三角）：单位/炮台材质多是 Cull Back，
    /// 顶视平面网格的正反朝向不易一次判对，双面让它与相机在平面哪一侧无关都可见——省去逐材质对 winding 的心智负担。
    /// 全部静态懒建 + 跨所有池化实例共享同一份 <c>sharedMesh</c>（同原型不重复分配）。生命周期与应用同寿、不销毁。
    /// </summary>
    internal static class OutpostMeshes
    {
        private static Mesh _arrowhead;
        private static Mesh _hexagon;
        private static Mesh _unitDisc;
        private static Mesh _dart;
        private static Mesh _needle;
        private static Mesh _octagon;
        private static Mesh _projectile;
        private static Mesh _unitQuad;

        /// <summary>快速种：指向行进方向的箭头（本地 +X 为箭尖，<see cref="EnemyView"/> 逐帧转向来袭方向 = 冲向哨站）。</summary>
        public static Mesh Arrowhead
        {
            get { if (_arrowhead == null) _arrowhead = BuildArrowhead(); return _arrowhead; }
        }

        /// <summary>无人机（炮灰）：指向行进方向的小三角（一个顶点朝本地 +X，逐帧转向来袭方向）。比箭头更小更尖、读作成群小飞行器。</summary>
        public static Mesh Dart
        {
            get { if (_dart == null) _dart = BuildPolygon("OutpostDart", 3, 0f, 0.5f); return _dart; }
        }

        /// <summary>掠袭机（极速种）：细长尖针，指向行进方向（本地 +X 为针尖，逐帧转向来袭方向）。比箭头更瘦更尖，读作高速掠袭。</summary>
        public static Mesh Needle
        {
            get { if (_needle == null) _needle = BuildNeedle(); return _needle; }
        }

        /// <summary>攻城核（重装种）：厚重八边形。半径 0.5，由体型直径缩放。</summary>
        public static Mesh Octagon
        {
            get { if (_octagon == null) _octagon = BuildPolygon("OutpostOctagon", 8, 22.5f, 0.5f); return _octagon; }
        }

        // 细长针：针尖朝本地 +X，尾部凹口，整体细窄，缩放后读作高速掠袭体。
        private static Mesh BuildNeedle()
        {
            var v = new[]
            {
                new Vector3(0.68f, 0f, 0f),      // 0 针尖（+X）
                new Vector3(-0.42f, 0.16f, 0f),  // 1 左后翼
                new Vector3(-0.28f, 0f, 0f),     // 2 尾部凹口
                new Vector3(-0.42f, -0.16f, 0f)  // 3 右后翼
            };
            var t = new[] { 0, 1, 2, 0, 2, 3 };
            return Build("OutpostNeedle", v, t);
        }

        /// <summary>装甲种：厚重平顶六边形。半径 0.5，由体型直径缩放。</summary>
        public static Mesh Hexagon
        {
            get { if (_hexagon == null) _hexagon = BuildPolygon("OutpostHexagon", 6, 30f, 0.5f); return _hexagon; }
        }

        /// <summary>单位半径（=1）填充盘：射程覆盖区等用 transform 缩放到目标半径。</summary>
        public static Mesh UnitDisc
        {
            get { if (_unitDisc == null) _unitDisc = BuildPolygon("OutpostUnitDisc", 48, 0f, 1f); return _unitDisc; }
        }

        /// <summary>在飞弹丸：细长拖尾菱形（尖头朝本地 +X、总长 1），按飞行方向旋转、整体缩放到弹长——高速直飞读成一道光痕。</summary>
        public static Mesh Projectile
        {
            get { if (_projectile == null) _projectile = BuildProjectile(); return _projectile; }
        }

        /// <summary>1×1 轴对齐正方形（中心原点）：泥地热力图的密度格色块，按格边长缩放平铺。</summary>
        public static Mesh UnitQuad
        {
            get
            {
                if (_unitQuad == null)
                {
                    var v = new[]
                    {
                        new Vector3(-0.5f, -0.5f, 0f),
                        new Vector3(-0.5f, 0.5f, 0f),
                        new Vector3(0.5f, 0.5f, 0f),
                        new Vector3(0.5f, -0.5f, 0f),
                    };
                    _unitQuad = Build("OutpostUnitQuad", v, new[] { 0, 1, 2, 0, 2, 3 });
                }
                return _unitQuad;
            }
        }

        // 拖尾菱形：前 30% 是尖头、后 70% 收成细尾，宽仅 0.14——缩放后不糊成圆点、方向可读。
        private static Mesh BuildProjectile()
        {
            var v = new[]
            {
                new Vector3(0.5f, 0f, 0f),     // 0 弹头（+X）
                new Vector3(0.2f, 0.07f, 0f),  // 1 上肩
                new Vector3(-0.5f, 0f, 0f),    // 2 尾尖
                new Vector3(0.2f, -0.07f, 0f)  // 3 下肩
            };
            var t = new[] { 0, 1, 2, 0, 2, 3 };
            return Build("OutpostProjectile", v, t);
        }

        // 凹尾飞镖：箭尖朝本地 +X，整体落在半径约 0.6 内，缩放后与其他原型体型可比。
        private static Mesh BuildArrowhead()
        {
            var v = new[]
            {
                new Vector3(0.60f, 0f, 0f),     // 0 箭尖（+X）
                new Vector3(-0.50f, 0.42f, 0f), // 1 左后翼
                new Vector3(-0.25f, 0f, 0f),    // 2 尾部凹口
                new Vector3(-0.50f, -0.42f, 0f) // 3 右后翼
            };
            var t = new[] { 0, 1, 2, 0, 2, 3 }; // 两片，顶点 CCW → 正面朝相机
            return Build("OutpostArrowhead", v, t);
        }

        // 正多边形填充（中心扇形三角）。startDeg 控朝向（六边形 30° = 平顶），顶点按角度递增 = CCW 正面。
        private static Mesh BuildPolygon(string name, int sides, float startDeg, float radius)
        {
            var v = new Vector3[sides + 1];
            v[0] = Vector3.zero;
            float start = startDeg * Mathf.Deg2Rad;
            for (int i = 0; i < sides; i++)
            {
                float a = start + i / (float)sides * Mathf.PI * 2f;
                v[i + 1] = new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f);
            }
            var t = new int[sides * 3];
            for (int i = 0; i < sides; i++)
            {
                t[i * 3] = 0;
                t[i * 3 + 1] = 1 + i;
                t[i * 3 + 2] = 1 + (i + 1) % sides;
            }
            return Build(name, v, t);
        }

        private static Mesh Build(string name, Vector3[] verts, int[] tris)
        {
            var m = new Mesh { name = name };
            m.SetVertices(verts);
            m.SetTriangles(DoubleSided(tris), 0);
            m.RecalculateBounds();
            return m;
        }

        // 追加一组反向绕序三角 → 双面网格（正反都渲染，不被 Cull Back 材质按朝向剔除）。
        private static int[] DoubleSided(int[] tris)
        {
            var d = new int[tris.Length * 2];
            System.Array.Copy(tris, d, tris.Length);
            for (int i = 0; i < tris.Length; i += 3)
            {
                d[tris.Length + i] = tris[i];
                d[tris.Length + i + 1] = tris[i + 2];
                d[tris.Length + i + 2] = tris[i + 1];
            }
            return d;
        }
    }
}
