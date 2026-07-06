using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Commands;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>结算页：展示本局得分。分数经 <c>Open(args)</c> 传入（来自 <c>ResultState</c> 构造参数）。</summary>
    [UIWindow(Layer = UILayer.Page)]
    public sealed class ResultWindow : UIToolkitWindowBase
    {
        private Label _score;

        protected override void OnCreated()
        {
            var page = OutpostUiKit.FullPage(Root, "结算", new Color(0.07f, 0.11f, 0.08f, 1f));
            _score = OutpostUiKit.Lbl(page, "");
            OutpostUiKit.Btn(page, "回标题", () => this.ExecuteCommand(new GoToTitleCommand()));
        }

        protected override void OnOpen(object args) => _score.text = $"得分：{(args is int s ? s : 0)}";
    }
}
