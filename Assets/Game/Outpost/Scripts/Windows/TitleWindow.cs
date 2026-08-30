using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Commands;
using Game.Outpost.Net;
using Game.Outpost.Save;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// 标题页：游戏入口菜单 + 历史战绩概览。由 <c>TitleState</c> 打开 / 随其退出关闭。
    /// 布局在 <c>Res/UI/TitleWindow.uxml</c>（<see cref="UIWindowAttribute.Asset"/> 经资源系统加载、
    /// 共享 <c>Outpost.uss</c> 主题），本类只做查询接线与数据填充——布局与逻辑分离的标准姿势。
    /// 文案全部绑本地化 key（<c>BindLocalizedText</c>，§21）：设置窗切语言时本页在其下实时刷新。
    /// </summary>
    [UIWindow(Layer = UILayer.Page, Asset = "TitleWindow")]
    public sealed class TitleWindow : UIToolkitWindowBase
    {
        private Label _record;

        protected override void OnCreated()
        {
            // Root 已是 uxml clone 的结果，这里按 name 查询元素接线。
            _record = Root.Q<Label>("record");
            Bag.BindLocalizedText(Root.Q<Label>("subtitle"), "title/subtitle");
            Bag.BindLocalizedText(Root.Q<Button>("start"), "title/start");
            Bag.BindLocalizedText(Root.Q<Button>("about"), "title/about");
            Bag.BindLocalizedText(Root.Q<Button>("settings"), "title/settings");

            Bag.SubscribeClickAsync(Root.Q<Button>("start"),
                ct => this.ExecuteCommandAsync(new StartBattleCommand(), ct));

            // 全服排行（M4）：仅 dev 环境有对端（进程内 dev server），正式包直接藏入口。
            var leaderboard = Root.Q<Button>("leaderboard");
            leaderboard.style.display = OutpostNet.Available ? DisplayStyle.Flex : DisplayStyle.None;
            Bag.BindLocalizedText(leaderboard, "lb/title");
            Bag.SubscribeClickAsync(leaderboard,
                async ct => { await this.GetUtility<IUIUtility>().OpenRequired<LeaderboardWindow>(ct); });
            // 框架看点：开一个模态弹窗（同一 UI 入口的窗口栈），把玩法接到框架能力 + 指向对照文档。
            Bag.SubscribeClickAsync(Root.Q<Button>("about"),
                async ct => { await this.GetUtility<IUIUtility>().OpenRequired<AboutWindow>(ct); });
            // 设置：音量 / 语言（同为 Popup 层模态，压在标题之上——切语言时能看到本页文案跟着变）。
            Bag.SubscribeClickAsync(Root.Q<Button>("settings"),
                async ct => { await this.GetUtility<IUIUtility>().OpenRequired<SettingsWindow>(ct); });

            // 战绩行是「数据 × 文本修订」双源：修订同时覆盖换语言与延迟源就绪；OnOpen 再补一次数据面
            // （存档只在结算变化，每次开窗取最新战绩）。
            var loc = this.GetUtility<ILocalizationUtility>();
            Bag.Subscribe(loc.TextRevision, _ => RefreshRecord(loc));
        }

        protected override void OnOpen(object args)
            => RefreshRecord(this.GetUtility<ILocalizationUtility>());

        private void RefreshRecord(ILocalizationUtility loc)
        {
            // 存档在启动（BootState）已载入 Model；打完一局回标题时也已由 SubmitRunResultCommand 更新——直读当前值即最新。
            var rec = this.ExecuteCommand(new GetPlayerRecordCommand());
            _record.text = rec.Runs.CurrentValue == 0
                ? loc.Get("record/none")
                : loc.Get("record/summary", rec.BestScore.CurrentValue, rec.BestWave.CurrentValue, rec.Runs.CurrentValue);
        }
    }
}
