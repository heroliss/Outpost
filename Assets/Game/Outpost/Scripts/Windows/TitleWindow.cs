using Cysharp.Threading.Tasks;
using Game.Framework.Common;
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
    /// </summary>
    [UIWindow(Layer = UILayer.Page, Asset = "TitleWindow")]
    public sealed class TitleWindow : UIToolkitWindowBase
    {
        private Label _record;

        protected override void OnCreated()
        {
            // Root 已是 uxml clone 的结果，这里按 name 查询元素接线。
            _record = Root.Q<Label>("record");
            Bag.SubscribeClick(Root.Q<Button>("start"), () => this.ExecuteCommand(new StartBattleCommand()));
            // 框架看点：开一个模态弹窗（同一 UI 入口的窗口栈），把玩法接到框架能力 + 指向对照文档。
            Bag.SubscribeClick(Root.Q<Button>("about"), () => this.GetUtility<IUIUtility>().Open<AboutWindow>().Forget());
        }

        protected override void OnOpen(object args)
        {
            // 存档在启动（BootState）已载入 Model；打完一局回标题时也已由 SubmitRunResultCommand 更新——每次开窗直读当前值即最新。
            var rec = this.ExecuteCommand(new GetPlayerRecordCommand());
            _record.text = rec.Runs.CurrentValue == 0
                ? "尚无战绩 —— 开始你的第一场防守"
                : $"历史最佳 {rec.BestScore.CurrentValue} 分 · 最远 {rec.BestWave.CurrentValue} 波 · 共 {rec.Runs.CurrentValue} 局";
        }
    }
}
