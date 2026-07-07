using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Commands;
using Game.Outpost.Save;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>标题页：游戏入口菜单 + 历史战绩概览。由 <c>TitleState</c> 打开 / 随其退出关闭。</summary>
    [UIWindow(Layer = UILayer.Page)]
    public sealed class TitleWindow : UIToolkitWindowBase
    {
        private Label _record;

        protected override void OnCreated()
        {
            var page = OutpostUiKit.FullPage(Root, "OUTPOST", new Color(0.07f, 0.10f, 0.14f, 1f));
            OutpostUiKit.Lbl(page, "哨站生存 —— 框架垂直切片");
            _record = OutpostUiKit.Lbl(page, ""); // 历史战绩，OnOpen 从存档读模型填（每次进标题刷新，含刚打完一局的回标题）
            _record.style.color = new Color(0.65f, 0.72f, 0.8f);
            OutpostUiKit.Btn(page, "开始游戏", () => this.ExecuteCommand(new StartBattleCommand()));
            // 框架看点：开一个模态弹窗（同一 UI 入口的窗口栈），把玩法接到框架能力 + 指向对照文档。
            OutpostUiKit.Btn(page, "框架看点", () => this.GetUtility<IUIUtility>().Open<AboutWindow>().Forget());
        }

        protected override void OnOpen(object args)
        {
            // 存档在启动（BootState）已载入 Model；打完一局回标题时也已由 SubmitRunResultCommand 更新——每次开窗直读当前值即最新。
            var rec = this.ExecuteCommand(new GetPlayerRecordCommand());
            _record.text = rec.Runs.CurrentValue == 0
                ? "尚无战绩 —— 开始你的第一场防守"
                : $"历史最佳 {rec.BestScore.CurrentValue} 分 · 最高波次 {rec.BestWave.CurrentValue} · 胜 {rec.Wins.CurrentValue}/{rec.Runs.CurrentValue}";
        }
    }
}
