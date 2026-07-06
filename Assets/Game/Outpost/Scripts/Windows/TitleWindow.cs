using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Commands;
using UnityEngine;

namespace Game.Outpost.Windows
{
    /// <summary>标题页：游戏入口菜单。由 <c>TitleState</c> 打开 / 随其退出关闭。</summary>
    [UIWindow(Layer = UILayer.Page)]
    public sealed class TitleWindow : UIToolkitWindowBase
    {
        protected override void OnCreated()
        {
            var page = OutpostUiKit.FullPage(Root, "OUTPOST", new Color(0.07f, 0.10f, 0.14f, 1f));
            OutpostUiKit.Lbl(page, "哨站生存 —— 框架垂直切片");
            OutpostUiKit.Btn(page, "开始游戏", () => this.ExecuteCommand(new StartBattleCommand()));
        }
    }
}
