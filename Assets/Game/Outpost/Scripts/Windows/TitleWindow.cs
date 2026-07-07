using Cysharp.Threading.Tasks;
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
            // 框架看点：开一个模态弹窗（同一 UI 入口的窗口栈），把玩法接到框架能力 + 指向对照文档。
            OutpostUiKit.Btn(page, "框架看点", () => this.GetUtility<IUIUtility>().Open<AboutWindow>().Forget());
        }
    }
}
