using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// 「框架看点」模态弹窗：把游戏里看得见的现象接到框架能力上，指向 <c>docs/outpost-guide.md</c> 深读。
    /// 本身也是个演示——Popup 层 + Modal 遮罩盖住标题页（窗口栈 / 模态是 UI 框架能力，见 guide §17）。
    /// 看点内容是静态文案，直接写在 <c>Res/UI/AboutWindow.uxml</c> 里，本类只接一个关闭按钮。
    /// </summary>
    [UIWindow(Layer = UILayer.Popup, Modal = true, Asset = "AboutWindow")]
    public sealed class AboutWindow : UIToolkitWindowBase
    {
        protected override void OnCreated()
            => Bag.SubscribeClick(Root.Q<Button>("close"), () => this.GetUtility<IUIUtility>().Close(this));
    }
}
