using System;
using System.Collections.Generic;
using Game.Framework.Event;
using Game.Framework.Network;

namespace Game.Outpost.Net
{
    /// <summary>
    /// 上传一局成绩（POST <c>api/score</c> 请求体）。等价 .proto：
    /// <c>message SubmitScoreRequest { string player = 1; int32 score = 2; int32 wave = 3; int32 kills = 4; }</c>
    /// </summary>
    [Serializable]
    public sealed class SubmitScoreRequest
    {
        public string Player;
        public int Score;
        public int Wave;
        public int Kills;
    }

    /// <summary>上传成绩的应答：该玩家当前在全服榜上的名次（1 起）。等价 .proto：<c>{ int32 rank = 1; }</c></summary>
    [Serializable]
    public sealed class SubmitScoreResponse
    {
        public int Rank;
    }

    /// <summary>榜上一条战绩。等价 .proto 同 <see cref="SubmitScoreRequest"/> 的四字段。</summary>
    [Serializable]
    public sealed class LeaderboardEntry
    {
        public string Player;
        public int Score;
        public int Wave;
        public int Kills;
    }

    /// <summary>排行榜（GET <c>api/leaderboard?count=N</c> 应答），按分数降序。等价 .proto：<c>{ repeated LeaderboardEntry entries = 1; }</c></summary>
    [Serializable]
    public sealed class LeaderboardResponse
    {
        public List<LeaderboardEntry> Entries = new();
    }

    /// <summary>
    /// 服务器广播「全服纪录被刷新」——WS 推送 type <see cref="OutpostNet.NewRecordPushType"/> 经
    /// <c>RegisterPush</c> 映射成本事件（§32 消息建模双轨之二），任意阶段 Toast 提示。
    /// 推送事件约定：<c>[Serializable] struct + 公共字段</c> 实现 <c>IEvent</c>。
    /// </summary>
    [Serializable]
    public struct NewRecordPushEvent : IEvent
    {
        public string Player;
        public int Score;
    }

    /// <summary>
    /// Outpost 网络协议常量 + Protobuf 编解码注册（客户端与进程内 dev server 共用同一份契约——
    /// 两侧各自 <see cref="CreateSerializer"/> 一个实例，等价于共享同一份 .proto）。
    /// </summary>
    public static class OutpostNet
    {
        /// <summary>网络栈是否可用：M4 阶段只有进程内 dev server（Editor / Development Build）；
        /// 正式包无对端，网络 UI 全部隐藏（M5 构建收口时再定正式环境策略）。</summary>
        public static bool Available =>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            true;
#else
            false;
#endif

        /// <summary>WS 推送 type：全服纪录刷新广播。</summary>
        public const string NewRecordPushType = "new_record";

        /// <summary>上传成绩端点（POST）。</summary>
        public const string ScorePath = "api/score";

        /// <summary>取排行榜端点（GET），count = 条数上限。</summary>
        public static string LeaderboardPath(int count) => $"api/leaderboard?count={count}";

        /// <summary>
        /// 建一个注册齐全部 Outpost 消息的 Protobuf 序列化器。
        /// 编解码是 per-message 显式函数（<c>ProtoWriter/ProtoReader</c>，字段号见各消息注释）——
        /// 消息就这几个，手写比引 protoc 工具链划算；换真后端时字段号即 .proto 契约。
        /// </summary>
        public static ProtobufNetworkSerializer CreateSerializer() => new ProtobufNetworkSerializer()
            .Register<SubmitScoreRequest>(
                (w, m) =>
                {
                    w.WriteString(1, m.Player);
                    w.WriteInt32(2, m.Score);
                    w.WriteInt32(3, m.Wave);
                    w.WriteInt32(4, m.Kills);
                },
                r =>
                {
                    var m = new SubmitScoreRequest { Player = "" };
                    while (r.TryReadTag(out int f, out int wt))
                        switch (f)
                        {
                            case 1: m.Player = r.ReadString(); break;
                            case 2: m.Score = r.ReadInt32(); break;
                            case 3: m.Wave = r.ReadInt32(); break;
                            case 4: m.Kills = r.ReadInt32(); break;
                            default: r.SkipField(wt); break;
                        }
                    return m;
                })
            .Register<SubmitScoreResponse>(
                (w, m) => w.WriteInt32(1, m.Rank),
                r =>
                {
                    var m = new SubmitScoreResponse();
                    while (r.TryReadTag(out int f, out int wt))
                        switch (f)
                        {
                            case 1: m.Rank = r.ReadInt32(); break;
                            default: r.SkipField(wt); break;
                        }
                    return m;
                })
            .Register<LeaderboardResponse>(
                (w, m) =>
                {
                    foreach (var e in m.Entries)
                    {
                        var item = new ProtoWriter();
                        WriteEntry(item, e);
                        w.WriteMessage(1, item.ToArray());
                    }
                },
                r =>
                {
                    var m = new LeaderboardResponse();
                    while (r.TryReadTag(out int f, out int wt))
                        switch (f)
                        {
                            case 1: m.Entries.Add(ReadEntry(r.ReadMessage())); break;
                            default: r.SkipField(wt); break;
                        }
                    return m;
                })
            .Register<NewRecordPushEvent>(
                (w, m) =>
                {
                    w.WriteString(1, m.Player);
                    w.WriteInt32(2, m.Score);
                },
                r =>
                {
                    var m = new NewRecordPushEvent { Player = "" };
                    while (r.TryReadTag(out int f, out int wt))
                        switch (f)
                        {
                            case 1: m.Player = r.ReadString(); break;
                            case 2: m.Score = r.ReadInt32(); break;
                            default: r.SkipField(wt); break;
                        }
                    return m;
                });

        private static void WriteEntry(ProtoWriter w, LeaderboardEntry e)
        {
            w.WriteString(1, e.Player);
            w.WriteInt32(2, e.Score);
            w.WriteInt32(3, e.Wave);
            w.WriteInt32(4, e.Kills);
        }

        private static LeaderboardEntry ReadEntry(ProtoReader r)
        {
            var e = new LeaderboardEntry { Player = "" };
            while (r.TryReadTag(out int f, out int wt))
                switch (f)
                {
                    case 1: e.Player = r.ReadString(); break;
                    case 2: e.Score = r.ReadInt32(); break;
                    case 3: e.Wave = r.ReadInt32(); break;
                    case 4: e.Kills = r.ReadInt32(); break;
                    default: r.SkipField(wt); break;
                }
            return e;
        }
    }
}
