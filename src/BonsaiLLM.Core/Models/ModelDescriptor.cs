namespace BonsaiLLM.Core.Models;

/// <summary>扫描得到的一个可加载模型（主权重）。</summary>
public sealed record ModelDescriptor
{
    public required string Path { get; init; }

    public required string FileName { get; init; }

    /// <summary>GGUF 里的 general.name，缺失时用文件名。</summary>
    public string DisplayName { get; init; } = "";

    public string Architecture { get; init; } = "";

    /// <summary>从文件名推断的量化，如 Q4_K_S。</summary>
    public string Quantization { get; init; } = "";

    public long FileSizeBytes { get; init; }

    public int NativeContextLength { get; init; }

    public int LayerCount { get; init; }

    public int HeadCount { get; init; }

    public int HeadCountKv { get; init; }

    public int EmbeddingLength { get; init; }

    /// <summary>同目录配对到的视觉投影文件；纯文本模型为 null。</summary>
    public string? VisionProjectorPath { get; init; }

    public long VisionProjectorSizeBytes { get; init; }

    /// <summary>头部读取失败时只显示文件名与大小。</summary>
    public bool MetadataAvailable { get; init; }

    /// <summary>GGUF 内嵌 chat template，缺失则为 null。</summary>
    public string? ChatTemplate { get; init; }

    public bool HasVision => !string.IsNullOrEmpty(VisionProjectorPath);

    /// <summary>估算参数量（亿），由层数 × embedding² × 12 粗估。</summary>
    public double ParameterBillions => LayerCount > 0 && EmbeddingLength > 0
        ? LayerCount * (double)EmbeddingLength * EmbeddingLength * 12 / 1e9
        : 0;

    public long TotalSizeBytes => FileSizeBytes + VisionProjectorSizeBytes;

    public string SizeText => FormatSize(TotalSizeBytes);

    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
