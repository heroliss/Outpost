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
    /// 游戏速度循环按钮（HUD 屏幕按钮，UGUI 路径）：点击在 0.25×→0.5×→1×→2×→4× 之间循环，
    /// 导演订阅速度写 <c>Time.timeScale</c> 实时缩放整场（弹丸/敌人/特效一起变速——慢放看清扫掠碰撞、快进看规模）。
    /// 只读订阅 <see cref="GetSimSpeedCommand"/> 回显当前倍率，写只走 <see cref="SetSimSpeedCommand"/>（读写分离同其余 View）。
    /// 非 1× 时按钮染暖色提示"当前非正常速度"。
    /// </summary>
    public sealed class GameSpeedButtonView : MonoViewBase
    {
        [SerializeField, Tooltip("速度切换按钮。")]
        private Button _button;

        [SerializeField, Tooltip("按钮文案（回显「速度 N×」）。")]
        private TMP_Text _label;

        [SerializeField, Tooltip("按钮底图（1× 中性灰、非 1× 暖色提示）。")]
        private Image _background;

        // 循环档位：慢放到 0.25×（逐帧看弹丸扫掠命中）、快进到 4×（快速看规模爬坡）。
        private static readonly float[] Steps = { 0.25f, 0.5f, 1f, 2f, 4f };

        private static readonly Color NormalColor = new(0.22f, 0.24f, 0.30f, 0.85f);  // 1×：暗灰（同托管关）
        private static readonly Color AlteredColor = new(0.85f, 0.55f, 0.20f, 0.90f); // 非 1×：暖橙提示

        private float _current = 1f;

        protected override void Awake()
        {
            base.Awake(); // 注入 + 绑定 Context，之后即可经 Command 拿只读订阅源

            // 回显：订阅即得当前值，速度任意时刻变化都刷新文案与配色；文案绑本地化 key（倍率 × 语言双源，§21）。
            var loc = this.GetUtility<ILocalizationUtility>();
            Bag.Subscribe(this.ExecuteCommand(new GetSimSpeedCommand()).CombineLatest(loc.Locale, (s, _) => s), s =>
            {
                _current = s;
                if (_label != null) _label.text = loc.Get("hud/speed", s.ToString("0.##"));
                if (_background != null) _background.color = Mathf.Approximately(s, 1f) ? NormalColor : AlteredColor;
            });

            // 点击 = 前进到下一档（到顶回绕；意图经命令中转，View 不碰 Model）。
            Bag.Subscribe(_button.onClick, () => this.ExecuteCommand(new SetSimSpeedCommand(NextStep(_current))));
        }

        // 找当前值最接近的档位、前进一档（回绕）。容差匹配防浮点不等（外部理论上只会设成档位值）。
        private static float NextStep(float current)
        {
            int idx = 0;
            float best = float.MaxValue;
            for (int i = 0; i < Steps.Length; i++)
            {
                float d = Mathf.Abs(Steps[i] - current);
                if (d < best) { best = d; idx = i; }
            }
            return Steps[(idx + 1) % Steps.Length];
        }
    }
}
