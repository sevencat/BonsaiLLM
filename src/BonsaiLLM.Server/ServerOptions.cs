using BonsaiLLM.Core.Settings;

namespace BonsaiLLM.Server;

/// <summary>
/// appsettings.json 的 <c>BonsaiLLM</c> 节。Server 不做目录扫描，
/// 模型和运行参数全部写死在配置里，改完重启进程即生效。
/// </summary>
public sealed class ServerOptions
{
    public string Urls { get; set; } = "http://127.0.0.1:5180";

    /// <summary>cuda12 / vulkan / cpu；一个进程只能固定一个后端。</summary>
    public string Backend { get; set; } = "cuda12";

    public string DefaultSystemPrompt { get; set; } = "你是一个乐于助人的中文助手，回答尽量简洁准确。";

    /// <summary>启动后自动加载的模型（对应 <see cref="Models"/> 的 name），留空则等前端点。</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>NLog 最低级别：trace / debug / info / warn / error / off。</summary>
    public string LogLevel { get; set; } = "info";

    /// <summary>日志目录；相对路径按程序目录解析，留空写 <c>logs</c>。</summary>
    public string LogDirectory { get; set; } = "logs";

    /// <summary>所有模型共用的默认参数，模型条目里可单项覆盖。</summary>
    public ModelDefaults Defaults { get; set; } = new();

    public List<ModelConfig> Models { get; set; } = [];

    public BackendKind ParseBackend() => Backend?.Trim().ToLowerInvariant() switch
    {
        "cuda" or "cuda12" or "cuda-12" => BackendKind.Cuda12,
        "vulkan" => BackendKind.Vulkan,
        "cpu" => BackendKind.Cpu,
        _ => BackendKind.Cuda12,
    };
}

public sealed class ModelConfig
{
    /// <summary>界面显示名，同时用于匹配 <see cref="ServerOptions.DefaultModel"/>。</summary>
    public string Name { get; set; } = "";

    /// <summary>主权重绝对路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>视觉投影（mmproj）；相对路径按主权重目录解析，纯文本模型留空。</summary>
    public string VisionProjector { get; set; } = "";

    public int? GpuLayers { get; set; }

    public int? ContextSize { get; set; }

    public int? BatchSize { get; set; }

    public int? Threads { get; set; }

    public string? KvCacheType { get; set; }

    public string? SystemPrompt { get; set; }

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? TopK { get; set; }

    public double? RepeatPenalty { get; set; }

    public int? MaxTokens { get; set; }
}
