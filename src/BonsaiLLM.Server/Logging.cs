using System.Text;

using NLog;
using NLog.Config;
using NLog.Targets;

using NLogLevel = NLog.LogLevel;

namespace BonsaiLLM.Server;

/// <summary>
/// 内存里留最近若干条，给 <c>/api/logs</c> 用；页面不依赖它，排查问题时省得翻文件。
/// </summary>
public sealed class MemoryLogTarget : TargetWithLayout
{
    public const int Capacity = 500;

    private readonly Lock _sync = new();
    private readonly Queue<LogLineDto> _buffer = new(Capacity);

    protected override void Write(LogEventInfo logEvent)
    {
        var line = new LogLineDto(
            logEvent.TimeStamp.ToString("HH:mm:ss"),
            logEvent.Level.Name.ToLowerInvariant(),
            logEvent.FormattedMessage);

        lock (_sync)
        {
            _buffer.Enqueue(line);
            while (_buffer.Count > Capacity)
            {
                _buffer.Dequeue();
            }
        }
    }

    public IReadOnlyList<LogLineDto> Snapshot()
    {
        lock (_sync)
        {
            return _buffer.ToArray();
        }
    }
}

/// <summary>
/// 代码式 NLog 配置：控制台 + 按天滚动的文件 + 内存副本。
/// 不读 NLog.config —— Native AOT 下走 XML 要带上反射元数据，没必要。
/// </summary>
public static class LoggingSetup
{
    private const string ConsoleLayout =
        "${longdate} [${level:uppercase=true:padding=-5}] ${message}${onexception:${newline}${exception:format=tostring}}";

    private const string FileLayout =
        "${longdate}|${level:uppercase=true:padding=-5}|${logger:shortName=true}|${message}${onexception:${newline}${exception:format=tostring}}";

    public static MemoryLogTarget Configure(ServerOptions options, string contentRoot)
    {
        var minLevel = ParseLevel(options.LogLevel);
        var memory = new MemoryLogTarget { Name = "memory" };
        var config = new LoggingConfiguration();

        var console = new ColoredConsoleTarget("console")
        {
            Layout = ConsoleLayout,
            UseDefaultRowHighlightingRules = true,
        };

        var file = new FileTarget("file")
        {
            FileName = Path.Combine(ResolveLogDirectory(options, contentRoot), "server-${shortdate}.log"),
            Layout = FileLayout,
            Encoding = Encoding.UTF8,
            KeepFileOpen = true,
            ArchiveEvery = FileArchivePeriod.Day,
            MaxArchiveDays = 14,
        };

        // ASP.NET Core 每个请求要写四行 Info，太吵：Info 及以下丢掉，Warn 以上照常进三处。
        // NLog 的规则按顺序匹配，Final 只作用于「已经命中」的那条，所以低级别得先单独吃掉。
        // 注意别连 Microsoft.Hosting.* 一起吞——"Now listening on" 还是想看见。
        var quiet = new LoggingRule(
            "Microsoft.AspNetCore.*",
            NLogLevel.Trace,
            NLogLevel.Info,
            new DiscardTarget { Name = "quiet" }) { Final = true };

        config.LoggingRules.Add(quiet);

        var framework = new LoggingRule("Microsoft.AspNetCore.*", NLogLevel.Warn, NLogLevel.Fatal, console) { Final = true };
        framework.Targets.Add(file);
        framework.Targets.Add(memory);
        config.LoggingRules.Add(framework);

        config.AddRule(minLevel, NLogLevel.Fatal, console);
        config.AddRule(minLevel, NLogLevel.Fatal, file);
        config.AddRule(minLevel, NLogLevel.Fatal, memory);

        LogManager.Configuration = config;
        return memory;
    }

    private static string ResolveLogDirectory(ServerOptions options, string contentRoot)
    {
        var configured = (options.LogDirectory ?? string.Empty).Trim();

        if (configured.Length == 0)
        {
            return Path.Combine(contentRoot, "logs");
        }

        return Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
    }

    private static NLogLevel ParseLevel(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "trace" or "verbose" => NLogLevel.Trace,
        "debug" => NLogLevel.Debug,
        "warn" or "warning" => NLogLevel.Warn,
        "error" => NLogLevel.Error,
        "fatal" => NLogLevel.Fatal,
        "off" or "none" => NLogLevel.Off,
        _ => NLogLevel.Info,
    };
}

/// <summary>什么都不写的 target，用来把低级别日志吃掉、让规则链提前结束。</summary>
internal sealed class DiscardTarget : Target
{
    protected override void Write(LogEventInfo logEvent)
    {
    }
}
