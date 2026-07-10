using Cysharp.Threading.Tasks;
using Game.Framework.Audio;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.UI;
using Game.Framework.UI.Toolkit;
using Game.Outpost.Save;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// 设置弹窗（Popup + Modal，压在标题页上）：音量三滑条 + 语言切换。
    /// <b>本窗只是遥控器</b>——音量真源在 <c>IAudioUtility</c>、语言真源在 <c>ILocalizationUtility.Locale</c>，
    /// 滑条 / 按钮直改 Utility 即时生效（在播 BGM 立刻变、下层标题页文案实时切）；
    /// 关窗时一次 <see cref="SaveSettingsCommand"/> 收口落盘（不随滑条拖动高频写盘）。
    /// View 直连 Utility 是合法权限（<c>ICanGetUtility</c>，与开窗 / <c>Bag.Load</c> 同心智，§27/§29 demo 同款姿势）。
    /// </summary>
    [UIWindow(Layer = UILayer.Popup, Modal = true, Asset = "SettingsWindow")]
    public sealed class SettingsWindow : UIToolkitWindowBase
    {
        // 音效滑条的试听节流：拖动连发 change，最短间隔内只播一声（听得出音量差即可，不做机关枪）。
        private const float AuditionMinInterval = 0.12f;
        private float _lastAuditionTime;
        private AudioClip _auditionClip;

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

            Bag.SubscribeClick(Root.Q<Button>("close"), () => this.GetUtility<IUIUtility>().Close(this));
        }

        protected override void OnClose()
        {
            // 收口落盘：改动早已在 Utility 上生效，这里只存快照。无参调用的取消令牌绑根 Context（窗口非 Mono），
            // 窗口关闭不打断保存；落盘失败命令内部已兜底记录。
            this.ExecuteCommandAsync(new SaveSettingsCommand()).Forget();
        }

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
