# Outpost 排行榜服务端（生产化参考实现）

进程内 dev server（`Assets/Game/Outpost/Scripts/Net/OutpostDevServer.cs`）的**独立部署版**——把「验证客户端网络栈」用的临时对端，换成一个能上任意云的真服务端。

**定位**：这是 Outpost 切片的一部分、**不进框架**（框架被验证的是客户端栈 `IHttpUtility`/`IWebSocketUtility`，对端在哪个进程/机器不影响验证效力）。切片抽成独立 package 时，本目录随 Outpost 一起走。

## 与 dev server 的差别

| 维度 | 进程内 dev server | 本服务端 |
|---|---|---|
| 运行位置 | Unity 进程内（仅 Editor / Development Build） | 独立 .NET 进程，Docker 上任意云 |
| WebSocket | 手写 RFC6455（握手/掩码/分帧） | **Kestrel 原生**（`app.UseWebSockets()`，全交给框架） |
| HTTP | `HttpListener` | ASP.NET Core minimal API |
| 榜单 | 内存（随游戏生灭） | **SQLite 持久化**（进程重启不丢） |
| wire 格式 | ProtoWire（真 protobuf） | **同一套 ProtoWire**（逐字节一致，见下） |

**wire 兼容是设计前提**：服务端 `Protocol/ProtoWire.cs` 是框架 `Core/Network/ProtoWire.cs` 的逐字节移植，`Protocol/OutpostProtocol.cs` 的字段号与客户端 `Scripts/Net/OutpostNetMessages.cs` 一致。两端各自编解码同一份标准 protobuf wire，字段号对上即互通——客户端切到本服务端**零代码改动**。

## 契约（等价一份 .proto）

```proto
message SubmitScoreRequest  { string player = 1; int32 score = 2; int32 wave = 3; int32 kills = 4; }
message SubmitScoreResponse { int32 rank = 1; }
message LeaderboardEntry    { string player = 1; int32 score = 2; int32 wave = 3; int32 kills = 4; }
message LeaderboardResponse { repeated LeaderboardEntry entries = 1; }
message NewRecordPush       { string player = 1; int32 score = 2; }
message Envelope            { string type = 1; bytes payload = 2; }   // WS 推送外壳
```

| 端点 | 方法 | 请求 | 应答 |
|---|---|---|---|
| `/api/score` | POST | `SubmitScoreRequest`（protobuf 体） | `SubmitScoreResponse`（名次；刷新纪录时 WS 广播） |
| `/api/leaderboard?count=N` | GET | — | `LeaderboardResponse`（分数降序 Top N） |
| `/ws` | GET（Upgrade） | WebSocket 长连接 | 收 `Envelope{type="new_record", payload=NewRecordPush}` 二进制帧 |
| `/health` | GET | — | `"ok"`（云平台探针） |

## 本地运行

```bash
cd OutpostServer
dotnet run                      # 默认 http://localhost:5xxx（控制台打印实际端口）
# 或指定端口：
ASPNETCORE_URLS=http://127.0.0.1:8080 dotnet run
```

SQLite 库落工作目录 `outpost.db`（首次启动 seed 5 条驻军成绩）。改连接串：
`ConnectionStrings__Leaderboard="Data Source=/some/path/outpost.db"`（环境变量或 appsettings）。

## Docker 部署

```bash
cd OutpostServer
docker build -t outpost-server .
docker run -d -p 8080:8080 -v outpost-data:/data outpost-server
#   -v 把 SQLite 库挂命名卷，容器重建不丢榜（Dockerfile 已把连接串指向 /data/outpost.db）
```

镜像监听 `8080`，HTTP 与 WebSocket 同端口（`/ws`）。上云（Fly.io / Railway / Render / 任意 K8s）：
推镜像 + 挂一个持久卷到 `/data` 即可；健康检查配 `/health`。部署平台细节见 `D:\KnowledgeBase`（01/02 部署知识）。

## 客户端接线（切到本服务端）

`OutpostContext` 的 Inspector 直接切：`_remoteHttpBaseUrl` / `_remoteWsUrl` 填本服务端地址（如 `http://127.0.0.1:5080` / `ws://127.0.0.1:5080/ws`）即直连、不再起进程内 dev server；两个都留空回默认。地址收口在 `OutpostNetEndpoint`（唯一真源），业务调用代码零改动——这正是双接缝（传输/序列化构造注入）的价值。已实测（2026-07-13）：Unity 客户端栈对本服务端 POST 名次 / GET 榜单 / WS `new_record` 推送 → Toast 全链路互通。正式包网络策略当前是「保持隐藏」（`OutpostNet.Available` 门控），云端部署后再开。

## 序列化实现：两端可各自演进（灰度已实测）

客户端已换官方 Google.Protobuf（框架模块 `Game.Framework.Network.Proto`，契约在 `Proto~/outpost_net.proto`）；本服务端仍用手写 ProtoWire（消息就这几个，划算）。两端产同一套标准 protobuf wire、字段号一致，联调实测互通——**「换实现线上格式不变、可灰度」不再是推断而是现状**。服务端消息膨胀到需要 map/oneof/有符号/浮点时，同样把上面的 .proto 喂 protoc 换 Google.Protobuf 即可。
