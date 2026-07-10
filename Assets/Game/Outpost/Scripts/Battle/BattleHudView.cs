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

        [SerializeField, Tooltip("性能行（后端 | 敌人数 | 残骸数 | 模拟耗时 | fps）——两个 Sim 后端同题对比的实时度量。")]
        private TMP_Text _perfText;

        private static readonly Color HpGreen = new(0.35f, 0.92f, 0.45f);
        private static readonly Color HpAmber = new(0.95f, 0.75f, 0.30f);
        private static readonly Color HpRed = new(0.95f, 0.30f, 0.25f);

        private float _prevHp = float.NaN;
        private float _flashAlpha;
        private int _bannerWave;
        private float _bannerElapsed = float.MaxValue;

        // 性能行的订阅缓存（订阅只写字段，Update 按节流间隔拼串——避免每帧 3 个流各自触发字符串分配）。
        private string _backendName = "";
        private int _enemyCount;
        private int _wreckCount;
        private float _simTickMs;
        private float _fpsSmoothed;
        private float _perfRefreshTimer;
        private const float PerfRefreshInterval = 0.25f;

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

                // 红屏闪只对"明显掉血"触发：平台期漏怪伤害是持续小流量（每帧都在掉一点），
                // 逐帧触发会变成常亮红雾——阈值取上限的 2%（导演侧的受创演出另有聚合窗口）。
                if (!float.IsNaN(_prevHp) && _prevHp - t.hp > Mathf.Max(1f, t.max * 0.02f))
                    _flashAlpha = 0.32f;
                _prevHp = t.hp;
            });

            // 文案 × 语言双源：UGUI/TMP 侧没有 BindLocalizedText 语法糖，按 §21 姿势 CombineLatest(数据, Locale)——
            // 进战斗时语言已定（设置入口只在标题），但绑定姿势保持响应式，语言维度零额外心智。
            var loc = this.GetUtility<ILocalizationUtility>();
            Bag.Subscribe(rm.Wave.CombineLatest(loc.Locale, (w, _) => w), w =>
            {
                _waveText.text = loc.Get("hud/wave", w);

                // 波次号首次变到某个 ≥1 的值 = 新一波开场，弹横幅（无限模式无"最终波"；语言重推不满足 w != _bannerWave，不会重弹）。
                if (w >= 1 && w != _bannerWave)
                {
                    _bannerWave = w;
                    _bannerElapsed = 0f;
                    _waveBanner.text = loc.Get("hud/banner", w);
                }
            });

            Bag.Subscribe(rm.Kills.CombineLatest(loc.Locale, (k, _) => k), k => _killsText.text = loc.Get("hud/kills", k));
            Bag.Subscribe(rm.Score.CombineLatest(loc.Locale, (s, _) => s), s => _scoreText.text = loc.Get("hud/score", s));

            // 性能行：订阅只缓存值，拼串在 Update 里节流——EnemyCount/SimTickMs 每帧都变，逐次拼串太浪费。
            Bag.Subscribe(rm.Backend, b => _backendName = b);
            Bag.Subscribe(rm.EnemyCount, c => _enemyCount = c);
            Bag.Subscribe(rm.WreckCount, c => _wreckCount = c);
            Bag.Subscribe(rm.SimTickMs, ms => _simTickMs = ms);
        }

        private void Update()
        {
            // 性能行：fps 指数平滑，按节流间隔刷新文本。
            float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
            _fpsSmoothed = _fpsSmoothed <= 0f ? fps : Mathf.Lerp(_fpsSmoothed, fps, 0.1f);
            _perfRefreshTimer -= Time.unscaledDeltaTime;
            if (_perfRefreshTimer <= 0f)
            {
                _perfRefreshTimer = PerfRefreshInterval;
                _perfText.text = $"{_backendName} · 敌 {_enemyCount} · 残骸 {_wreckCount} · 模拟 {_simTickMs:F2}ms · {_fpsSmoothed:F0}fps";
            }

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
