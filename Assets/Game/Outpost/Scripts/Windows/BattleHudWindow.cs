using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Commands;
using UnityEngine;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// 战斗 HUD。M0 占位：只有一个「结束战斗」按钮验证流程闭环；
    /// M1 起换成真实战场 HUD（血量 / 波次 / 击杀的 RP 绑定）。
    /// </summary>
    [UIWindow(Layer = UILayer.Page)]
    public sealed class BattleHudWindow : UIToolkitWindowBase
    {
        protected override void OnCreated()
        {
            var page = OutpostUiKit.FullPage(Root, "战斗中", new Color(0.12f, 0.07f, 0.06f, 1f));
            OutpostUiKit.Lbl(page, "（M0 占位——M1 起这里是真实战场）");
            OutpostUiKit.Btn(page, "结束战斗（占位胜利）", () => this.ExecuteCommand(new EndBattleCommand(100)));
        }
    }
}
