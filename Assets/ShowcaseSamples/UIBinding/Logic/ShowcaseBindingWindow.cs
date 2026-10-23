using Game.Framework.UI;
using Game.Framework.UI.UGui;

namespace Showcase.SelfMade.UI
{
    /// <summary>ShowcaseBindingWindow 窗口逻辑。节点绑定字段在 ShowcaseBindingWindow.nodes.g.cs（重新生成会覆盖）；本文件只写业务逻辑，不会被覆盖。</summary>
    [UIWindow(Asset = "ShowcaseBindingWindow", Layer = UILayer.Window)]
    public partial class ShowcaseBindingWindow : UGuiWindowBase
    {
        protected override void OnCreated()
        {
            // 接线入口：订阅查询 Command、接按钮（绑定字段此时已就绪）。
        }
    }
}
