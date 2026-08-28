using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Framework.UI;
using Game.Outpost.Windows;
using R3;

namespace Game.Outpost.Flow
{
    /// <summary>标题阶段：主菜单页。</summary>
    /// <remarks>
    /// 窗口生命周期归本状态：关窗动作进 <c>Bag</c> 而不是写在 OnExit——被顶替 / 宿主销毁时
    /// OnExit 不保证被调，Bag 才是可靠清理位（guide §20）。
    /// </remarks>
    public sealed class TitleState : FlowState
    {
        public override string ToString() => "标题";

        protected override async UniTask OnEnter(CancellationToken ct)
        {
            var ui = Context.GetUtility<IUIUtility>();
            await ui.OpenRequired<TitleWindow>(ct);
            Bag.Add(Disposable.Create(() => ui.Close<TitleWindow>()));
        }
    }
}
