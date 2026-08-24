using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.UI.UGui;
using Game.Framework.View;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 波间升级面板（UGUI 路径，战斗场景内、绑 <see cref="BattleContext"/>）：抉择时弹出、列出三选一卡片，
    /// 点卡片 = 一次 <see cref="ChooseUpgradeCommand"/>。候选集合用 <c>Bag.BindList</c> 增量绑定——换一批候选只增删对应卡片。
    /// View 只读订阅（经查询 Command）+ 外发命令，不碰 Model/System。这是 UGUI 侧 BindList 的落点（与 HUD 同属战斗子上下文）。
    /// </summary>
    public sealed class UpgradeChoiceView : MonoViewBase
    {
        [SerializeField, Tooltip("抉择时显示的整块（遮罩 + 标题 + 卡片容器）；非抉择态隐藏。")]
        private GameObject _content;

        [SerializeField, Tooltip("卡片容器（挂 HorizontalLayoutGroup 自动排布）；BindList 的目标。")]
        private Transform _cardContainer;

        [SerializeField, Tooltip("升级卡片 prefab（根含 Button，子节点 Title / Desc 为 TMP_Text）。")]
        private GameObject _cardPrefab;

        [SerializeField, Tooltip("面板标题（绑本地化 key upgrade/pick）。")]
        private TMP_Text _panelTitle;

        [SerializeField, Tooltip("面板操作提示（绑本地化 key upgrade/hint）。")]
        private TMP_Text _panelHint;

        protected override void Awake()
        {
            base.Awake(); // 注入 + 绑定 Context，之后即可经 Command 拿只读订阅源

            var rm = this.ExecuteCommand(new GetUpgradeChoiceCommand());
            var loc = this.GetUtility<ILocalizationUtility>();

            // 面板标题 / 操作提示绑 key（进战斗时取当前语言）。
            Bag.Subscribe(loc.TextRevision, _ =>
            {
                _panelTitle.text = loc.Get("upgrade/pick");
                _panelHint.text = loc.Get("upgrade/hint");
            });

            // 抉择开关 → 面板显隐（订阅即得当前值；初始 false 时面板隐藏）。
            Bag.Subscribe(rm.IsChoosing, choosing =>
            {
                if (_content != null) _content.SetActive(choosing);
            });

            // 候选集合 → 卡片（增量绑定：换一批候选只增删变化的卡片，不整表重建）。
            // 工厂第二参是该卡片专属子 bag：行内订阅（按钮点击 / 文案的语言订阅）随卡片离开列表自动退订。
            Bag.BindList(_cardContainer, rm.Choices, (opt, rowBag) =>
            {
                var go = Instantiate(_cardPrefab);

                // opt.Title / opt.Desc 是本地化 key（配置表存 key）：行内订文本修订，换语言或源就绪都重取。
                var title = go.transform.Find("Title");
                var desc = go.transform.Find("Desc");
                rowBag.Subscribe(loc.TextRevision, _ =>
                {
                    if (title != null) title.GetComponent<TMP_Text>().text = loc.Get(opt.Title);
                    if (desc != null) desc.GetComponent<TMP_Text>().text = loc.Get(opt.Desc);
                });

                var button = go.GetComponent<Button>();
                if (button != null)
                {
                    int id = opt.Id;
                    rowBag.Subscribe(button.onClick, () => this.ExecuteCommand(new ChooseUpgradeCommand(id)));
                }

                return go;
            });
        }
    }
}
