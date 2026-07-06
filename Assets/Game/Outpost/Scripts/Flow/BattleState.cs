using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Framework.UI;
using Game.Outpost.Windows;
using R3;

namespace Game.Outpost.Flow
{
    /// <summary>
    /// 战斗阶段。M0 占位：只开占位 HUD，「结束战斗」按钮直接进结算。
    /// M1 起：InstallBindings 注册战斗模拟与战斗 Model，OnEnter 加载战斗场景（Bag.LoadScene）。
    /// </summary>
    public sealed class BattleState : FlowState
    {
        public override string ToString() => "战斗";

        protected override async UniTask OnEnter(CancellationToken ct)
        {
            var ui = Context.GetUtility<IUIUtility>();
            await ui.Open<BattleHudWindow>(ct);
            Bag.Add(Disposable.Create(() => ui.Close<BattleHudWindow>()));
        }
    }
}
