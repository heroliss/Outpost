using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Audio;
using Game.Framework.Command;
using Game.Framework.Localization;
using Game.Framework.Logging;
using Game.Framework.Storage;
using Game.Framework.Systems;

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

                // 战斗后端偏好：-1 = 没选过（含老存档缺字段），保持 Model 默认（Ecs）。
                var prefs = ctx.GetModel<Battle.BattlePrefsModel>();
                if (settings.BattleBackend >= 0)
                    prefs.Backend.Value = (Battle.BattleSimBackend)settings.BattleBackend;
                prefs.ShowWreckHeatmap.Value = settings.WreckHeatmap;
                prefs.ExpansionBgm.Value = settings.ExpansionBgm;

                // 扩展包已安装：后台补一次初始化（拉版本/清单，内容已在缓存不重下）——不 await，
                // 启动不等它；音频侧按包状态懒加载，init 未完成前的战斗用默认曲、下一场自然接上。
                if (settings.ExpansionInstalled)
                    ctx.GetUtility<IAssetUtility>()
                        .Initialize(Game.Main.AssetPackages.OutpostExpansionPackage)
                        .Forget(LogExpansionInitFailure);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Error("设置载入失败，将按默认设置继续。", e, nameof(LoadSettingsCommand));
            }
        }

        private static void LogExpansionInitFailure(Exception exception)
        {
            if (exception is OperationCanceledException) return;
            Log.Warning($"扩展包启动初始化失败（内容仍在缓存，下次重试）：{exception.Message}",
                nameof(LoadSettingsCommand));
        }
    }

    /// <summary>
    /// 立即落盘当前设置。快照收集与写入策略归 <see cref="SettingsPersistenceSystem"/>；本命令只提供
    /// View / Flow 可调用、可等待的用户意图入口。存储失败保持异常传播，由调用方决定如何呈现。
    /// </summary>
    public readonly struct SaveSettingsCommand : IAsyncCommand
    {
        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
            => await ctx.GetSystem<SettingsPersistenceSystem>().SaveNow(cancellationToken);
    }

    /// <summary>
    /// 通知设置持久化 System：某个运行时真源已经变化。调用立即返回，连续变更由 System 合并写盘；
    /// 用于音量、语言这类 View 可直接修改 Utility、但仍应统一持久化策略的入口。
    /// </summary>
    public readonly struct RequestSettingsSaveCommand : ICommand
    {
        public void Execute(ICommandContext ctx)
            => ctx.GetSystem<SettingsPersistenceSystem>().RequestSave();
    }
}
