using BonsaiLLM.Core.Models;

namespace BonsaiLLM.Core.Chat;

/// <summary>推理入口。M1 由 DemoChatEngine 提供假数据，M2 起由 BonsaiLLM.Llama 实现。</summary>
public interface IChatEngine : IAsyncDisposable
{
    string DisplayName { get; }

    /// <summary>当前驻留的模型；未加载时为 null。</summary>
    ModelDescriptor? Loaded { get; }

    bool IsLoaded { get; }

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
