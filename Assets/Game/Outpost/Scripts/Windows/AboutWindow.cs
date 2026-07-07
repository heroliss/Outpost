using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using UnityEngine;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// 「框架看点」模态弹窗：把游戏里看得见的现象接到框架能力上，指向 <c>docs/outpost-guide.md</c> 深读。
    /// 本身也是个演示——Popup 层 + Modal 遮罩盖住标题页（窗口栈 / 模态是 UI 框架能力，见 guide §17）。
    /// </summary>
    [UIWindow(Layer = UILayer.Popup, Modal = true)]
    public sealed class AboutWindow : UIToolkitWindowBase
    {
        protected override void OnCreated()
        {
            var card = OutpostUiKit.Card(Root, "框架看点 · 这个 demo 串起了什么");

            OutpostUiKit.Bullet(card, "读写分离",
                "HUD / 升级面板只读订阅、只发命令，从不直接写数据。看 BattleHudView、UpgradeChoiceView");
            OutpostUiKit.Bullet(card, "子上下文随场景整棵撤",
                "战斗私有的 Model 与对象池注册在 BattleContext，退出战斗一并销毁，无跨局残留");
            OutpostUiKit.Bullet(card, "纯 C# 模拟接缝",
                "战斗规则藏在零引擎依赖的 IBattleSim 后，可 AOT / 可单测 / 未来置换 ECS 后端。看 Sim 程序集");
            OutpostUiKit.Bullet(card, "事件 → 表现翻译层",
                "模拟只发'刷怪/命中/受击'事件，BattleDirector 翻成池化演出；换后端时这层原样复用");
            OutpostUiKit.Bullet(card, "响应式集合增量绑定",
                "三选一升级卡片用 Bag.BindList，换一批候选只增删变化的卡片。看 UpgradeChoiceView");
            OutpostUiKit.Bullet(card, "对象池 / 流程状态机",
                "敌人 / 曳光 / 飘字 / 脉冲全走 Bag.Spawn 借还；Boot→Title→Battle→Result 用 IGameFlow");

            OutpostUiKit.Hint(card, "完整「游戏现象 ↔ 框架能力 ↔ 源码位置」对照见 docs/outpost-guide.md");

            OutpostUiKit.Btn(card, "关闭", () => this.GetUtility<IUIUtility>().Close(this));
        }
    }
}
