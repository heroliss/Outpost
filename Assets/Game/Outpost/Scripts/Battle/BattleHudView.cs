using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.View;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 战斗 HUD（UGUI 路径）：血条 + 波次 + 击杀 + 得分，只读绑定 <see cref="BattleModel"/>；
    /// 另有两处纯表现动画——波次开场横幅（波次值变化触发）与受击红屏闪（血量下降触发），
    /// 都从只读数据流推导，View 不需要 director 额外通知。
    /// 这是"UGUI × Toolkit 混用"的落点——标题/结算走 Toolkit 窗口栈，战斗 HUD 是战斗场景内的独立 UGUI Canvas 视图
    /// （非窗口，不占 UI 入口，与根 Context 的 MonoToolkitUI 并存）。View 只经查询 Command 读、不碰 Model。
    /// </summary>
    public sealed class BattleHudView : MonoViewBase
    {
        [SerializeField] private Image _hpFill;
        [SerializeField] private TMP_Text _hpText;
        [SerializeField] private TMP_Text _waveText;
        [SerializeField] private TMP_Text _killsText;
        [SerializeField] private TMP_Text _scoreText;

        [SerializeField, Tooltip("波次开场横幅（居中大字，波次变化时弹出后淡出）。")]
        private TMP_Text _waveBanner;

        [SerializeField, Tooltip("受击红屏闪（全屏 Image，blocksRaycasts 必须关）。")]
        private Image _damageFlash;

        private static readonly Color HpGreen = new(0.35f, 0.92f, 0.45f);
        private static readonly Color HpAmber = new(0.95f, 0.75f, 0.30f);
        private static readonly Color HpRed = new(0.95f, 0.30f, 0.25f);

        private float _prevHp = float.NaN;
        private float _flashAlpha;
        private int _bannerWave;
        private float _bannerElapsed = float.MaxValue;

        private const float BannerIn = 0.15f;
        private const float BannerHold = 1.0f;
        private const float BannerOut = 0.45f;

        protected override void Awake()
        {
            base.Awake(); // 注入 + 绑定 Context，之后即可经 Command 拿只读订阅源

            var rm = this.ExecuteCommand(new GetBattleReadModelCommand());

            // 血条 = 当前 / 上限，两个流合成一个比例（任一变化都重算）；血量下降时触发红屏闪。
            Bag.Subscribe(rm.Hp.CombineLatest(rm.MaxHp, (hp, max) => (hp, max)), t =>
            {
                float ratio = t.max > 0f ? Mathf.Clamp01(t.hp / t.max) : 0f;
                _hpFill.fillAmount = ratio;
                _hpFill.color = ratio > 0.5f
                    ? Color.Lerp(HpAmber, HpGreen, (ratio - 0.5f) * 2f)
                    : Color.Lerp(HpRed, HpAmber, ratio * 2f);
                _hpText.text = $"{Mathf.CeilToInt(t.hp)} / {Mathf.CeilToInt(t.max)}";

                if (!float.IsNaN(_prevHp) && t.hp < _prevHp - 0.01f)
                    _flashAlpha = 0.32f;
                _prevHp = t.hp;
            });

            Bag.Subscribe(rm.Wave, w =>
            {
                _waveText.text = $"波次 {w}";

                // 波次号首次变到某个 ≥1 的值 = 新一波开场，弹横幅（无限模式无"最终波"）。
                if (w >= 1 && w != _bannerWave)
                {
                    _bannerWave = w;
                    _bannerElapsed = 0f;
                    _waveBanner.text = $"第 {w} 波";
                }
            });

            Bag.Subscribe(rm.Kills, k => _killsText.text = $"击杀 {k}");
            Bag.Subscribe(rm.Score, s => _scoreText.text = $"得分 {s}");
        }

        private void Update()
        {
            // 受击红闪衰减。
            if (_flashAlpha > 0f)
            {
                _flashAlpha = Mathf.Max(0f, _flashAlpha - Time.deltaTime * 1.1f);
                var c = _damageFlash.color;
                c.a = _flashAlpha;
                _damageFlash.color = c;
            }

            // 波次横幅：弹入 → 停留 → 淡出。
            float total = BannerIn + BannerHold + BannerOut;
            if (_bannerElapsed < total)
            {
                _bannerElapsed += Time.deltaTime;
                float alpha;
                if (_bannerElapsed < BannerIn) alpha = _bannerElapsed / BannerIn;
                else if (_bannerElapsed < BannerIn + BannerHold) alpha = 1f;
                else alpha = Mathf.Clamp01(1f - (_bannerElapsed - BannerIn - BannerHold) / BannerOut);

                float scaleT = Mathf.Clamp01(_bannerElapsed / 0.25f);
                _waveBanner.transform.localScale = Vector3.one * Mathf.Lerp(1.25f, 1f, 1f - (1f - scaleT) * (1f - scaleT));

                var c = _waveBanner.color;
                c.a = alpha;
                _waveBanner.color = c;
            }
        }
    }
}
