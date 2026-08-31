using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Audio;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Localization;
using Game.Framework.Logging;
using Game.Framework.Storage;
using Game.Framework.Systems;
using Game.Outpost.Battle;

namespace Game.Outpost.Save
{
    /// <summary>
    /// Outpost 设置的持久化策略所有者（owner）：从各运行时真源收集快照，并把连续变更合并成一次异步写盘。
    /// </summary>
    /// <remarks>
    /// 设置窗、战斗 HUD 都只是修改入口；保存不能依赖某一个窗口的 <c>OnClose</c>。持久化设置每次
    /// 真正变化后调用 <see cref="RequestSave"/>，本 System 用短延迟合并滑条连发；需要明确提交点
    /// （窗口正常关闭、扩展包安装完成）时调用 <see cref="SaveNow"/> 取消尚未开始的延迟并立即排队。
    /// <para>
    /// 已经进入 <see cref="IStorageUtility"/> FIFO 的写入不因后续变更取消：新快照会排在其后，最终以
    /// 最新值落盘。System 随根 Context 释放；释放只取消尚未开始的延迟，Context token 负责终止在途 IO。
    /// </para>
    /// </remarks>
    public sealed class SettingsPersistenceSystem : ISystem, IHasGameContext, IDisposable
    {
        private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(250);

        private GameContext _context;
        private CancellationTokenSource _pendingDelay;
        private bool _disposed;

        IGameContext IHasGameContext.Context => _context;

        /// <summary>
        /// 请求保存当前设置。连续调用只保留最后一个 250ms 延迟，适合音量滑条等高频输入；
        /// 保存失败由本 System 统一记录，因为调用方没有可等待的 task。
        /// </summary>
        public void RequestSave()
        {
            MainThreadGuard.AssertMainThread(nameof(SettingsPersistenceSystem));
            ThrowIfDisposed();

            var next = CancellationTokenSource.CreateLinkedTokenSource(_context.CancellationToken);
            var previous = _pendingDelay;

            // 先发布新 owner 再取消旧 owner。Cancel 会同步运行 continuation；旧 continuation 不能把
            // 本次刚发布的 owner 清空，也不能让一个坏取消回调截断这次设置变更。
            _pendingDelay = next;
            CancelOwnerSafely(previous, "被后续设置变更替换的保存延迟");
            SaveAfterDelay(next).Forget(LogUnexpectedObserverFailure);
        }

        /// <summary>
        /// 立即保存当前快照。取消尚未开始的合并延迟，但不抢断已经进入存储 FIFO 的旧写入；
        /// 存储失败与调用方取消保持原异常传播，交由明确提交点决定如何呈现。
        /// </summary>
        public async UniTask SaveNow(CancellationToken cancellationToken = default)
        {
            MainThreadGuard.AssertMainThread(nameof(SettingsPersistenceSystem));
            ThrowIfDisposed();
            CancelPendingDelay();
            await SaveSnapshot(cancellationToken);
        }

        /// <summary>扩展包安装态的真源判定：包 Ready 且无缺失下载（EditorSimulate 下无下载量 = 初始化过即安装）。</summary>
        internal static bool IsExpansionInstalled(IAssetUtility assets)
            => assets.GetInitState(Game.Main.AssetPackages.OutpostExpansionPackage).CurrentValue == AssetInitState.Ready
               && assets.CreateAllDownloader(Game.Main.AssetPackages.OutpostExpansionPackage).TotalCount == 0;

        private async UniTask SaveAfterDelay(CancellationTokenSource owner)
        {
            try
            {
                await UniTask.Delay(SaveDelay, ignoreTimeScale: true, cancellationToken: owner.Token);

                // 延迟可能被新 owner 替换。只有仍占槽者有权提交；先摘槽再 await IO，让后续变更能
                // 建立自己的延迟，而不是取消一个已经进入 Storage FIFO 的物理写入。
                if (!ReferenceEquals(_pendingDelay, owner)) return;
                _pendingDelay = null;
                await SaveSnapshot(_context.CancellationToken);
            }
            catch (OperationCanceledException) when (
                owner.IsCancellationRequested ||
                _disposed ||
                (_context != null && _context.CancellationToken.IsCancellationRequested))
            {
                // 新变更替换延迟或根 Context 收口，都是预期控制流。
            }
            catch (Exception e)
            {
                Log.Error(
                    "设置自动保存失败；本会话内设置仍已生效，下次启动将回落旧值。",
                    e,
                    nameof(SettingsPersistenceSystem));
            }
            finally
            {
                if (ReferenceEquals(_pendingDelay, owner)) _pendingDelay = null;
                owner.Dispose();
            }
        }

        private async UniTask SaveSnapshot(CancellationToken cancellationToken)
        {
            var audio = this.GetUtility<IAudioUtility>();
            var prefs = this.GetModel<BattlePrefsModel>();
            var settings = new OutpostSettings
            {
                MasterVolume = audio.MasterVolume,
                MusicVolume = audio.GetGroupVolume(AudioGroups.Music),
                SfxVolume = audio.GetGroupVolume(AudioGroups.Sfx),
                Locale = this.GetUtility<ILocalizationUtility>().Locale.CurrentValue,
                ExpansionInstalled = IsExpansionInstalled(this.GetUtility<IAssetUtility>()),
                BattleBackend = (int)prefs.Backend.CurrentValue,
                WreckHeatmap = prefs.ShowWreckHeatmap.CurrentValue,
                ExpansionBgm = prefs.ExpansionBgm.CurrentValue,
            };
            await this.GetUtility<IStorageUtility>()
                .Save(StorageKeys.Settings, settings, cancellationToken);
        }

        /// <summary>随根 Context 释放，停止尚未开始的合并延迟。幂等。</summary>
        public void Dispose()
        {
            MainThreadGuard.AssertMainThread(nameof(SettingsPersistenceSystem));
            if (_disposed) return;
            _disposed = true;
            CancelPendingDelay();
        }

        private void CancelPendingDelay()
        {
            var owner = _pendingDelay;
            _pendingDelay = null;
            CancelOwnerSafely(owner, "设置保存延迟");
        }

        private static void CancelOwnerSafely(CancellationTokenSource owner, string label)
        {
            if (owner == null) return;
            try
            {
                owner.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 延迟 task 已完成并释放 owner，无需重复处理。
            }
            catch (Exception e)
            {
                // Cancel 已发出，只是某个注册回调失败；不能让它截断新 owner 发布或 Context 释放。
                Log.Warning($"{label}的取消回调抛出异常，已隔离：{e.Message}", nameof(SettingsPersistenceSystem));
            }
        }

        private static void LogUnexpectedObserverFailure(Exception exception)
        {
            if (exception is OperationCanceledException) return;
            Log.Error("设置保存任务观察器意外失败。", exception, nameof(SettingsPersistenceSystem));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(
                    nameof(SettingsPersistenceSystem),
                    "设置持久化 System 已随根 Context 释放，不能再请求保存。");
        }
    }
}
