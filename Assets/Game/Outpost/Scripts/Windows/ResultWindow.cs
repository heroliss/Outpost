using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Commands;
using Game.Outpost.Flow;
using Game.Outpost.Save;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// 结算页：展示本局胜负与成绩，并对照历史存档（战绩已在 <c>ResultState</c> 并入存档后经 <see cref="ResultArgs"/> 传入）。
    /// </summary>
    [UIWindow(Layer = UILayer.Page)]
    public sealed class ResultWindow : UIToolkitWindowBase
    {
        private static readonly Color RecordColor = new(0.65f, 0.72f, 0.8f);
        private static readonly Color NewBestColor = new(1f, 0.85f, 0.35f);

        private Label _verdict;
        private Label _detail;
        private Label _record;

        protected override void OnCreated()
        {
            var page = OutpostUiKit.FullPage(Root, "结算", new Color(0.07f, 0.11f, 0.08f, 1f));
            _verdict = OutpostUiKit.Lbl(page, "");
            _verdict.style.fontSize = 22;
            _detail = OutpostUiKit.Lbl(page, "");
            _record = OutpostUiKit.Lbl(page, "");
            OutpostUiKit.Btn(page, "回标题", () => this.ExecuteCommand(new GoToTitleCommand()));
        }

        protected override void OnOpen(object args)
        {
            var a = args is ResultArgs ra ? ra : default;
            var r = a.Result;
            // 无限模式无胜负：终局即失守，比拼坚持到第几波。
            _verdict.text = $"哨站失守 · 第 {r.Wave} 波";
            _verdict.style.color = new Color(0.9f, 0.45f, 0.4f);
            _detail.text = $"击杀 {r.Kills}　得分 {r.Score}";

            // 存档已在进本状态前并入，这里只读展示历史面（新纪录高亮）。ReadOnlyReactiveProperty 直读当前值——结算页是快照、不需订阅。
            var rec = this.ExecuteCommand(new GetPlayerRecordCommand());
            _record.text = a.NewBest
                ? $"新纪录！历史最佳 {rec.BestScore.CurrentValue} 分"
                : $"历史最佳 {rec.BestScore.CurrentValue} 分 · 最远 {rec.BestWave.CurrentValue} 波 · 共 {rec.Runs.CurrentValue} 局";
            _record.style.color = a.NewBest ? NewBestColor : RecordColor;
        }
    }
}
