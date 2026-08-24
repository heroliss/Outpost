#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Game.Framework.Logging;
using Game.Framework.Network;
using Game.Framework.Utility;

namespace Game.Outpost.Net
{
    /// <summary>
    /// Outpost 的进程内排行榜 dev server（仅 Editor / Development Build，本文件整体条件编译）：
    /// HTTP 收分数 / 发 Top 榜（POST <c>api/score</c> / GET <c>api/leaderboard</c>），
    /// WS 在全服纪录被刷新时向所有连接广播 <c>new_record</c>。
    /// 全程 Protobuf 对讲（<see cref="OutpostNet.CreateSerializer"/> 同一契约、二进制 WS 帧）——
    /// 框架被验证的是客户端栈（<c>IHttpUtility</c>/<c>IWebSocketUtility</c> + <c>ProtobufNetworkSerializer</c>），
    /// 对端在哪个进程不影响验证效力；换独立真后端（<c>Server~/OutpostServer</c>）= 在 <c>OutpostContext</c>
    /// Inspector 填对端地址，本类不再构造、客户端栈零改动。
    /// </summary>
    /// <remarks>
    /// 结构照 <c>DemoGameServer</c>（ADR-0028 §8 的结论直接复用）：<b>HTTP 用 <see cref="HttpListener"/></b>
    /// （回环免管理员权限）；<b>WS 用 <see cref="TcpListener"/> + 手写 RFC6455</b>——Editor 的 Mono 运行时
    /// HttpListener 不支持 WS 升级。全部 IO 在后台线程 / 线程池（不碰 Unity API；Protobuf 编解码纯 C# 线程安全，
    /// 与主线程共享契约代码没有 JsonUtility 那种线程亲和问题）。构造即启动，Dispose（随根 Context 拆除）即停。
    /// 榜单在内存（进程内 dev server 随游戏生灭，持久榜等真后端）；每玩家只留最好成绩。
    /// </remarks>
    public sealed class OutpostDevServer : IUtility, IDisposable
    {
        private const string WsMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"; // RFC6455 握手固定常量

        private readonly IWebSocketEnvelopeSerializer _proto = OutpostNet.CreateSerializer();
        private readonly HttpListener _http;
        private readonly TcpListener _wsListener;
        private readonly CancellationTokenSource _cts = new();

        // 活跃 WS 连接（lock(_wsClients) 保护）：广播遍历写、断开移除、Dispose 主动关。
        // writeLock 每连接一把——广播（后台任意线程）与该连接的 close 应答写同一 socket 需串行。
        private readonly List<WsClient> _wsClients = new();

        // 榜单（lock(_board) 保护）：分数降序、每玩家一条（POST 并入时只保留更好成绩）。
        // 预置几条"驻军"成绩让榜看起来有人气，也给玩家一个追赶目标。
        private readonly List<LeaderboardEntry> _board = new()
        {
            new LeaderboardEntry { Player = "Vanguard", Score = 32000, Wave = 96, Kills = 21400 },
            new LeaderboardEntry { Player = "Bastion", Score = 18500, Wave = 74, Kills = 12800 },
            new LeaderboardEntry { Player = "Aegis", Score = 9400, Wave = 52, Kills = 6900 },
            new LeaderboardEntry { Player = "Sentry", Score = 4200, Wave = 33, Kills = 3100 },
            new LeaderboardEntry { Player = "Nova", Score = 1500, Wave = 18, Kills = 1200 },
        };

        private volatile bool _running;
        private bool _disposed;

        private sealed class WsClient
        {
            public TcpClient Tcp;
            public NetworkStream Stream;
            public object WriteLock;
        }

        /// <summary>HTTP 基地址（已含实际端口），注册 <c>IHttpUtility</c> 时作 baseUrl。</summary>
        public string HttpBaseUrl { get; }

        /// <summary>WebSocket 地址（ws://，已含实际端口），Connect 时用。</summary>
        public string WsUrl { get; }

        public OutpostDevServer()
        {
            HttpListener http = null;
            TcpListener wsListener = null;
            try
            {
                int httpPort = FindFreeHttpPort(out http);
                wsListener = new TcpListener(IPAddress.Loopback, 0); // 端口 0 = OS 分配空闲端口
                wsListener.Start();

                _http = http;
                _wsListener = wsListener;
                HttpBaseUrl = $"http://127.0.0.1:{httpPort}";
                WsUrl = $"ws://127.0.0.1:{((IPEndPoint)wsListener.LocalEndpoint).Port}/";
                _running = true;
                _ = HttpAcceptLoop();
                _ = WsAcceptLoop();
            }
            catch
            {
                try { wsListener?.Stop(); } catch { }
                try { http?.Close(); } catch { }
                try { _cts.Cancel(); } catch { }
                try { _cts.Dispose(); } catch { }
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            bool wasRunning = _running;
            _running = false;
            if (wasRunning)
            {
                try { _cts.Cancel(); }
                catch (Exception e) { Log.Error("Cancellation callback threw while stopping the Outpost dev server; listener cleanup will continue.", e, "OutpostDevServer"); }
            }
            try { if (_http.IsListening) _http.Stop(); }
            catch (Exception e) { Log.Error("HTTP listener threw while stopping; remaining dev-server cleanup will continue.", e, "OutpostDevServer"); }
            try { _http.Close(); } catch { /* 已停 */ }
            try { _wsListener.Stop(); }
            catch (Exception e) { Log.Error("WebSocket listener threw while stopping; active clients will still be closed.", e, "OutpostDevServer"); }

            // 停监听只挡新连接；已建立连接的读循环阻塞在 ReadAsync（Mono 的 NetworkStream 不响应 ct），主动关 socket 让它退出。
            lock (_wsClients)
            {
                foreach (var client in _wsClients)
                    try { client.Tcp.Close(); } catch { /* 已断 */ }
                _wsClients.Clear();
            }
            try { _cts.Dispose(); } catch { }
        }

        // ── HTTP：排行榜读写 ─────────────────────────────────────────────────

        private static int FindFreeHttpPort(out HttpListener listener)
        {
            // 18500 起扫描：demo 服务器占 18400 段、框架测试服务器占 18200 段，错开避免共存冲突。
            Exception lastError = null;
            for (int port = 18500; port < 18560; port++)
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.Start();
                    listener = candidate;
                    return port;
                }
                catch (HttpListenerException e)
                {
                    lastError = e;
                    candidate.Close();
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    lastError = e;
                    candidate.Close(); // Unity Mono 的 HttpListener.Start 可能直接透出该异常
                }
                catch
                {
                    candidate.Close();
                    throw;
                }
            }
            listener = null;
            throw new InvalidOperationException("18500-18559 无可用端口，无法启动 Outpost dev server。", lastError);
        }

        private async Task HttpAcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); }
                catch { break; } // Stop/Close 后 GetContextAsync 抛出 = 正常退出
                _ = Task.Run(() => HandleHttp(ctx));
            }
        }

        private void HandleHttp(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                switch (req.Url.AbsolutePath)
                {
                    case "/api/score" when req.HttpMethod == "POST":
                        HandleSubmitScore(ctx);
                        break;

                    case "/api/leaderboard" when req.HttpMethod == "GET":
                    {
                        int count = int.TryParse(req.QueryString["count"], out int n) ? Math.Clamp(n, 1, 100) : 10;
                        var resp = new LeaderboardResponse();
                        lock (_board)
                            resp.Entries.Add(_board.Take(count)); // RepeatedField.Add(IEnumerable)——生成类型无 AddRange
                        WriteProto(ctx.Response, 200, _proto.Serialize(resp));
                        break;
                    }

                    default:
                        WriteError(ctx.Response, 404, "没有这个路由");
                        break;
                }
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* 客户端已断（取消 / 超时的常态） */ }
            }
        }

        private void HandleSubmitScore(HttpListenerContext ctx)
        {
            SubmitScoreRequest submit;
            try
            {
                using var ms = new MemoryStream();
                ctx.Request.InputStream.CopyTo(ms);
                submit = _proto.Deserialize<SubmitScoreRequest>(ms.ToArray());
            }
            catch
            {
                WriteError(ctx.Response, 400, "请求体不是合法的 SubmitScoreRequest");
                return;
            }
            if (string.IsNullOrEmpty(submit.Player))
            {
                WriteError(ctx.Response, 400, "缺少 player");
                return;
            }

            int rank;
            bool newTop;
            lock (_board)
            {
                int previousTop = _board.Count > 0 ? _board[0].Score : 0;

                // 每玩家一条：只并入更好成绩（榜答复的名次 = 该玩家最好成绩的名次，与榜显示自洽）
                var mine = _board.FirstOrDefault(e => e.Player == submit.Player);
                if (mine == null)
                {
                    mine = new LeaderboardEntry { Player = submit.Player, Score = -1 };
                    _board.Add(mine);
                }
                if (submit.Score > mine.Score)
                {
                    mine.Score = submit.Score;
                    mine.Wave = submit.Wave;
                    mine.Kills = submit.Kills;
                }
                _board.Sort((a, b) => b.Score.CompareTo(a.Score));

                rank = _board.IndexOf(mine) + 1;
                newTop = rank == 1 && mine.Score > previousTop;
            }

            WriteProto(ctx.Response, 200, _proto.Serialize(new SubmitScoreResponse { Rank = rank }));

            // 全服纪录被刷新：广播给所有 WS 连接（含提交者本人——本人看到 Toast 也是正反馈）
            if (newTop)
                Broadcast(OutpostNet.NewRecordPushType,
                    _proto.Serialize(new NewRecordPushEvent { Player = submit.Player, Score = submit.Score }));
        }

        private static void WriteProto(HttpListenerResponse resp, int status, byte[] body)
        {
            resp.StatusCode = status;
            resp.ContentType = "application/x-protobuf";
            resp.ContentLength64 = body.Length;
            resp.OutputStream.Write(body, 0, body.Length);
            resp.Close();
        }

        // 错误响应用 UTF-8 文本——客户端侧只进 NetworkException.ResponseBody 供人排查，不参与反序列化。
        private static void WriteError(HttpListenerResponse resp, int status, string message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            resp.StatusCode = status;
            resp.ContentType = "text/plain; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.Close();
        }

        // ── WebSocket：新纪录广播（TcpListener + 手写 RFC6455，二进制帧）───────

        private async Task WsAcceptLoop()
        {
            while (_running)
            {
                TcpClient tcp;
                try { tcp = await _wsListener.AcceptTcpClientAsync(); }
                catch { break; } // Stop 后 AcceptTcpClientAsync 抛出 = 正常退出
                _ = Task.Run(() => HandleWsClient(tcp));
            }
        }

        private async Task HandleWsClient(TcpClient tcp)
        {
            var client = new WsClient { Tcp = tcp, WriteLock = new object() };
            lock (_wsClients)
            {
                if (!_running) { tcp.Close(); return; } // Dispose 与 Accept 竞态：晚到的连接直接关
                _wsClients.Add(client);
            }
            try
            {
                using (tcp)
                {
                    var stream = tcp.GetStream();
                    client.Stream = stream;
                    if (!await DoHandshake(stream)) return;

                    // 读循环：本服务器不消费客户端消息（推送单向），只应答 close 帧完成关闭握手。
                    while (!_cts.IsCancellationRequested)
                    {
                        (int opcode, byte[] _) = await ReadFrame(stream, _cts.Token);
                        if (opcode == -1) break; // 连接断开
                        if (opcode == 0x8)
                        {
                            TryWriteFrame(stream, client.WriteLock, new byte[] { 0x88, 0x00 }); // close ack
                            break;
                        }
                        // 其余帧（文本/二进制/ping）忽略——协议里客户端没有要发的消息
                    }
                }
            }
            catch { /* 连接异常断开：静默收尾 */ }
            finally
            {
                lock (_wsClients) _wsClients.Remove(client);
            }
        }

        /// <summary>向所有活跃连接发一条 envelope 推送（Protobuf envelope + 二进制帧）。后台线程调用。</summary>
        private void Broadcast(string type, byte[] payload)
        {
            byte[] frame = EncodeBinaryFrame(_proto.EncodeEnvelope(type, payload));
            WsClient[] clients;
            lock (_wsClients) clients = _wsClients.ToArray();
            foreach (var c in clients)
                if (c.Stream != null)
                    TryWriteFrame(c.Stream, c.WriteLock, frame);
        }

        // 服务器→客户端帧不带掩码；opcode 0x2 = 二进制（Protobuf envelope 不是合法 UTF-8，不能用文本帧）。
        private static byte[] EncodeBinaryFrame(byte[] payload)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x82); // FIN + binary opcode
            if (payload.Length < 126)
            {
                ms.WriteByte((byte)payload.Length);
            }
            else // 推送消息不会超过 64KB，只需 126（2 字节长度）分支
            {
                ms.WriteByte(126);
                ms.WriteByte((byte)((payload.Length >> 8) & 0xFF));
                ms.WriteByte((byte)(payload.Length & 0xFF));
            }
            ms.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        private static void TryWriteFrame(NetworkStream stream, object writeLock, byte[] frame)
        {
            try
            {
                lock (writeLock)
                    stream.Write(frame, 0, frame.Length);
            }
            catch { /* 连接已断——读循环随后退出并移除该连接 */ }
        }

        // RFC6455 握手：读 HTTP 升级请求头 → 算 Sec-WebSocket-Accept → 回 101。
        private static async Task<bool> DoHandshake(NetworkStream stream)
        {
            string headers = await ReadHttpHeaders(stream);
            string key = ExtractHeader(headers, "Sec-WebSocket-Key");
            if (string.IsNullOrEmpty(key)) return false;

            string accept;
            using (var sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.UTF8.GetBytes(key + WsMagicGuid)));

            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
            await stream.WriteAsync(response, 0, response.Length);
            return true;
        }

        private static async Task<string> ReadHttpHeaders(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            // 逐字节读到空行（\r\n\r\n）为止；结尾判断只看尾部 4 字符，避免每字节整串重建
            while (sb.Length < 4096)
            {
                int read = await stream.ReadAsync(buf, 0, 1);
                if (read == 0) break;
                sb.Append((char)buf[0]);
                if (sb.Length >= 4 &&
                    sb[sb.Length - 4] == '\r' && sb[sb.Length - 3] == '\n' &&
                    sb[sb.Length - 2] == '\r' && sb[sb.Length - 1] == '\n')
                    break;
            }
            return sb.ToString();
        }

        private static string ExtractHeader(string headers, string name)
        {
            foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(colon + 1).Trim();
            }
            return null;
        }

        // 读一个客户端帧（客户端帧必然带掩码）。返回 (opcode, 解掩码后的 payload)；断开返回 (-1, null)。
        private static async Task<(int opcode, byte[] payload)> ReadFrame(NetworkStream stream, CancellationToken ct)
        {
            byte[] head = await ReadExact(stream, 2, ct);
            if (head == null) return (-1, null);

            int opcode = head[0] & 0x0F;
            bool masked = (head[1] & 0x80) != 0;
            long len = head[1] & 0x7F;
            if (len == 126)
            {
                byte[] ext = await ReadExact(stream, 2, ct);
                if (ext == null) return (-1, null);
                len = (ext[0] << 8) | ext[1];
            }
            else if (len == 127)
            {
                byte[] ext = await ReadExact(stream, 8, ct);
                if (ext == null) return (-1, null);
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
            }

            byte[] mask = masked ? await ReadExact(stream, 4, ct) : null;
            byte[] payload = await ReadExact(stream, (int)len, ct);
            if (payload == null) return (-1, null);
            if (masked)
                for (int i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i % 4];
            return (opcode, payload);
        }

        // 从流读满 count 字节；流关闭（读到 0）返回 null。
        private static async Task<byte[]> ReadExact(NetworkStream stream, int count, CancellationToken ct)
        {
            var buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int read = await stream.ReadAsync(buf, off, count - off, ct);
                if (read == 0) return null;
                off += read;
            }
            return buf;
        }
    }
}
#endif
