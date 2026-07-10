using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 撤离按钮（HUD 屏幕按钮，UGUI 路径）：点击即收束本局、按当前战绩进结算。
    /// 无限模式下托管稳态可以永续，撤离是"把分数落袋"的常规出口——按钮常驻可见。
    /// 一按钮一命令（<see cref="RetreatCommand"/>）；按钮文案绑本地化 key（进战斗时取当前语言）。
    /// </summary>
    public sealed class RetreatButtonView : MonoViewBase
    {
        [SerializeField, Tooltip("撤离按钮。")]
        private Button _button;

        [SerializeField, Tooltip("按钮文案（绑本地化 key hud/retreat）。")]
        private TMP_Text _label;

        protected override void Awake()
        {
            base.Awake(); // 注入 + 绑定 Context
            Bag.Subscribe(_button.onClick, () => this.ExecuteCommand(new RetreatCommand()));

            var loc = this.GetUtility<ILocalizationUtility>();
            Bag.Subscribe(loc.Locale, _ => _label.text = loc.Get("hud/retreat"));
        }
    }
}
