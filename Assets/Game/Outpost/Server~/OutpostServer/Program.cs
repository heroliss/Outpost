using System.Net.WebSockets;
using Outpost.Server;
using Outpost.Server.Leaderboard;
using Outpost.Server.Protocol;

// Outpost 排行榜服务端（生产化参考实现）。进程内 dev server 的三个端点原样搬到 ASP.NET Core：
//   POST /api/score          上传成绩（protobuf 体）→ 返回名次（protobuf）；刷新纪录则 WS 广播
//   GET  /api/leaderboard    取分数降序 Top N（protobuf）
//   GET  /ws                 WebSocket 长连接，接收「全服纪录刷新」推送（二进制 envelope 帧）
// wire 格式与客户端逐字节一致（同一套 ProtoWire + envelope 契约），客户端切后端只改 baseUrl / ws url。

var builder = WebApplication.CreateBuilder(args);

// SQLite 连接串：配置优先（Docker 里指向挂卷路径），否则落工作目录的 outpost.db。
string connectionString = builder.Configuration.GetConnectionString("Leaderboard")
    ?? "Data Source=outpost.db";
builder.Services.AddSingleton(new LeaderboardStore(connectionString));
builder.Services.AddSingleton<PushHub>();

var app = builder.Build();
app.UseWebSockets();

var store = app.Services.GetRequiredService<LeaderboardStore>();
var hub = app.Services.GetRequiredService<PushHub>();
var appStopping = app.Lifetime.ApplicationStopping;

// ── POST /api/score ──
app.MapPost("/api/score", async (HttpContext ctx) =>
{
    byte[] body = await ReadBodyAsync(ctx);

    SubmitScoreRequest req;
    try { req = OutpostProtocol.ReadSubmitScoreRequest(body); }
    catch { return Results.Text("请求体不是合法的 SubmitScoreRequest", "text/plain", statusCode: 400); }

    if (string.IsNullOrEmpty(req.Player))
        return Results.Text("缺少 player", "text/plain", statusCode: 400);

    var (rank, newTop) = store.Submit(req);

    // 全服纪录被刷新：广播给所有 WS 连接（含提交者本人——本人看到 Toast 也是正反馈）。
    if (newTop)
    {
        byte[] frame = OutpostProtocol.EncodeEnvelope(
            OutpostProtocol.NewRecordPushType,
            OutpostProtocol.WriteNewRecordPush(new NewRecordPush(req.Player, req.Score)));
        await hub.BroadcastAsync(frame, appStopping);
    }

    return ProtoResult(OutpostProtocol.WriteSubmitScoreResponse(new SubmitScoreResponse(rank)));
});

// ── GET /api/leaderboard?count=N ──
app.MapGet("/api/leaderboard", (int? count) =>
{
    var top = store.Top(count ?? 10);
    return ProtoResult(OutpostProtocol.WriteLeaderboardResponse(new LeaderboardResponse(top)));
});

// ── GET /ws（WebSocket 长连接：接收新纪录推送）──
app.MapGet("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.HandleAsync(socket, appStopping);
});

// 健康检查（云平台 liveness / readiness 探针用）。
app.MapGet("/health", () => Results.Ok("ok"));

app.Run();

// protobuf 体响应：application/x-protobuf + 原始字节（与客户端 ContentType 一致）。
static IResult ProtoResult(byte[] body) => Results.Bytes(body, OutpostProtocol.ContentType);

// 把请求体读成 byte[]（protobuf 是二进制、长度已知，直接全量读）。
static async Task<byte[]> ReadBodyAsync(HttpContext ctx)
{
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    return ms.ToArray();
}
