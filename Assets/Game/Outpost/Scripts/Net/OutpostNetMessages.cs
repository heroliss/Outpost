using Game.Framework.Network;

namespace Game.Outpost.Net
{
    /// <summary>
    /// Outpost 网络协议常量 + Protobuf 序列化器装配。
    /// <para><b>消息类型由 protoc 生成</b>（<c>Proto~/outpost_net.proto</c> → <c>Scripts/Net/Gen/</c>，
    /// 菜单 <c>SSFramework/Protobuf/生成全部</c>，路径配置在 Outpost 的 ProtoConfigProfile）：
    /// <see cref="SubmitScoreRequest"/> / <see cref="SubmitScoreResponse"/> / <see cref="LeaderboardEntry"/> /
    /// <see cref="LeaderboardResponse"/> / <see cref="NewRecordPushEvent"/> 都是 Google.Protobuf 的 <c>IMessage</c>。</para>
    /// <para>序列化经框架模块的 <see cref="GoogleProtobufNetworkSerializer"/> 接进 <c>IWebSocketEnvelopeSerializer</c> 接缝。
    /// 与内核手写 <c>ProtobufNetworkSerializer</c>、以及 ASP.NET 服务端 / 进程内 dev server 的 ProtoWire
    /// <b>wire 互通</b>（都产标准 protobuf 字节、字段号一致），可灰度换端。</para>
    /// </summary>
    public static class OutpostNet
    {
        /// <summary>网络栈是否可用：仅 Editor / Development Build（对端 = 进程内 dev server，
        /// 或 <c>OutpostContext</c> Inspector 指定的独立真后端）；正式包无网络栈，网络 UI 全部隐藏
        /// （正式接入策略见 Server~/README）。</summary>
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
        /// 建一个注册齐全部 Outpost 消息解析器的 Google.Protobuf 序列化器：RegisterFile 整文件注册——
        /// 往 .proto 加新消息重新生成即自动纳入，无需逐消息点名。客户端与进程内 dev server 各建一个实例，
        /// 等价共享同一份 .proto 契约。
        /// </summary>
        public static GoogleProtobufNetworkSerializer CreateSerializer() =>
            new GoogleProtobufNetworkSerializer().RegisterFile(OutpostNetReflection.Descriptor);
    }
}
