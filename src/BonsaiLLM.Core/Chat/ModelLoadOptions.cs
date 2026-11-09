namespace BonsaiLLM.Core.Chat;

/// <summary>加载模型时的参数（已合并全局默认与单模型覆盖）。</summary>
public sealed record ModelLoadOptions
{
    public int GpuLayerCount { get; init; } = 99;

    public int ContextSize { get; init; } = 8192;

    public int BatchSize { get; init; } = 512;

    /// <summary>0 表示交给运行时决定。</summary>
    public int Threads { get; init; }

    /// <summary>f16 / q8_0 / q4_0。</summary>
    public string KvCacheType { get; init; } = "f16";

    public bool LoadVisionProjector { get; init; } = true;
}
