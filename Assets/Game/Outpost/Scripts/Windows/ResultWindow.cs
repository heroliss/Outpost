using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Commands;
using Game.Outpost.Flow;
using Game.Outpost.Net;
using Game.Outpost.Save;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// 结算页：展示本局结局（主动撤离 / 哨站失守）与成绩，并对照历史存档
    /// （战绩已在 <c>ResultState</c> 并入存档后经 <see cref="ResultArgs"/> 传入）。
    /// 布局在 <c>Res/UI/ResultWindow.uxml</c>，本类只做查询接线与按结局填充文案 / 颜色。
    /// 固定文案绑 key；成绩行在 OnOpen 一次性 <c>loc.Get</c> 填充——语言入口只在标题页，结算期间语言不变。
    /// </summary>
    [UIWindow(Layer = UILayer.Page, Asset = "ResultWindow")]
    public sealed class ResultWindow : UIToolkitWindowBase
    {
        private static readonly Color RetreatColor = new(0.45f, 0.85f, 0.9f);
        private static readonly Color DefeatColor = new(0.9f, 0.45f, 0.4f);
        private static readonly Color RecordColor = new(0.65f, 0.72f, 0.8f);
        private static readonly Color NewBestColor = new(1f, 0.85f, 0.35f);

        private Label _verdict;
        private Label _detail;
        private Label _record;
        private Label _rank;

        protected override void OnCreated()
        {
            _verdict = Root.Q<Label>("verdict");
            _detail = Root.Q<Label>("detail");
            _record = Root.Q<Label>("record");
            _rank = Root.Q<Label>("rank");
            Bag.BindLocalizedText(Root.Q<Label>("title"), "result/title");
            Bag.BindLocalizedText(Root.Q<Button>("back"), "result/back");
            Bag.SubscribeClickAsync(Root.Q<Button>("back"),
                ct => this.ExecuteCommandAsync(new GoToTitleCommand(), ct));

            // 全服排行入口（M4）：仅 dev 环境有对端；本局名次行由 OnOpen 按上传结果显隐。
            var leaderboard = Root.Q<Button>("leaderboard");
            leaderboard.style.display = OutpostNet.Available ? DisplayStyle.Flex : DisplayStyle.None;
            Bag.BindLocalizedText(leaderboard, "lb/title");
            Bag.SubscribeClickAsync(leaderboard,
                async ct => { await this.GetUtility<IUIUtility>().OpenRequired<LeaderboardWindow>(ct); });
        }

        protected override void OnOpen(object args)
        {
            var a = args is ResultArgs ra ? ra : default;
            var r = a.Result;
            var loc = this.GetUtility<ILocalizationUtility>();

            // 无限模式无胜负：主动撤离（常规收束）或哨站失守，都比拼坚持到第几波。
            _verdict.text = loc.Get(r.Retreated ? "result/retreat" : "result/defeat", r.Wave);
            _verdict.style.color = r.Retreated ? RetreatColor : DefeatColor;
            _detail.text = loc.Get("result/detail", r.Kills, r.Score);

            // 存档已在进本状态前并入，这里只读展示历史面（新纪录高亮）。ReadOnlyReactiveProperty 直读当前值——结算页是快照、不需订阅。
            var rec = this.ExecuteCommand(new GetPlayerRecordCommand());
            _record.text = a.NewBest
                ? loc.Get("record/new-best", rec.BestScore.CurrentValue)
                : loc.Get("record/summary", rec.BestScore.CurrentValue, rec.BestWave.CurrentValue, rec.Runs.CurrentValue);
            _record.style.color = a.NewBest ? NewBestColor : RecordColor;

            // 全服名次行：ResultState 上传成功才有值（0 = 无网络 / 失败 / 正式包，整行藏掉不留空占位）。
            _rank.style.display = a.ServerRank > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (a.ServerRank > 0)
                _rank.text = loc.Get("result/rank", a.ServerRank);
        }
    }
}
