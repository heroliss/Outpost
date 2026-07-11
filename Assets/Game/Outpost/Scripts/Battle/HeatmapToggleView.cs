using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.View;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 泥地热力图开关（HUD 屏幕按钮，UGUI 路径）：点击切换「泥地图：开/关」，开启后叠加显示模拟侧残骸密度格
    /// （越亮减速越狠——把"残骸是防御地形"这条规则直读出来，ADR-0031）。纯表现开关，切换即时生效。
    /// 只读订阅 <see cref="GetWreckHeatmapCommand"/> 回显文案 / 配色，写只走 <see cref="SetWreckHeatmapCommand"/>（读写分离同其余 View）。
    /// </summary>
    public sealed class HeatmapToggleView : MonoViewBase
    {
        [SerializeField, Tooltip("热力图开关按钮。")]
        private Button _button;

        [SerializeField, Tooltip("按钮文案（回显「泥地图：开 / 关」）。")]
        private TMP_Text _label;

        [SerializeField, Tooltip("按钮底图（回显开 / 关配色）。")]
        private Image _background;

        private static readonly Color OnColor = new(0.85f, 0.45f, 0.16f, 0.92f);   // 开：暖橙（呼应热力图色）
        private static readonly Color OffColor = new(0.22f, 0.24f, 0.30f, 0.85f);  // 关：暗灰

        private bool _current;

        protected override void Awake()
        {
            base.Awake(); // 注入 + 绑定 Context

            var loc = this.GetUtility<ILocalizationUtility>();
            Bag.Subscribe(this.ExecuteCommand(new GetWreckHeatmapCommand()).CombineLatest(loc.Locale, (on, _) => on), on =>
            {
                _current = on;
                if (_label != null) _label.text = loc.Get(on ? "hud/heatmap-on" : "hud/heatmap-off");
                if (_background != null) _background.color = on ? OnColor : OffColor;
            });

            Bag.Subscribe(_button.onClick, () => this.ExecuteCommand(new SetWreckHeatmapCommand(!_current)));
        }
    }
}
