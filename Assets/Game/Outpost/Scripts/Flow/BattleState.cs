using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Flow;
using Game.Outpost.Battle;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Outpost.Flow
{
    /// <summary>
    /// 战斗阶段。附加加载 <c>OutpostBattle</c> 场景——场景内 <c>BattleContext</c>（回退到根 Context 拿全局服务）+
    /// <c>BattleDirectorSystem</c> 完成配置、资源与模拟后端初始化后跑一局，终局导演直接 <c>GoTo(ResultState)</c>。
    /// <para>场景进 <see cref="FlowState.Bag"/>：本状态退出（进结算）时自动卸载——场景内 Context / 对象池 / 敌人视觉
    /// 整棵撤，不写一行手动清理。OnEnter 只有在场景句柄有效且 director 真正就绪后才返回；因此
    /// <c>Current is BattleState</c> 表示战斗已经可交互，不会把加载失败或永久未就绪提交成稳定状态。</para>
    /// </summary>
    public sealed class BattleState : FlowState
    {
        public override string ToString() => "战斗";

        protected override async UniTask OnEnter(CancellationToken ct)
        {
            var handle = await Bag.LoadScene("OutpostBattle", LoadSceneMode.Additive, ct: ct);
            if (handle == null || !handle.IsValid)
            {
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException(
                    "战斗场景 'OutpostBattle' 未返回有效 ISceneHandle；请检查资源地址、构建清单与默认资源包。");
            }

            Scene scene = handle.Scene;
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException(
                    "战斗场景 'OutpostBattle' 的句柄已返回，但 Unity Scene 尚未有效加载。");

            BattleDirectorSystem director = FindEnabledDirector(scene, out int totalCount, out int enabledCount);
            if (totalCount == 0)
                throw new InvalidOperationException(
                    "战斗场景 'OutpostBattle' 缺少 BattleDirectorSystem，无法确认战斗 Module 已就绪。");
            if (enabledCount == 0)
                throw new InvalidOperationException(
                    "战斗场景 'OutpostBattle' 中的 BattleDirectorSystem 均未启用，初始化不会开始。");
            if (enabledCount > 1)
                throw new InvalidOperationException(
                    $"战斗场景 'OutpostBattle' 同时启用了 {enabledCount} 个 BattleDirectorSystem；每个战斗会话必须只有一个导演。");

            await director.WaitUntilReady(ct);
        }

        private static BattleDirectorSystem FindEnabledDirector(
            Scene scene,
            out int totalCount,
            out int enabledCount)
        {
            totalCount = 0;
            enabledCount = 0;
            BattleDirectorSystem enabledDirector = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (BattleDirectorSystem director in
                         root.GetComponentsInChildren<BattleDirectorSystem>(includeInactive: true))
                {
                    totalCount++;
                    if (!director.isActiveAndEnabled) continue;
                    enabledCount++;
                    enabledDirector ??= director;
                }
            }
            return enabledDirector;
        }
    }
}
