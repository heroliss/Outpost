#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Game.Framework.Utility;

namespace Game.Outpost.Net
{
    /// <summary>
    /// 排行榜对端地址的唯一真源（仅 Editor / Development Build，随网络栈整体条件编译）：
    /// HTTP 基地址与 WS 地址一并持有。消费方（<c>OutpostNetSystem</c> 连 WS）只认本类，
    /// 不关心对端是进程内 <see cref="OutpostDevServer"/> 还是独立真后端（<c>Server~/OutpostServer</c>）——
    /// 二选一的装配分支在 <c>OutpostContext.InstallBindings</c>。
    /// </summary>
    /// <remarks>
    /// WS 地址显式持有、不从 HTTP 地址推导：两种对端的 WS 布局本就不同
    /// （dev server 是独立端口挂根路径，ASP.NET 真后端与 HTTP 同端口挂 <c>/ws</c>），
    /// 推导等于把某一侧的服务器布局硬编码进客户端。
    /// </remarks>
    public sealed class OutpostNetEndpoint : IUtility
    {
        /// <summary>HTTP 基地址（如 <c>http://127.0.0.1:5080</c>），注册 <c>IHttpUtility</c> 时作 baseUrl。</summary>
        public string HttpBaseUrl { get; }

        /// <summary>WebSocket 地址（ws:// 或 wss://，含路径），Connect 时用。</summary>
        public string WsUrl { get; }

        public OutpostNetEndpoint(string httpBaseUrl, string wsUrl)
        {
            HttpBaseUrl = httpBaseUrl;
            WsUrl = wsUrl;
        }
    }
}
#endif
