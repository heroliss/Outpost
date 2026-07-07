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
    /// 战斗 HUD（UGUI 路径）：血条 + 波次 + 击杀 + 得分，只读绑定 <see cref="BattleModel"/>。
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

        protected override void Awake()
        {
            base.Awake(); // 注入 + 绑定 Context，之后即可经 Command 拿只读订阅源

            var rm = this.ExecuteCommand(new GetBattleReadModelCommand());

            // 血条 = 当前 / 上限，两个流合成一个比例（任一变化都重算）。
            Bag.Subscribe(rm.Hp.CombineLatest(rm.MaxHp, (hp, max) => (hp, max)), t =>
            {
                float ratio = t.max > 0f ? Mathf.Clamp01(t.hp / t.max) : 0f;
                if (_hpFill != null) _hpFill.fillAmount = ratio;
                if (_hpText != null) _hpText.text = $"{Mathf.CeilToInt(t.hp)} / {Mathf.CeilToInt(t.max)}";
            });

            Bag.Subscribe(rm.Wave.CombineLatest(rm.WaveCount, (w, c) => (w, c)), t =>
            {
                if (_waveText != null) _waveText.text = $"波次 {t.w} / {t.c}";
            });

            Bag.Subscribe(rm.Kills, k => { if (_killsText != null) _killsText.text = $"击杀 {k}"; });
            Bag.Subscribe(rm.Score, s => { if (_scoreText != null) _scoreText.text = $"得分 {s}"; });
        }
    }
}
