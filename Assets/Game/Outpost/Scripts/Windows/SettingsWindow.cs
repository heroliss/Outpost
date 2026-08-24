using System;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Audio;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Main;
using Game.Outpost.Battle;
using Game.Outpost.Save;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// 设置弹窗（Popup + Modal，压在标题页上）：音量三滑条 + 语言切换 + 战斗后端选择 + 扩展内容下载。
    /// <b>本窗只是遥控器</b>——音量真源在 <c>IAudioUtility</c>、语言真源在 <c>ILocalizationUtility.Locale</c>、
    /// 后端偏好真源在 <c>BattlePrefsModel</c>（经命令读写，下一局生效）、扩展包安装态真源在 <c>IAssetUtility</c>
    /// 的包状态；改动即时生效在各真源上，关窗时一次 <see cref="SaveSettingsCommand"/> 收口落盘（不随滑条拖动高频写盘）。
    /// View 直连 Utility 是合法权限（<c>ICanGetUtility</c>，与开窗 / <c>Bag.Load</c> 同心智，§27/§29 demo 同款姿势）。
    /// <para>扩展区演示「不自动初始化的第二资源包」消费全流程（§13 多包 / 按需下载）：
    /// Initialize（拉清单）→ <c>CreateAllDownloader</c>（显式下载器带进度）→ 完成即落盘安装标记；
    /// 下载刻意不随窗口关闭取消（包级内容不是窗口私有物），关窗后静默完成、下次开窗见「已启用」。</para>
    /// </summary>
    [UIWindow(Layer = UILayer.Popup, Modal = true, Asset = "SettingsWindow")]
    public sealed class SettingsWindow : UIToolkitWindowBase
    {
        // 音效滑条的试听节流：拖动连发 change，最短间隔内只播一声（听得出音量差即可，不做机关枪）。
        private const float AuditionMinInterval = 0.12f;
        private float _lastAuditionTime;
        private AudioClip _auditionClip;

        // 扩展内容区元素与状态。_expStatusKey 是状态短语的 l10n key（换语言时经 Locale 订阅重查）。
        private Label _expStatus;
        private VisualElement _expBar;
        private VisualElement _expFill;
        private Button _expButton;
        private VisualElement _expBgmRow; // 电台战斗曲开关行：仅扩展包已安装时显示
        private string _expStatusKey = "";
        private bool _closed; // 异步下载返回后 UI 已随窗销毁：只跳过界面更新，下载与落盘照常收尾

        protected override void OnCreated()
        {
            var audio = this.GetUtility<IAudioUtility>();
            var loc = this.GetUtility<ILocalizationUtility>();

            Bag.BindLocalizedText(Root.Q<Label>("title"), "settings/title");
            Bag.BindLocalizedText(Root.Q<Label>("lang-label"), "settings/language");
            Bag.BindLocalizedText(Root.Q<Button>("close"), "common/close");

            var master = Root.Q<Slider>("master");
            var music = Root.Q<Slider>("music");
            var sfx = Root.Q<Slider>("sfx");

            // Slider 不是 TextElement（label 是它的属性），本地化直接订 Locale 写回。
            Bag.Subscribe(loc.Locale, _ =>
            {
                master.label = loc.Get("settings/master");
                music.label = loc.Get("settings/music");
                sfx.label = loc.Get("settings/sfx");
            });

            // 初值从真源取、变更写回真源（主 × 组 × 单次的分组音量模型，§27）；即时作用于所有在播声音。
            master.value = audio.MasterVolume;
            music.value = audio.GetGroupVolume(AudioGroups.Music);
            sfx.value = audio.GetGroupVolume(AudioGroups.Sfx);
            master.RegisterValueChangedCallback(e => audio.MasterVolume = e.newValue);
            music.RegisterValueChangedCallback(e => audio.SetGroupVolume(AudioGroups.Music, e.newValue));
            sfx.RegisterValueChangedCallback(e =>
            {
                audio.SetGroupVolume(AudioGroups.Sfx, e.newValue);
                PlayAudition(audio); // 音效组没有常驻在播的声音，给一声试听反馈才听得出改了什么
            });
            PreloadAuditionAsync().Forget();

            // 语言切换：直调 SetLocale（同值 no-op 不重刷）；当前语言按钮描边高亮。按钮文案是语言自称，不本地化。
            var zh = Root.Q<Button>("lang-zh");
            var en = Root.Q<Button>("lang-en");
            Bag.SubscribeClick(zh, () => loc.SetLocale(OutpostLocales.ChineseSimplified));
            Bag.SubscribeClick(en, () => loc.SetLocale(OutpostLocales.English));
            Bag.Subscribe(loc.Locale, l =>
            {
                zh.EnableInClassList("op-btn--lang-active", l == OutpostLocales.ChineseSimplified);
                en.EnableInClassList("op-btn--lang-active", l == OutpostLocales.English);
            });

            // 战斗模拟后端（ADR-0030 双后端）：写走命令、读走查询命令的只读订阅源（View 不碰 Model，§1.1）。
            // 改动即写进 BattlePrefsModel（导演每局开局采样，下一局生效），落盘随关窗的设置快照。
            Bag.BindLocalizedText(Root.Q<Label>("backend-label"), "settings/backend");
            Bag.BindLocalizedText(Root.Q<Label>("backend-hint"), "settings/backend-hint");
            var ecsBtn = Root.Q<Button>("backend-ecs");
            var refBtn = Root.Q<Button>("backend-ref");
            Bag.SubscribeClick(ecsBtn, () => this.ExecuteCommand(new SetBattleBackendCommand(BattleSimBackend.Ecs)));
            Bag.SubscribeClick(refBtn, () => this.ExecuteCommand(new SetBattleBackendCommand(BattleSimBackend.Reference)));
            Bag.Subscribe(this.ExecuteCommand(new GetBattleBackendCommand()), b =>
            {
                ecsBtn.EnableInClassList("op-btn--lang-active", b == BattleSimBackend.Ecs);
                refBtn.EnableInClassList("op-btn--lang-active", b == BattleSimBackend.Reference);
            });

            Bag.SubscribeClick(Root.Q<Button>("close"), () => this.GetUtility<IUIUtility>().Close(this));

            SetupExpansion(loc);
        }

        protected override void OnClose()
        {
            _closed = true;
            // 收口落盘：改动早已在 Utility 上生效，这里只存快照。无参调用的取消令牌绑根 Context（窗口非 Mono），
            // 窗口关闭不打断保存；落盘失败命令内部已兜底记录。
            this.ExecuteCommandAsync(new SaveSettingsCommand()).Forget();
        }

        // ── 扩展内容（OutpostExpansionPackage · 增援电台）────────────────────────

        private void SetupExpansion(ILocalizationUtility loc)
        {
            _expStatus = Root.Q<Label>("exp-status");
            _expBar = Root.Q<VisualElement>("exp-bar");
            _expFill = Root.Q<VisualElement>("exp-fill");
            _expButton = Root.Q<Button>("exp-download");
            _expBgmRow = Root.Q<VisualElement>("exp-bgm-row");

            Bag.BindLocalizedText(Root.Q<Label>("exp-name"), "settings/expansion-name");
            Bag.BindLocalizedText(Root.Q<Label>("exp-desc"), "settings/expansion-desc");
            Bag.BindLocalizedText(_expButton, "settings/expansion-download");
            // 状态短语是"动态选 key"（下载中/已启用/失败），BindLocalizedText 只认固定 key——订 Locale 手动重查。
            Bag.Subscribe(loc.Locale, _ => RefreshExpansionStatusText(loc));

            // 电台战斗曲开关（同后端选择的读写姿势）：写走命令、读走查询命令的只读订阅源；
            // OutpostAudioSystem 订阅该偏好，战斗中切换即时换曲。行默认藏、扩展包已安装才显示。
            _expBgmRow.style.display = DisplayStyle.None;
            Bag.BindLocalizedText(Root.Q<Label>("exp-bgm-label"), "settings/expansion-bgm");
            var bgmOn = Root.Q<Button>("exp-bgm-on");
            var bgmOff = Root.Q<Button>("exp-bgm-off");
            Bag.BindLocalizedText(bgmOn, "common/on");
            Bag.BindLocalizedText(bgmOff, "common/off");
            Bag.SubscribeClick(bgmOn, () => this.ExecuteCommand(new SetExpansionBgmCommand(true)));
            Bag.SubscribeClick(bgmOff, () => this.ExecuteCommand(new SetExpansionBgmCommand(false)));
            Bag.Subscribe(this.ExecuteCommand(new GetExpansionBgmCommand()), on =>
            {
                bgmOn.EnableInClassList("op-btn--lang-active", on);
                bgmOff.EnableInClassList("op-btn--lang-active", !on);
            });

            _expBar.style.display = DisplayStyle.None;
            Bag.SubscribeClick(_expButton, () => DownloadExpansion(loc).Forget());

            // 开窗时的初始态：已安装（Ready 且无缺失下载）显示「已启用」，否则保持下载按钮。
            if (SaveSettingsCommand.IsExpansionInstalled(this.GetUtility<IAssetUtility>()))
                ShowExpansionReady(loc);
        }

        private async UniTaskVoid DownloadExpansion(ILocalizationUtility loc)
        {
            var assets = this.GetUtility<IAssetUtility>();
            _expButton.SetEnabled(false);
            SetExpansionStatus("settings/expansion-downloading", loc);
            try
            {
                // Initialize 幂等且普通失败不抛（结果写包状态）；调用者取消仍保持 OCE。
                await assets.Initialize(AssetPackages.OutpostExpansionPackage);
                if (assets.GetInitState(AssetPackages.OutpostExpansionPackage).CurrentValue != AssetInitState.Ready)
                    throw new InvalidOperationException("扩展包初始化未就绪（拉取版本/清单失败）。");

                var downloader = assets.CreateAllDownloader(AssetPackages.OutpostExpansionPackage);
                if (downloader.TotalCount > 0)
                {
                    if (!_closed)
                    {
                        _expBar.style.display = DisplayStyle.Flex;
                        // 进度订阅挂窗口 Bag：关窗自动退订，下载本身继续（不传窗口令牌，见类注释）。
                        Bag.Subscribe(downloader.Progress, r =>
                            _expFill.style.width = Length.Percent(Mathf.Clamp01(r.Progress) * 100f));
                    }
                    await downloader.Download();
                }

                // 下载成功即固化安装标记（不等关窗）：即刻落盘一次设置快照，防止「下完就退进程」丢标记。
                this.ExecuteCommandAsync(new SaveSettingsCommand()).Forget();
                if (!_closed) ShowExpansionReady(loc);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Settings] 扩展包下载失败：{e.Message}");
                if (_closed) return;
                _expBar.style.display = DisplayStyle.None;
                _expButton.SetEnabled(true);
                SetExpansionStatus("settings/expansion-failed", loc);
            }
        }

        private void ShowExpansionReady(ILocalizationUtility loc)
        {
            _expButton.style.display = DisplayStyle.None;
            _expBar.style.display = DisplayStyle.None;
            _expBgmRow.style.display = DisplayStyle.Flex; // 已安装才露出电台曲开关
            SetExpansionStatus("settings/expansion-ready", loc);
        }

        private void SetExpansionStatus(string key, ILocalizationUtility loc)
        {
            _expStatusKey = key;
            RefreshExpansionStatusText(loc);
        }

        private void RefreshExpansionStatusText(ILocalizationUtility loc)
            => _expStatus.text = _expStatusKey.Length == 0 ? string.Empty : loc.Get(_expStatusKey);

        // 试听 clip 经资源系统预载（开窗到首次拖动之间足够完成；未就绪时静默跳过一声，无碍）。
        private async UniTaskVoid PreloadAuditionAsync()
            => _auditionClip = await Bag.Load<AudioClip>("sfx_click");

        private void PlayAudition(IAudioUtility audio)
        {
            if (_auditionClip == null || Time.unscaledTime - _lastAuditionTime < AuditionMinInterval) return;
            _lastAuditionTime = Time.unscaledTime;
            audio.PlaySfx(_auditionClip);
        }
    }
}
