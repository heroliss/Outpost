using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Commands;
using Game.Outpost.Flow;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>结算页：展示本局胜负与成绩。结果经 <c>Open(args)</c> 传入（来自 <c>ResultState</c> 构造参数）。</summary>
    [UIWindow(Layer = UILayer.Page)]
    public sealed class ResultWindow : UIToolkitWindowBase
    {
        private Label _verdict;
        private Label _detail;

        protected override void OnCreated()
        {
            var page = OutpostUiKit.FullPage(Root, "结算", new Color(0.07f, 0.11f, 0.08f, 1f));
            _verdict = OutpostUiKit.Lbl(page, "");
            _verdict.style.fontSize = 22;
            _detail = OutpostUiKit.Lbl(page, "");
            OutpostUiKit.Btn(page, "回标题", () => this.ExecuteCommand(new GoToTitleCommand()));
        }

        protected override void OnOpen(object args)
        {
            var r = args is BattleResult br ? br : default;
            _verdict.text = r.Victory ? "胜利！哨站守住了" : "失守——哨站沦陷";
            _verdict.style.color = r.Victory ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.9f, 0.45f, 0.4f);
            _detail.text = $"抵达波次 {r.Wave}/{r.WaveCount}　击杀 {r.Kills}　得分 {r.Score}";
        }
    }
}
