using System.Collections.Generic;
using OutpostCfg;
using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 一种敌人的表现参数（颜色 / 形状 / 体型 / 爆炸倍率）——从配置表的表现列解析而来。
    /// 模拟内核只认数值列（<see cref="BattleSetupFactory"/> 刻意不映射这些字段），表现层经 <see cref="EnemyVisuals"/> 读表取用。
    /// </summary>
    public readonly struct EnemyVisual
    {
        public readonly Color Color;
        public readonly Mesh Mesh;
        public readonly float Diameter;

        /// <summary>是否逐帧转向来袭方向（箭头 / 尖针等有向形状；六边形等对称形状保持固定朝向）。</summary>
        public readonly bool FaceTravel;

        /// <summary>爆炸体量倍率。&lt; 0.8 的炮灰击毁只留脉冲、不出碎片 / 烟——海量击杀防刷屏。</summary>
        public readonly float ExplosionScale;

        public EnemyVisual(Color color, Mesh mesh, float diameter, bool faceTravel, float explosionScale)
        {
            Color = color;
            Mesh = mesh;
            Diameter = diameter;
            FaceTravel = faceTravel;
            ExplosionScale = explosionScale;
        }
    }

    /// <summary>
    /// 敌人表现参数表：把 <c>TbEnemy</c> 的表现列（colorHex / shape / diameter / explosionScale）解析成运行时结构。
    /// 加一种敌人 = 配置表加一行，表现层零代码改动；形状名映射到 <see cref="OutpostMeshes"/> 的程序网格。
    /// </summary>
    public static class EnemyVisuals
    {
        // 未知原型的兜底外观（配置表缺行 / 形状名拼错时可见地"发白"提示，而不是隐形或抛异常）。
        private static readonly EnemyVisual Fallback =
            new(Color.white, null, 0.8f, false, 1f);

        /// <summary>从配置表解析全部敌人的表现参数（战斗开始时构建一次）。</summary>
        public static Dictionary<int, EnemyVisual> Build(Tables cfg)
        {
            var map = new Dictionary<int, EnemyVisual>(cfg.TbEnemy.DataList.Count);
            foreach (var e in cfg.TbEnemy.DataList)
            {
                if (!ColorUtility.TryParseHtmlString("#" + e.ColorHex, out var color))
                {
                    Debug.LogError($"[EnemyVisuals] 敌人 {e.Id}({e.Name}) 的 colorHex \"{e.ColorHex}\" 解析失败，用白色兜底。");
                    color = Color.white;
                }
                var (mesh, faceTravel) = ResolveShape(e.Shape, e.Id, e.Name);
                map[e.Id] = new EnemyVisual(color, mesh, e.Diameter, faceTravel, e.ExplosionScale);
            }
            return map;
        }

        /// <summary>按原型 id 取表现参数；未知 id 返回白色兜底外观。</summary>
        public static EnemyVisual Get(Dictionary<int, EnemyVisual> map, int archId)
            => map.TryGetValue(archId, out var v) ? v : FallbackWithMesh();

        private static EnemyVisual FallbackWithMesh()
            => new(Fallback.Color, OutpostMeshes.Hexagon, Fallback.Diameter, Fallback.FaceTravel, Fallback.ExplosionScale);

        // 形状名 → 程序网格 + 是否有向（有向形状逐帧转向来袭方向）。
        private static (Mesh mesh, bool faceTravel) ResolveShape(string shape, int id, string name) => shape switch
        {
            "dart" => (OutpostMeshes.Dart, true),
            "arrowhead" => (OutpostMeshes.Arrowhead, true),
            "needle" => (OutpostMeshes.Needle, true),
            "hexagon" => (OutpostMeshes.Hexagon, false),
            "octagon" => (OutpostMeshes.Octagon, false),
            _ => LogUnknownShape(shape, id, name),
        };

        private static (Mesh, bool) LogUnknownShape(string shape, int id, string name)
        {
            Debug.LogError($"[EnemyVisuals] 敌人 {id}({name}) 的 shape \"{shape}\" 未知（可选 dart/arrowhead/needle/hexagon/octagon），用六边形兜底。");
            return (OutpostMeshes.Hexagon, false);
        }
    }
}
