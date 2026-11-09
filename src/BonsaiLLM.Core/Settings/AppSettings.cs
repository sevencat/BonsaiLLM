namespace BonsaiLLM.Core.Settings;

/// <summary>本地推理后端。一个进程只注册一个，切换需要重启。</summary>
public enum BackendKind
{
    Cuda12,
    Vulkan,
    Cpu,
}

/// <summary>上下文模式：携带历史 / 每次全新。</summary>
public enum ContextMode
{
    Carry,
    Fresh,
}

/// <summary>%AppData%\BonsaiLLM\settings.json 的模型，字段与 plan.md 5.2 对应。</summary>
public sealed class AppSettings
{
    /// <summary>用户显式添加的扫描根，初始为空。</summary>
    public List<string> ModelDirectories { get; set; } = [];

    /// <summary>上次成功加载的模型，启动时自动加载。</summary>
    public string LastModelPath { get; set; } = "";

    /// <summary>null 表示尚未选择，首次启动需要让用户选。</summary>
    public BackendKind? Backend { get; set; }

    public int FontSize { get; set; } = 14;

    public ContextMode DefaultContextMode { get; set; } = ContextMode.Fresh;

    public ModelDefaults Defaults { get; set; } = new();

    /// <summary>key = 模型绝对路径，只存与 Defaults 不同的字段。</summary>
    public Dictionary<string, ModelProfile> ModelProfiles { get; set; } = [];

    /// <summary>特化总开关（一期用于 WeVisOne 坐标检测）。</summary>
    public bool SpecializationEnabled { get; set; } = true;

    /// <summary>需要特化的模型名关键字，命中才启用适配器。</summary>
    public List<string> SpecializedModelNames { get; set; } = ["WeVisOne"];

    public bool NeedsSpecialization(string modelName) =>
        SpecializationEnabled
        && SpecializedModelNames.Any(name =>
            !string.IsNullOrWhiteSpace(name)
            && modelName.Contains(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>全局默认加载/采样参数。</summary>
public sealed class ModelDefaults
{
    public int GpuLayers { get; set; } = 99;

    public int ContextSize { get; set; } = 8192;

    public int BatchSize { get; set; } = 512;

    public string KvCacheType { get; set; } = "f16";

    public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    public double Temperature { get; set; } = 0.7;

    public double TopP { get; set; } = 0.9;

    public int TopK { get; set; } = 40;

    public double RepeatPenalty { get; set; } = 1.1;

    public int MaxTokens { get; set; } = -1;
}

/// <summary>单模型覆盖值；null 表示沿用全局默认。</summary>
public sealed class ModelProfile
{
    public int? GpuLayers { get; set; }

    public int? ContextSize { get; set; }

    public int? BatchSize { get; set; }

    public string? KvCacheType { get; set; }

    public int? Threads { get; set; }

    public string? SystemPrompt { get; set; }

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? TopK { get; set; }

    public double? RepeatPenalty { get; set; }

    public int? MaxTokens { get; set; }
}
