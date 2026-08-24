using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.Network;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Net;
using Game.Outpost.Save;
using ObservableCollections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// 全服排行榜弹窗（Popup + Modal，标题页 / 结算页都能开）：开窗即拉 Top 10，
    /// <c>Bag.BindList</c> 增量绑行（§31——排行数据放 <c>ObservableList</c>，行视图随集合增删自动建销）；
    /// 网络失败给出错误文案 + 「刷新」重试（§32 失败语义：命令抛 <c>NetworkException</c>、View 决定呈现）。
    /// 自己（存档署名）那一行高亮。
    /// </summary>
    [UIWindow(Layer = UILayer.Popup, Modal = true, Asset = "LeaderboardWindow")]
    public sealed class LeaderboardWindow : UIToolkitWindowBase
    {
        private const int TopCount = 10;

        /// <summary>榜单一行的展示模型：名次在拉取后按序号算好（<c>BindList</c> 工厂不带索引）。</summary>
        private readonly struct Row
        {
            public readonly int Rank;
            public readonly LeaderboardEntry Entry;

            public Row(int rank, LeaderboardEntry entry)
            {
                Rank = rank;
                Entry = entry;
            }
        }

        private readonly ObservableList<Row> _rows = new();
        private Label _status;
        private Button _refresh;
        private string _statusKey;
        private bool _closed; // 拉取的取消令牌绑根 Context 不随窗口——关窗后到达的响应不再动本实例 UI

        protected override void OnCreated()
        {
            _status = Root.Q<Label>("status");
            _refresh = Root.Q<Button>("refresh");
            Bag.BindLocalizedText(Root.Q<Label>("title"), "lb/title");
            Bag.BindLocalizedText(_refresh, "lb/refresh");
            Bag.BindLocalizedText(Root.Q<Button>("close"), "common/close");
            Bag.SubscribeClick(Root.Q<Button>("close"), () => this.GetUtility<IUIUtility>().Close(this));
            Bag.SubscribeClick(_refresh, () => Refresh().Forget());
            Bag.Subscribe(this.GetUtility<ILocalizationUtility>().TextRevision, _ => RefreshStatusText());

            Bag.BindList(Root.Q<VisualElement>("list"), _rows, CreateRow);
        }

        protected override void OnOpen(object args) => Refresh().Forget();

        protected override void OnClose() => _closed = true;

        private async UniTaskVoid Refresh()
        {
            _refresh.SetEnabled(false);
            SetStatus("lb/loading");
            _rows.Clear();
            try
            {
                var resp = await this.ExecuteCommandAsync(new FetchLeaderboardCommand(TopCount));
                if (_closed) return; // 等待期间窗口已被关掉——别再动 UI

                if (resp == null || resp.Entries.Count == 0)
                {
                    SetStatus("lb/empty");
                    return;
                }
                for (int i = 0; i < resp.Entries.Count; i++)
                    _rows.Add(new Row(i + 1, resp.Entries[i]));
                SetStatus(null);
            }
            catch (NetworkException e)
            {
                Debug.LogWarning($"[LeaderboardWindow] 拉取排行榜失败（{e.Kind}）：{e.Message}");
                if (_closed) return;
                SetStatus("lb/error");
            }
            finally
            {
                if (!_closed) _refresh.SetEnabled(true);
            }
        }

        private VisualElement CreateRow(Row row, DisposableBag rowBag)
        {
            // 榜单数据是快照；只有格式化文案订文本修订，行离开列表时由自己的 rowBag 退订。
            var root = new VisualElement();
            root.AddToClassList("op-lb-row");

            var rank = new Label(row.Rank.ToString());
            rank.AddToClassList("op-lb-row__rank");
            var name = new Label(row.Entry.Player);
            name.AddToClassList("op-lb-row__name");
            var wave = new Label();
            var loc = this.GetUtility<ILocalizationUtility>();
            rowBag.Subscribe(loc.TextRevision, _ => wave.text = loc.Get("lb/wave", row.Entry.Wave));
            wave.AddToClassList("op-lb-row__wave");
            var score = new Label(row.Entry.Score.ToString("N0"));
            score.AddToClassList("op-lb-row__score");

            root.Add(rank);
            root.Add(name);
            root.Add(wave);
            root.Add(score);

            // 自己的署名行高亮——看得见"我在榜上哪里"
            if (row.Entry.Player == this.ExecuteCommand(new GetPlayerRecordCommand()).Callsign.CurrentValue)
                root.AddToClassList("op-lb-row--self");
            return root;
        }

        private void SetStatus(string key)
        {
            _statusKey = key;
            _status.style.display = key == null ? DisplayStyle.None : DisplayStyle.Flex;
            RefreshStatusText();
        }

        private void RefreshStatusText()
        {
            if (_statusKey != null)
                _status.text = this.GetUtility<ILocalizationUtility>().Get(_statusKey);
        }
    }
}
