using BonsaiLLM.Core.Models;

namespace BonsaiLLM.Core.Chat;

/// <summary>推理入口。M1 由 DemoChatEngine 提供假数据，M2 起由 BonsaiLLM.Llama 实现。</summary>
public interface IChatEngine : IAsyncDisposable
{
    string DisplayName { get; }

    /// <summary>当前驻留的模型；未加载时为 null。</summary>
    ModelDescriptor? Loaded { get; }

    bool IsLoaded { get; }

    /// <summary>当前上下文窗口（token），用于预算与裁剪；未加载时为 0。</summary>
    int ContextSize { get; }

    /// <summary>当前 KV cache 里已占用的 token 数（含历史），用于顶栏「已用/总量」；未加载时为 0。</summary>
    int ContextTokensUsed { get; }

    /// <summary>按模型 tokenizer 计数，不可用时退化为字符估算。</summary>
    int CountTokens(string text);

    /// <summary>加载模型；失败应抛出带可读原因的异常，且不改变 <see cref="Loaded"/>。</summary>
    Task LoadAsync(
        ModelDescriptor model,
        ModelLoadOptions options,
        IProgress<float>? progress,
        CancellationToken cancellationToken);

    Task UnloadAsync();

    IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        ChatOptions options,
        CancellationToken cancellationToken);
}
