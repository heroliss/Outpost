using System;
using System.Collections.Generic;
using System.IO;
using Game.Framework.Network;
using Google.Protobuf;

namespace Game.Outpost.Net
{
    /// <summary>
    /// 用<b>官方 Google.Protobuf</b> 实现框架的 <see cref="IWebSocketEnvelopeSerializer"/> 接缝：
    /// 消息编解码走 protoc 生成的 <see cref="IMessage"/> 类型（<c>ToByteArray</c> / <c>MessageParser</c>），
    /// WS envelope 用官方 <see cref="CodedOutputStream"/> 手写 <c>{string type=1; bytes payload=2;}</c> + 二进制帧。
    /// </summary>
    /// <remarks>
    /// <b>定位</b>：这是「把一个真 protobuf 库接进框架接缝」的落地样板——框架内核零第三方依赖（序列化是接缝），
    /// 真库实现住在业务侧（依赖 Google.Protobuf），构造注入替换掉手写的 <see cref="ProtobufNetworkSerializer"/>，
    /// 消费方（Http/WS utility + 命令 + 排行榜窗）<b>零改动</b>。本类是全泛型的（对任意 <see cref="IMessage"/> 生效），
    /// 与 Outpost 无耦合，另一个项目要用直接搬。<br/>
    /// <b>wire 兼容</b>：Google.Protobuf 与手写 <see cref="ProtoWriter"/> 都产出标准 protobuf wire 字节、envelope 字段号一致——
    /// 三端（Google.Protobuf 客户端 / ProtoWire 进程内 dev server / ProtoWire ASP.NET 服务端）互通，可灰度换端。<br/>
    /// <b>反序列化注册</b>：<see cref="Register{T}"/> 把每个消息类型的 <see cref="MessageParser{T}"/> 登记（生成类的静态 <c>Parser</c>）——
    /// 避免运行时反射取 Parser，对 IL2CPP/HybridCLR AOT 更稳。注册只在构造 / 服务注册期做，之后字典只读、任意线程可并发编解码。
    /// </remarks>
    public sealed class GoogleProtobufNetworkSerializer : IWebSocketEnvelopeSerializer
    {
        private const int EnvelopeTypeField = 1;
        private const int EnvelopePayloadField = 2;

        // 类型 → 解析器（基类 MessageParser，ParseFrom 返回 IMessage 再转 T）。
        private readonly Dictionary<Type, MessageParser> _parsers = new();

        public string ContentType => "application/x-protobuf";

        public bool UseBinaryFrames => true;

        /// <summary>注册一个消息类型的解析器（传生成类的静态 <c>T.Parser</c>）。返回自身可链式注册；重复注册抛。</summary>
        public GoogleProtobufNetworkSerializer Register<T>(MessageParser<T> parser) where T : IMessage<T>
        {
            if (parser == null) throw new ArgumentNullException(nameof(parser));
            if (_parsers.ContainsKey(typeof(T)))
                throw new InvalidOperationException($"[GoogleProtobufNetworkSerializer] {typeof(T).Name} 已注册解析器。");
            _parsers[typeof(T)] = parser;
            return this;
        }

        public byte[] Serialize<T>(T data)
        {
            if (data is not IMessage message)
                throw new InvalidOperationException($"[GoogleProtobufNetworkSerializer] {typeof(T).Name} 不是 Google.Protobuf IMessage——只能序列化 protoc 生成的消息。");
            return message.ToByteArray();
        }

        public T Deserialize<T>(byte[] bytes)
        {
            if (!_parsers.TryGetValue(typeof(T), out var parser))
                throw new InvalidOperationException(
                    $"[GoogleProtobufNetworkSerializer] {typeof(T).Name} 未注册解析器——服务注册处先 Register<{typeof(T).Name}>({typeof(T).Name}.Parser)。");
            return (T)parser.ParseFrom(bytes);
        }

        // envelope 用官方 CodedOutputStream 手写（不依赖某个生成的 Envelope 类型，保持本适配器全泛型可复用）。
        // proto3 语义：type 空 / payload 空则省略——与框架手写 ProtobufNetworkSerializer.EncodeEnvelope 逐字节一致。
        public byte[] EncodeEnvelope(string type, byte[] payload)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            if (!string.IsNullOrEmpty(type))
            {
                output.WriteTag(EnvelopeTypeField, WireFormat.WireType.LengthDelimited);
                output.WriteString(type);
            }
            if (payload is { Length: > 0 })
            {
                output.WriteTag(EnvelopePayloadField, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(payload));
            }
            output.Flush();
            return ms.ToArray();
        }

        public void DecodeEnvelope(byte[] frame, out string type, out byte[] payload)
        {
            type = null;
            payload = null;
            var input = new CodedInputStream(frame);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case EnvelopeTypeField: type = input.ReadString(); break;
                    case EnvelopePayloadField: payload = input.ReadBytes().ToByteArray(); break;
                    default: input.SkipLastField(); break;
                }
            }
        }
    }
}
