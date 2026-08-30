using Game.Framework.Systems;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Localization;
using Game.Framework.Logging;
using Game.Framework.Network;
using Game.Framework.UI;
using Game.Outpost.Net;
#endif

namespace Game.Outpost.Systems
{
    /// <summary>
    /// 排行榜长连接的维持者：启动时连排行对端的 WS（地址取 <c>OutpostNetEndpoint</c>，不关心对端是
    /// 进程内 dev server 还是独立真后端）、消费「全服纪录刷新」推送并 Toast 提示，
    /// 断线自动退避重连（guide §25 样板的落地——重连是业务策略，框架只发 <c>WebSocketClosedEvent</c>）。
    /// 挂根 Context 的 Systems 节点、跨局常驻：新纪录广播在标题 / 战斗 / 结算任意阶段都该看得到。
    /// 请求-响应类操作（上传成绩 / 拉榜）不经过本系统——那是命令直达 <c>IHttpUtility</c> 的另一轨（§32）。
    /// </summary>
    /// <remarks>
    /// 网络栈仅在 Editor / Development Build 注册（对端装配见 <c>OutpostContext.InstallBindings</c>），
    /// 正式包本系统空转（<c>OutpostNet.Available</c> 为 false，实现整体条件编译）。
    /// </remarks>
    public sealed class OutpostNetSystem : MonoSystemBase
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const int MaxBackoffExponent = 5; // 退避 1,2,4,8,16,32s 封顶——对端在本进程或本地/近端，没必要更长

        private bool _maintaining; // 单飞门闩：启动连接与断线重连共用一条维持循环，不并发两条

        private void Start()
        {
            var ws = this.GetUtility<IWebSocketUtility>();

            // 全服纪录刷新 → 全局 Toast（含自己刷新的——即时正反馈）。文案带参数，用当前语言即时取。
            Bag.Subscribe<NewRecordPushEvent>(e =>
            {
                var loc = this.GetUtility<ILocalizationUtility>();
                this.GetUtility<IUIUtility>()
                    .ShowToast(loc.Get("net/new-record-toast", e.Player, e.Score), duration: 3.5f)
                    .Forget(LogUnexpectedToastFailure);
            });

            // 断线自动重连（guide §25 样板）：只处理非用户主动的断开；主动 Disconnect / Context 拆除不追连。
            Bag.Subscribe<WebSocketClosedEvent>(e =>
            {
                if (!e.ByUser) StartMaintainingConnection();
            });

            StartMaintainingConnection();
        }

        private void StartMaintainingConnection()
            => MaintainConnection().Forget(LogUnexpectedConnectionFailure);

        /// <summary>连到成功为止的维持循环：指数退避，随宿主销毁取消。已在维持中 / 已连上则直接返回（单飞）。</summary>
        private async UniTask MaintainConnection()
        {
            if (_maintaining) return;
            _maintaining = true;
            try
            {
                var ws = this.GetUtility<IWebSocketUtility>();
                string url = this.GetUtility<OutpostNetEndpoint>().WsUrl;
                for (int attempt = 0; ; attempt++)
                {
                    if (ws.State.CurrentValue != NetworkConnectionState.Disconnected)
                        return; // 已连上 / 在连——本循环没事可做

                    try
                    {
                        await ws.Connect(url, destroyCancellationToken);
                        return;
                    }
                    catch (NetworkException)
                    {
                        // 连接失败 → 退避后再试。进程内 dev server 常态首连即成，失败重试主要发生在
                        // 连独立真后端时（服务端未起 / 网络抖动）；生产级还会按错误类型区分可重试与否。
                    }

                    int delaySeconds = 1 << Math.Min(attempt, MaxBackoffExponent);
                    await UniTask.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken: destroyCancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 宿主销毁：静默收尾
            }
            finally
            {
                _maintaining = false;
            }
        }

        private static void LogUnexpectedConnectionFailure(Exception exception)
        {
            if (exception is OperationCanceledException) return;
            Log.Error("排行榜长连接维持循环发生未预期异常。", exception, nameof(OutpostNetSystem));
        }

        private static void LogUnexpectedToastFailure(Exception exception)
        {
            if (exception is OperationCanceledException) return;
            Log.Error("全服纪录 Toast 打开失败。", exception, nameof(OutpostNetSystem));
        }
#endif
    }
}
