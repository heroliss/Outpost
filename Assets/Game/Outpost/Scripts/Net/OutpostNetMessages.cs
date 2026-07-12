namespace Game.Outpost.Net
{
    /// <summary>
    /// Outpost 网络协议常量 + Protobuf 序列化器装配。
    /// <para><b>消息类型现由 protoc 生成</b>（<c>Proto~/outpost_net.proto</c> → <c>Scripts/Net/Gen/OutpostNet.cs</c>，
    /// 菜单 <c>SSFramework/Protobuf/生成 Outpost 协议代码</c>）：<see cref="SubmitScoreRequest"/> /
    /// <see cref="SubmitScoreResponse"/> / <see cref="LeaderboardEntry"/> / <see cref="LeaderboardResponse"/> /
    /// <see cref="NewRecordPushEvent"/> 都是 Google.Protobuf 的 <c>IMessage</c>（属性访问与旧手写 DTO 兼容，消费方零改动）。</para>
    /// <para>序列化经 <see cref="GoogleProtobufNetworkSerializer"/> 接进框架的 <c>IWebSocketEnvelopeSerializer</c> 接缝——
    /// 官方 protobuf 库的落地。与手写 <c>ProtobufNetworkSerializer</c>、以及 ASP.NET 服务端 / 进程内 dev server 的 ProtoWire
    /// <b>wire 互通</b>（都产标准 protobuf 字节、字段号一致），可灰度换端。</para>
    /// </summary>
    public static class OutpostNet
    {
        /// <summary>网络栈是否可用：M4 阶段只有进程内 dev server（Editor / Development Build）；
        /// 正式包无对端，网络 UI 全部隐藏（接真后端策略见 Server~/README）。</summary>
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
        /// 建一个注册齐全部 Outpost 消息解析器的 Google.Protobuf 序列化器。
        /// 每个消息传生成类的静态 <c>Parser</c>（免运行时反射，AOT 更稳）。客户端与进程内 dev server 各建一个实例，等价共享同一份 .proto。
        /// </summary>
        public static GoogleProtobufNetworkSerializer CreateSerializer() => new GoogleProtobufNetworkSerializer()
            .Register(SubmitScoreRequest.Parser)
            .Register(SubmitScoreResponse.Parser)
            .Register(LeaderboardEntry.Parser)
            .Register(LeaderboardResponse.Parser)
            .Register(NewRecordPushEvent.Parser);
    }
}
