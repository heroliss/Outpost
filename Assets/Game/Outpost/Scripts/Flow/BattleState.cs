using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Flow;
using UnityEngine.SceneManagement;

namespace Game.Outpost.Flow
{
    /// <summary>
    /// 战斗阶段。附加加载 <c>OutpostBattle</c> 场景——场景内 <c>BattleContext</c>（回退到根 Context 拿全局服务）+
    /// <c>BattleDirectorSystem</c> 自启动跑一局，终局导演直接 <c>GoTo(ResultState)</c>。
    /// <para>场景进 <see cref="FlowState.Bag"/>：本状态退出（进结算）时自动卸载——场景内 Context / 对象池 / 敌人视觉
    /// 整棵撤，不写一行手动清理。OnEnter 加载完即返回（状态转为 active），战斗本身由场景内 director 推进。</para>
    /// </summary>
    public sealed class BattleState : FlowState
    {
        public override string ToString() => "战斗";

        protected override async UniTask OnEnter(CancellationToken ct)
        {
            await Bag.LoadScene("OutpostBattle", LoadSceneMode.Additive, ct: ct);
        }
    }
}
