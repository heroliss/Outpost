using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Audio;
using Game.Framework.Command;
using Game.Framework.Localization;
using Game.Framework.Storage;
using UnityEngine;

namespace Game.Outpost.Save
{
    /// <summary>
    /// 启动回灌：从存档读设置，灌回音频（主音量 + 逐组）与本地化（<c>SetLocale</c>）。
    /// 无存档 = 保持注册时的默认（音量全 1、语言按系统推断），不落盘——首次真正改设置才产生存档。
    /// 由 <c>BootState</c> 在进标题前 await；载入失败只记日志、按默认继续（同历史战绩的容错口径）。
    /// </summary>
    public readonly struct LoadSettingsCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            try
            {
                var settings = await ctx.GetUtility<IStorageUtility>().Load<OutpostSettings>(StorageKeys.Settings, cancellationToken);
                if (settings == null) return;

                var audio = ctx.GetUtility<IAudioUtility>();
                audio.MasterVolume = settings.MasterVolume;
                audio.SetGroupVolume(AudioGroups.Music, settings.MusicVolume);
                audio.SetGroupVolume(AudioGroups.Sfx, settings.SfxVolume);

                if (!string.IsNullOrEmpty(settings.Locale))
                    ctx.GetUtility<ILocalizationUtility>().SetLocale(settings.Locale);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Debug.LogException(e); // 设置载入失败按默认继续，不把玩家卡在启动
            }
        }
    }

    /// <summary>
    /// 落盘当前设置：从两个 Utility 的<b>运行时真源</b>收集当前值（音量在 <c>IAudioUtility</c>、语言在
    /// <c>ILocalizationUtility.Locale</c>）拼成 <see cref="OutpostSettings"/> 快照保存。
    /// 由设置窗关闭时触发——改动即时生效在 Utility 上，落盘只在收口时做一次，不随滑条拖动高频写盘。
    /// </summary>
    public readonly struct SaveSettingsCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            var audio = ctx.GetUtility<IAudioUtility>();
            var settings = new OutpostSettings
            {
                MasterVolume = audio.MasterVolume,
                MusicVolume = audio.GetGroupVolume(AudioGroups.Music),
                SfxVolume = audio.GetGroupVolume(AudioGroups.Sfx),
                Locale = ctx.GetUtility<ILocalizationUtility>().Locale.CurrentValue,
            };
            try
            {
                await ctx.GetUtility<IStorageUtility>().Save(StorageKeys.Settings, settings, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Debug.LogException(e); // 落盘失败仅记录：本会话内设置已生效，下次启动回落旧值
            }
        }
    }
}
