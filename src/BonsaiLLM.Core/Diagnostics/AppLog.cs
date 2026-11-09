namespace BonsaiLLM.Core.Diagnostics;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message);

/// <summary>
/// 进程内环形日志缓冲。核心层没有 UI 依赖，界面通过 <see cref="Appended"/> 事件取增量，
/// 启动时用 <see cref="Snapshot"/> 补齐历史；缓冲有上限，长跑不会一直吃内存。
/// </summary>
public static class AppLog
{
    public const int Capacity = 500;

    private static readonly object Sync = new();
    private static readonly Queue<LogEntry> Buffer = new(Capacity);

    /// <summary>可能在线程池线程上触发，订阅方自行切回 UI 线程。</summary>
    public static event Action<LogEntry>? Appended;

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}：{ex.GetType().Name}: {ex.Message}");

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);

        lock (Sync)
        {
            Buffer.Enqueue(entry);
            while (Buffer.Count > Capacity)
            {
                Buffer.Dequeue();
            }
        }

        var handlers = Appended;
        if (handlers is null)
        {
            return;
        }

        // 订阅方抛异常不能影响调用方（比如加载模型的主流程）
        foreach (var handler in handlers.GetInvocationList().Cast<Action<LogEntry>>())
        {
            try
            {
                handler(entry);
            }
            catch (Exception)
            {
                // 忽略日志本身的故障
            }
        }
    }

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Sync)
        {
            return Buffer.ToArray();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Buffer.Clear();
        }
    }
}
