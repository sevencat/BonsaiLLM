using Avalonia.Media;
using BonsaiLLM.Core.Diagnostics;

namespace BonsaiLLM.App.ViewModels;

/// <summary>日志抽屉里的一行。颜色按级别区分，复制时用 <see cref="ToLogLine"/> 的纯文本。</summary>
public sealed class LogEntryViewModel
{
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#374151"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#B45309"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#B91C1C"));

    public LogEntryViewModel(LogEntry entry)
    {
        TimeText = entry.Time.ToString("HH:mm:ss");
        LevelText = entry.Level switch
        {
            LogLevel.Warning => "警告",
            LogLevel.Error => "错误",
            _ => "信息",
        };
        Message = entry.Message;
        Foreground = entry.Level switch
        {
            LogLevel.Warning => WarningBrush,
            LogLevel.Error => ErrorBrush,
            _ => InfoBrush,
        };
    }

    public string TimeText { get; }

    public string LevelText { get; }

    public string Message { get; }

    public IBrush Foreground { get; }

    public string ToLogLine() => $"[{TimeText}] [{LevelText}] {Message}";
}
