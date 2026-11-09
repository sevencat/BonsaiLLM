using BonsaiLLM.Core.Settings;
using LLama.Native;

namespace BonsaiLLM.Llama;

/// <summary>
/// 进程级原生后端配置。必须在任何 LLamaSharp 原生调用之前完成，选定后不可更改。
/// 关闭自动回退：用户选了 CUDA12 却加载失败，就报错让他重选，不静默跑 CPU。
/// </summary>
public static class LlamaBackend
{
    private static readonly object Gate = new();
    private static BackendKind? _configured;

    public static BackendKind? Configured
    {
        get
        {
            lock (Gate)
            {
                return _configured;
            }
        }
    }

    public static void Configure(BackendKind backend)
    {
        lock (Gate)
        {
            if (_configured == backend)
            {
                return;
            }

            if (_configured is not null)
            {
                throw new InvalidOperationException(
                    $"原生后端已初始化为 {_configured}，切换需要重启应用。");
            }

            var config = NativeLibraryConfig.All;
            config.WithAutoFallback(false);
            config.WithLogCallback(OnNativeLog);

            switch (backend)
            {
                case BackendKind.Cuda12:
                    config.WithCuda(true);
                    break;
                case BackendKind.Vulkan:
                    config.WithVulkan(true);
                    break;
                case BackendKind.Cpu:
                    config.WithCuda(false).WithVulkan(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(backend), backend, "未知后端");
            }

            _configured = backend;
        }
    }

    private static void OnNativeLog(LLamaLogLevel level, string message)
    {
        // 默认只留错误，避免 CUDA graph / 加载日志刷屏
        if (level == LLamaLogLevel.Error)
        {
            LastError = message;
        }
    }

    /// <summary>最近一条原生错误，用于把「后端初始化失败」做得可读。</summary>
    public static string? LastError { get; private set; }
}
