using System.Text;

namespace Outpost.Server.Protocol;

// ── 与框架 Assets/Game/Framework/Core/Network/ProtoWire.cs 逐字节等价的移植 ──
// 服务端与客户端<b>共享同一份 wire 格式</b>是对讲的前提，所以这里刻意复制而非引用：
// 客户端在 Unity 程序集里、服务端在独立 .NET 工程里，无法共享程序集，但两边都是纯 C#、
// 都产出标准 protobuf wire 字节（字段号一致即互通），复制成本低于强行抽共享库。
// 字节与标准 protobuf 完全互通——将来任一端换 Google.Protobuf / protobuf-net 线上格式不变。

/// <summary>最小 Protobuf wire 写入器（varint + length-delimited，proto3 语义：默认值省略、仅非负 int32）。</summary>
public sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public byte[] ToArray() => _ms.ToArray();

    /// <summary>写非负 int32 字段（varint）；0 按 proto3 语义省略；负数抛（本 wire 层不做 zigzag）。</summary>
    public void WriteInt32(int fieldNumber, int value)
    {
        if (value == 0) return;
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "ProtoWriter 只支持非负 int32（无 zigzag）。");
        WriteTag(fieldNumber, 0);
        WriteVarint((uint)value);
    }

    /// <summary>写 string 字段（UTF-8 length-delimited）；null/空串省略。</summary>
    public void WriteString(int fieldNumber, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        WriteLengthDelimited(fieldNumber, Encoding.UTF8.GetBytes(value));
    }

    /// <summary>写 bytes 字段；null/空省略。</summary>
    public void WriteBytes(int fieldNumber, byte[]? value)
    {
        if (value == null || value.Length == 0) return;
        WriteLengthDelimited(fieldNumber, value);
    }

    /// <summary>写嵌套消息 / repeated 元素（length-delimited；空消息也写 tag——repeated 计数靠 tag 出现次数）。</summary>
    public void WriteMessage(int fieldNumber, byte[] encodedMessage)
        => WriteLengthDelimited(fieldNumber, encodedMessage ?? Array.Empty<byte>());

    private void WriteLengthDelimited(int fieldNumber, byte[] bytes)
    {
        WriteTag(fieldNumber, 2);
        WriteVarint((uint)bytes.Length);
        _ms.Write(bytes, 0, bytes.Length);
    }

    private void WriteTag(int fieldNumber, int wireType) => WriteVarint((uint)((fieldNumber << 3) | wireType));

    private void WriteVarint(uint value)
    {
        while (value >= 0x80)
        {
            _ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _ms.WriteByte((byte)value);
    }
}

/// <summary>最小 Protobuf wire 读取器（配对 <see cref="ProtoWriter"/>）；损坏/越界抛 <see cref="InvalidDataException"/>。</summary>
public sealed class ProtoReader
{
    private readonly byte[] _buf;
    private readonly int _end;
    private int _pos;

    public ProtoReader(byte[] buffer) : this(buffer, 0, buffer.Length) { }

    public ProtoReader(byte[] buffer, int offset, int length)
    {
        _buf = buffer;
        _pos = offset;
        _end = offset + length;
    }

    public bool TryReadTag(out int fieldNumber, out int wireType)
    {
        if (_pos >= _end)
        {
            fieldNumber = 0;
            wireType = 0;
            return false;
        }
        uint tag = ReadVarint();
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);
        if (fieldNumber == 0) throw new InvalidDataException("Protobuf 字段号 0 非法——字节流损坏。");
        return true;
    }

    public int ReadInt32() => (int)ReadVarint();

    public string ReadString()
    {
        int len = ReadLength();
        string s = Encoding.UTF8.GetString(_buf, _pos, len);
        _pos += len;
        return s;
    }

    public byte[] ReadBytes()
    {
        int len = ReadLength();
        var bytes = new byte[len];
        Buffer.BlockCopy(_buf, _pos, bytes, 0, len);
        _pos += len;
        return bytes;
    }

    public ProtoReader ReadMessage()
    {
        int len = ReadLength();
        var sub = new ProtoReader(_buf, _pos, len);
        _pos += len;
        return sub;
    }

    public void SkipField(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: Advance(8); break;
            case 2: Advance(ReadLength()); break;
            case 5: Advance(4); break;
            default: throw new InvalidDataException($"未知 protobuf wire 类型 {wireType}——字节流损坏。");
        }
    }

    private int ReadLength()
    {
        int len = (int)ReadVarint();
        if (len < 0 || _pos + len > _end) throw new InvalidDataException("Protobuf 长度字段越界——字节流损坏。");
        return len;
    }

    private void Advance(int count)
    {
        if (_pos + count > _end) throw new InvalidDataException("Protobuf 读取越界——字节流损坏。");
        _pos += count;
    }

    private uint ReadVarint()
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            if (_pos >= _end) throw new InvalidDataException("Protobuf varint 未终止——字节流损坏。");
            byte b = _buf[_pos++];
            if (shift < 32) result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 64) throw new InvalidDataException("Protobuf varint 超长——字节流损坏。");
        }
    }
}
