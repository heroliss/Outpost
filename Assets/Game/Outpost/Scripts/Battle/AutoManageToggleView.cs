using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 托管模式开关（HUD 屏幕按钮，UGUI 路径）：点击切换「托管开/关」，开启后波间三选一由导演自动选卡、玩家进入纯观战。
    /// 只读订阅 <see cref="UpgradeChoiceReadModel.AutoManaged"/> 回显按钮文案 / 配色，写只走 <see cref="SetAutoManageCommand"/>——
    /// 读写分离同其余 View：看得到状态、改不了 Model，切换意图经命令中转到导演。可随时点、随时开关。
    /// </summary>
    public sealed class AutoManageToggleView : MonoViewBase
    {
        [SerializeField, Tooltip("托管开关按钮。")]
        private Button _button;

        [SerializeField, Tooltip("按钮文案（回显「托管：开 / 关」）。")]
        private TMP_Text _label;

        [SerializeField, Tooltip("按钮底图（回显开 / 关配色）。")]
        private Image _background;

        private static readonly Color OnColor = new(0.30f, 0.72f, 0.95f, 0.92f);   // 托管开：青蓝
        private static readonly Color OffColor = new(0.22f, 0.24f, 0.30f, 0.85f);  // 托管关：暗灰

        private bool _current;

        protected override void Awake()
        {
            base.Awake(); // 注入 + 绑定 Context，之后即可经 Command 拿只读订阅源

            var rm = this.ExecuteCommand(new GetUpgradeChoiceCommand());

            // 回显：订阅即得当前值，托管状态任意时刻变化都刷新文案与配色。
            Bag.Subscribe(rm.AutoManaged, on =>
            {
                _current = on;
                if (_label != null) _label.text = on ? "托管：开" : "托管：关";
                if (_background != null) _background.color = on ? OnColor : OffColor;
            });

            // 点击 = 反转当前托管状态（意图经命令中转到导演，View 不碰 Model）。
            Bag.Subscribe(_button.onClick, () => this.ExecuteCommand(new SetAutoManageCommand(!_current)));
        }
    }
}
