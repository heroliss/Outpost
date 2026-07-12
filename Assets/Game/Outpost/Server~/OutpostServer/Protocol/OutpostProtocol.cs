namespace Outpost.Server.Protocol;

// ── 消息 DTO + 编解码 + envelope ──
// 字段号是客户端与服务端共同的契约（等价一份 .proto）；改字段号 = 破坏兼容，只增不改。
// 客户端侧对应：Assets/Game/Outpost/Scripts/Net/OutpostNetMessages.cs（同字段号）。

/// <summary>上传一局成绩（POST api/score 请求体）。.proto：<c>{ string player=1; int32 score=2; int32 wave=3; int32 kills=4; }</c></summary>
public sealed record SubmitScoreRequest(string Player, int Score, int Wave, int Kills);

/// <summary>上传成绩应答：该玩家当前全服名次（1 起）。.proto：<c>{ int32 rank=1; }</c></summary>
public sealed record SubmitScoreResponse(int Rank);

/// <summary>榜上一条战绩（同 SubmitScoreRequest 四字段）。</summary>
public sealed record LeaderboardEntry(string Player, int Score, int Wave, int Kills);

/// <summary>排行榜（GET api/leaderboard?count=N 应答），分数降序。.proto：<c>{ repeated LeaderboardEntry entries=1; }</c></summary>
public sealed record LeaderboardResponse(IReadOnlyList<LeaderboardEntry> Entries);

/// <summary>「全服纪录被刷新」广播（WS 推送 type <see cref="OutpostProtocol.NewRecordPushType"/>）。.proto：<c>{ string player=1; int32 score=2; }</c></summary>
public sealed record NewRecordPush(string Player, int Score);

/// <summary>
/// Outpost 协议常量 + protobuf 编解码。与客户端 <c>ProtobufNetworkSerializer</c> 走同一 wire 格式：
/// 消息逐字段 protobuf、WS envelope 是 <c>{string type=1; bytes payload=2;}</c> + 二进制帧。
/// 无 protoc、无反射——消息就这几个，手写编解码，字段号即契约。
/// </summary>
public static class OutpostProtocol
{
    /// <summary>WS 推送 type：全服纪录刷新广播。</summary>
    public const string NewRecordPushType = "new_record";

    /// <summary>HTTP 体的 protobuf content-type（与客户端 <c>ContentType</c> 一致）。</summary>
    public const string ContentType = "application/x-protobuf";

    private const int EnvelopeTypeField = 1;
    private const int EnvelopePayloadField = 2;

    // ── envelope（WS 推送）──

    /// <summary>编码 WS 推送 envelope：<c>{type, payload}</c> → protobuf 字节（客户端 DecodeEnvelope 逐字节对上）。</summary>
    public static byte[] EncodeEnvelope(string type, byte[] payload)
    {
        var w = new ProtoWriter();
        w.WriteString(EnvelopeTypeField, type);
        w.WriteBytes(EnvelopePayloadField, payload);
        return w.ToArray();
    }

    // ── 请求（客户端 → 服务端）──

    public static SubmitScoreRequest ReadSubmitScoreRequest(byte[] bytes)
    {
        string player = ""; int score = 0, wave = 0, kills = 0;
        var r = new ProtoReader(bytes);
        while (r.TryReadTag(out int f, out int wt))
            switch (f)
            {
                case 1: player = r.ReadString(); break;
                case 2: score = r.ReadInt32(); break;
                case 3: wave = r.ReadInt32(); break;
                case 4: kills = r.ReadInt32(); break;
                default: r.SkipField(wt); break;
            }
        return new SubmitScoreRequest(player, score, wave, kills);
    }

    // ── 应答（服务端 → 客户端）──

    public static byte[] WriteSubmitScoreResponse(SubmitScoreResponse m)
    {
        var w = new ProtoWriter();
        w.WriteInt32(1, m.Rank);
        return w.ToArray();
    }

    public static byte[] WriteLeaderboardResponse(LeaderboardResponse m)
    {
        var w = new ProtoWriter();
        foreach (var e in m.Entries)
            w.WriteMessage(1, WriteEntry(e));
        return w.ToArray();
    }

    public static byte[] WriteNewRecordPush(NewRecordPush m)
    {
        var w = new ProtoWriter();
        w.WriteString(1, m.Player);
        w.WriteInt32(2, m.Score);
        return w.ToArray();
    }

    private static byte[] WriteEntry(LeaderboardEntry e)
    {
        var w = new ProtoWriter();
        w.WriteString(1, e.Player);
        w.WriteInt32(2, e.Score);
        w.WriteInt32(3, e.Wave);
        w.WriteInt32(4, e.Kills);
        return w.ToArray();
    }
}
