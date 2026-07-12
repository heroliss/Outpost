using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Outpost.Server;

/// <summary>
/// WebSocket 推送中枢：维护活跃连接集合，向所有连接广播<b>二进制帧</b>（envelope 是 protobuf、非 UTF-8，不能走文本帧）。
/// 单例服务——提交成绩的 HTTP 请求上下文经它把「全服纪录刷新」广播给所有长连接（含提交者本人）。
/// </summary>
/// <remarks>
/// 用 Kestrel 原生 <see cref="WebSocket"/>：握手 / 掩码 / 分帧全由框架处理，进程内 dev server 那套手写 RFC6455 整套扔掉。
/// 并发：<see cref="ConcurrentDictionary{TKey,TValue}"/> 存连接；每连接一把 <see cref="SemaphoreSlim"/> 串行化写
/// （<see cref="WebSocket.SendAsync"/> 不允许并发写同一 socket）。广播对单连接的写失败即摘除该连接（读循环随后也会退出）。
/// </remarks>
public sealed class PushHub
{
    private sealed record Connection(WebSocket Socket, SemaphoreSlim WriteLock);

    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    public int Count => _connections.Count;

    /// <summary>接管一个已升级的 WebSocket：登记 → 读循环维持到断开（本服务器单向推送，只吞客户端帧 / 应答 close）→ 摘除。</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken appStopping)
    {
        var id = Guid.NewGuid();
        var conn = new Connection(socket, new SemaphoreSlim(1, 1));
        _connections[id] = conn;
        try
        {
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !appStopping.IsCancellationRequested)
            {
                // 单向推送协议：客户端没有要发的业务消息，收到 Close 就走正常关闭握手，其余帧忽略。
                var result = await socket.ReceiveAsync(buffer, appStopping);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, appStopping);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* 进程关停：静默收尾 */ }
        catch (WebSocketException) { /* 连接异常断开：静默收尾 */ }
        finally
        {
            _connections.TryRemove(id, out _);
            conn.WriteLock.Dispose();
        }
    }

    /// <summary>向所有活跃连接广播一帧二进制消息。写失败的连接被摘除（其读循环随后退出）。</summary>
    public async Task BroadcastAsync(byte[] frame, CancellationToken ct)
    {
        foreach (var (id, conn) in _connections)
        {
            if (conn.Socket.State != WebSocketState.Open)
            {
                _connections.TryRemove(id, out _);
                continue;
            }
            await conn.WriteLock.WaitAsync(ct);
            try
            {
                await conn.Socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
            catch
            {
                _connections.TryRemove(id, out _);
            }
            finally
            {
                conn.WriteLock.Release();
            }
        }
    }
}
