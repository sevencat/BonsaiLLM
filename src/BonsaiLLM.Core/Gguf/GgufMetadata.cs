namespace BonsaiLLM.Core.Gguf;

/// <summary>
/// GGUF 头部的键值元数据。值类型映射：字符串 → <see cref="string"/>，整数 → <see cref="long"/>，
/// 浮点 → <see cref="double"/>，布尔 → <see cref="bool"/>，数组 → <see cref="Array"/>（元素同上）。
/// </summary>
public sealed class GgufMetadata
{
    public GgufMetadata(IReadOnlyDictionary<string, object?> values, int version, ulong tensorCount)
    {
        Values = values;
        Version = version;
        TensorCount = tensorCount;
    }

    public IReadOnlyDictionary<string, object?> Values { get; }

    public int Version { get; }

    public ulong TensorCount { get; }

    public bool TryGetValue(string key, out object? value) => Values.TryGetValue(key, out value);

    public string? GetString(string key) =>
        Values.TryGetValue(key, out var value) ? value as string : null;

    public long? GetInt64(string key) => Values.TryGetValue(key, out var value)
        ? value switch
        {
            long l => l,
            int i => i,
            uint ui => ui,
            ulong ul => unchecked((long)ul),
            _ => null,
        }
        : null;

    public int? GetInt32(string key)
    {
        var value = GetInt64(key);
        return value is null ? null : unchecked((int)Math.Clamp(value.Value, int.MinValue, int.MaxValue));
    }

    /// <summary>取数值或数值数组的第一个值（head_count_kv 等键可能是标量也可能是数组）。</summary>
    public int? GetInt32First(string key)
    {
        if (GetInt64(key) is { } scalar)
        {
            return unchecked((int)scalar);
        }

        if (!Values.TryGetValue(key, out var value) || value is not Array array || array.Length == 0)
        {
            return null;
        }

        var first = array.GetValue(0);
        return first switch
        {
            long l => unchecked((int)l),
            int i => i,
            _ => null,
        };
    }
}
