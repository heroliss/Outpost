using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Commands;
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

            Bag.SubscribeClick(Root.Q<Button>("start"), () => this.ExecuteCommand(new StartBattleCommand()));
            // 框架看点：开一个模态弹窗（同一 UI 入口的窗口栈），把玩法接到框架能力 + 指向对照文档。
            Bag.SubscribeClick(Root.Q<Button>("about"), () => this.GetUtility<IUIUtility>().Open<AboutWindow>().Forget());
            // 设置：音量 / 语言（同为 Popup 层模态，压在标题之上——切语言时能看到本页文案跟着变）。
            Bag.SubscribeClick(Root.Q<Button>("settings"), () => this.GetUtility<IUIUtility>().Open<SettingsWindow>().Forget());

            // 战绩行是「数据 × 语言」双源文本：数据是打开时的快照（存档只在结算变化），语言可被设置窗实时切——
            // 订 Locale 重算（订阅即得当前值 = 首次填充），OnOpen 再补一次数据面（每次开窗取最新战绩）。
            var loc = this.GetUtility<ILocalizationUtility>();
            Bag.Subscribe(loc.Locale, _ => RefreshRecord(loc));
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
