namespace BonsaiLLM.Llama;

/// <summary>模型加载失败，Message 可直接展示给用户。</summary>
public sealed class ModelLoadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>所选原生后端不可用；UI 应提示用户换一个后端并重启。</summary>
public sealed class BackendUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
