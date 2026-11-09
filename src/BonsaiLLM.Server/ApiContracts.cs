namespace BonsaiLLM.Server;

/// <summary>界面用的状态快照；对话本身走 OpenAI 接口，这里只管模型与加载进度。</summary>
public sealed record StateDto(
    IReadOnlyList<ModelDto> Models,
    LoadedDto? Loaded,
    string Backend,
    bool Loading,
    double LoadProgress,
    string Message,
    InputLimitsDto Limits);

public sealed record ModelDto(
    string Id,
    string Path,
    string Quantization,
    string SizeText,
    bool HasVision,
    int NativeContext,
    bool FileExists);

public sealed record LoadedDto(
    string Id,
    string Path,
    string Quantization,
    bool HasVision,
    int ContextSize,
    int TokensUsed);

/// <summary>前端据此限制输入，避免一次把上下文撑爆。</summary>
public sealed record InputLimitsDto(int MaxMessageChars, int MaxImageBytes);

public sealed record UsageDto(int PromptTokens, int CompletionTokens);

/// <summary>推理进度事件：delta / done / error / cancelled。</summary>
public sealed record ChatEventDto(
    string Type,
    string? Text = null,
    UsageDto? Usage = null,
    double? TokensPerSecond = null,
    double? FirstTokenMs = null,
    string? Message = null)
{
    public static ChatEventDto Delta(string text) => new("delta", Text: text);

    public static ChatEventDto Done(UsageDto? usage, double? tokensPerSecond, double? firstTokenMs) =>
        new("done", Usage: usage, TokensPerSecond: tokensPerSecond, FirstTokenMs: firstTokenMs);

    public static ChatEventDto Error(string message) => new("error", Message: message);

    public static ChatEventDto Cancelled() => new("cancelled", Message: "已停止生成");
}

/// <summary>界面点「加载」时提交的模型名。</summary>
public sealed record LoadRequest(string Model);

/// <summary>/api/logs 的一行，来自 NLog 的内存副本。</summary>
public sealed record LogLineDto(string Time, string Level, string Message);

/// <summary>界面端点的失败说明。</summary>
public sealed record ErrorDto(string Message);

/// <summary>/api/stop 的结果。</summary>
public sealed record StopDto(bool Stopped);
