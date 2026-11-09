using System.Text;

namespace BonsaiLLM.Core.Gguf;

/// <summary>
/// 只读 GGUF 头部的极简解析器：读元数据键值 + 张量表（张量数据跳过）。
/// 支持 v1/v2/v3；列向量尺寸等大字段不读，只走到张量表结束。
/// </summary>
public static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF"

    /// <summary>读取元数据；文件不存在/不是 GGUF/版本不支持时抛 <see cref="GgufFormatException"/>。</summary>
    public static GgufMetadata Read(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1 << 16,
            FileOptions.SequentialScan);

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Read(reader);
    }

    public static GgufMetadata Read(BinaryReader reader)
    {
        if (reader.ReadUInt32() != Magic)
        {
            throw new GgufFormatException("不是 GGUF 文件（magic 不匹配）");
        }

        var version = reader.ReadUInt32();
        if (version is < 1 or > 3)
        {
            throw new GgufFormatException($"不支持的 GGUF 版本 {version}");
        }

        ulong tensorCount;
        ulong kvCount;
        if (version == 1)
        {
            tensorCount = reader.ReadUInt32();
            kvCount = reader.ReadUInt32();
        }
        else
        {
            tensorCount = reader.ReadUInt64();
            kvCount = reader.ReadUInt64();
        }

        var values = new Dictionary<string, object?>(capacity: (int)Math.Min(kvCount, 1024), StringComparer.Ordinal);
        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadString(reader, version);
            var type = (GgufValueType)reader.ReadUInt32();
            values[key] = ReadValue(reader, version, type, depth: 0);
        }

        // 张量表只需要跳过，不需要内容
        for (ulong i = 0; i < tensorCount; i++)
        {
            _ = ReadString(reader, version); // name
            var dimensionCount = reader.ReadUInt32();
            for (var d = 0; d < dimensionCount; d++)
            {
                _ = reader.ReadUInt64();
            }

            _ = reader.ReadUInt32(); // ggml type
            _ = reader.ReadUInt64(); // offset
        }

        return new GgufMetadata(values, (int)version, tensorCount);
    }

    private static string ReadString(BinaryReader reader, uint version)
    {
        var length = version == 1 ? reader.ReadUInt32() : reader.ReadUInt64();
        if (length > 64UL * 1024 * 1024)
        {
            throw new GgufFormatException($"字符串长度异常：{length}");
        }

        var bytes = reader.ReadBytes((int)length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static object? ReadValue(BinaryReader reader, uint version, GgufValueType type, int depth)
    {
        switch (type)
        {
            case GgufValueType.UInt8: return (long)reader.ReadByte();
            case GgufValueType.Int8: return (long)reader.ReadSByte();
            case GgufValueType.UInt16: return (long)reader.ReadUInt16();
            case GgufValueType.Int16: return (long)reader.ReadInt16();
            case GgufValueType.UInt32: return (long)reader.ReadUInt32();
            case GgufValueType.Int32: return (long)reader.ReadInt32();
            case GgufValueType.Float32: return (double)reader.ReadSingle();
            case GgufValueType.Bool: return reader.ReadByte() != 0;
            case GgufValueType.String: return ReadString(reader, version);
            case GgufValueType.Array:
            {
                if (depth > 2)
                {
                    throw new GgufFormatException("数组嵌套过深");
                }

                var elementType = (GgufValueType)reader.ReadUInt32();
                var count = version == 1 ? reader.ReadUInt32() : reader.ReadUInt64();
                if (count > 16UL * 1024 * 1024)
                {
                    throw new GgufFormatException($"数组长度异常：{count}");
                }

                var array = new object?[count];
                for (ulong i = 0; i < count; i++)
                {
                    array[i] = ReadValue(reader, version, elementType, depth + 1);
                }

                return array;
            }
            case GgufValueType.UInt64: return unchecked((long)reader.ReadUInt64());
            case GgufValueType.Int64: return reader.ReadInt64();
            case GgufValueType.Float64: return reader.ReadDouble();
            default:
                throw new GgufFormatException($"未知的元数据类型 {(uint)type}");
        }
    }

    private enum GgufValueType : uint
    {
        UInt8 = 0,
        Int8 = 1,
        UInt16 = 2,
        Int16 = 3,
        UInt32 = 4,
        Int32 = 5,
        Float32 = 6,
        Bool = 7,
        String = 8,
        Array = 9,
        UInt64 = 10,
        Int64 = 11,
        Float64 = 12,
    }
}

public sealed class GgufFormatException(string message) : Exception(message);
