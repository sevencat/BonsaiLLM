using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using BonsaiLLM.Core.Chat;
using BonsaiLLM.Core.Models;
using BonsaiLLM.Core.Settings;
using BonsaiLLM.Llama;

using Microsoft.Extensions.Logging;

namespace BonsaiLLM.Server;

/// <summary>
/// 进程内唯一的推理宿主：模型清单来自 appsettings.json，引擎常驻。
/// 不保存会话——历史由客户端每轮带来，这里只做上下文预算裁剪后交给引擎。
/// </summary>
public sealed class ChatHost : IAsyncDisposable
{
    private const int MaxMessageChars = 32_000;

    private const int MaxImageBytes = 8 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly ServerOptions _options;
    private readonly ILogger<ChatHost> _logger;
    private readonly List<ModelEntry> _entries;
    private readonly LlamaChatEngine _engine;
    private readonly SemaphoreSlim _generateGate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private volatile bool _loading;
    private double _loadProgress;
    private string _message = "准备就绪";
    private CancellationTokenSource? _streamCts;

    public ChatHost(ServerOptions options, ILogger<ChatHost> logger)
    {
        _options = options;
        _logger = logger;

        // 原生后端必须在任何 LLamaSharp 调用之前定死，进程内不支持运行时切换
        LlamaBackend.Configure(options.ParseBackend());
        _engine = new LlamaChatEngine();

        _entries = options.Models.Select(BuildEntry).ToList();

        _logger.LogInformation(
            "BonsaiLLM.Server 启动：后端 {Backend}，{Count} 个模型（来自 appsettings.json）",
            LlamaBackend.Configured,
            _entries.Count);
    }

    public string Backend => (LlamaBackend.Configured ?? BackendKind.Cuda12).ToString();

    /// <summary>按 <c>defaultModel</c> 预热；失败只记日志，不影响 HTTP 服务启动。</summary>
    public void StartBackground()
    {
        if (string.IsNullOrWhiteSpace(_options.DefaultModel))
        {
            _logger.LogInformation("未配置 defaultModel，等首个请求再按需加载");
            return;
        }

        var entry = _entries.FirstOrDefault(item =>
            string.Equals(item.Name, _options.DefaultModel, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            _logger.LogWarning("defaultModel 不在 models 里：{Model}", _options.DefaultModel);
            return;
        }

        StartLoad(entry.Name);
    }

    public StateDto Snapshot()
    {
        lock (_sync)
        {
            var loaded = _engine.Loaded;
            var loadedEntry = loaded is null ? null : FindEntry(loaded.Path);

            return new StateDto(
                _entries
                    .Select(item => new ModelDto(
                        item.Name,
                        item.Descriptor.Path,
                        item.Descriptor.Quantization,
                        item.Descriptor.SizeText,
                        item.Descriptor.HasVision,
                        item.Descriptor.NativeContextLength,
                        File.Exists(item.Descriptor.Path)))
                    .ToList(),
                loaded is null
                    ? null
                    : new LoadedDto(
                        loadedEntry?.Name ?? DisplayName(loaded),
                        loaded.Path,
                        loaded.Quantization,
                        loaded.HasVision,
                        _engine.ContextSize,
                        _engine.ContextTokensUsed),
                Backend,
                _loading,
                _loadProgress,
                _message,
                new InputLimitsDto(MaxMessageChars, MaxImageBytes));
        }
    }

    /// <summary>响应里的 model 字段：优先报已加载的那个。</summary>
    public string ResolveModelId(string? model)
    {
        var loaded = _engine.Loaded;
        if (loaded is not null)
        {
            return FindEntry(loaded.Path)?.Name ?? DisplayName(loaded);
        }

        return ResolveEntry(model)?.Name ?? "unknown";
    }

    /// <summary>界面用的显式加载：按模型名/路径/文件名都能点。</summary>
    public bool StartLoad(string model)
    {
        var entry = ResolveEntry(model);
        if (entry is null || _loading)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureLoadedAsync(entry, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载模型失败");
            }
        });

        return true;
    }

    /// <summary>中止正在生成的那一轮（浏览器断开、点停止、Ctrl+C 都走这里）。</summary>
    public bool Stop()
    {
        var cts = _streamCts;
        if (cts is null)
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 一轮对话。事件序列固定：若干 delta → done / cancelled / error，
    /// 由 <see cref="Endpoints"/> 翻译成 OpenAI 的 chunk 或整包响应。
    /// </summary>
    public async IAsyncEnumerable<ChatEventDto> CompleteAsync(
        OpenAiChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 接口不负责换模型：只认当前驻留的那个，切换和加载留给网页。
        var loaded = _engine.Loaded;
        if (loaded is null)
        {
            yield return ChatEventDto.Error("还没有加载模型，先在网页上点「加载」再请求。");
            yield break;
        }

        var requestedKey = (request.Model ?? string.Empty).Trim();
        if (requestedKey.Length > 0 && MatchEntry(requestedKey) is { } requested &&
            !PathsEqual(requested.Descriptor.Path, loaded.Path))
        {
            var currentName = FindEntry(loaded.Path)?.Name ?? DisplayName(loaded);
            yield return ChatEventDto.Error(
                $"模型 {requested.Name} 没有加载，当前是 {currentName}；请先在网页上切换，接口不会自己换模型。");
            yield break;
        }

        var entry = FindEntry(loaded.Path);
        if (entry is null)
        {
            yield return ChatEventDto.Error("已加载的模型不在 appsettings.json 里了，重启 Server 试试。");
            yield break;
        }

        var (messages, image, parseError) = ParseMessages(request, entry);
        if (parseError is not null)
        {
            yield return ChatEventDto.Error(parseError);
            yield break;
        }

        var options = BuildChatOptions(entry, request, image);

        await _generateGate.WaitAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var firstTokenMs = -1.0;
        UsageInfo? usage = null;
        var cancelled = false;
        string? error = null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _streamCts = cts;

            // 手写枚举器：yield 不能出现在含 catch 的 try 里，异常只在推进枚举时接
            await using var enumerator = _engine
                .StreamAsync(messages, options, cts.Token)
                .GetAsyncEnumerator(cts.Token);

            while (true)
            {
                bool hasNext;

                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _logger.LogError(ex, "生成失败");
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                var delta = enumerator.Current;

                if (delta.IsDone)
                {
                    usage = delta.Usage;
                    break;
                }

                if (delta.Text.Length == 0)
                {
                    continue;
                }

                if (firstTokenMs < 0)
                {
                    firstTokenMs = stopwatch.Elapsed.TotalMilliseconds;
                }

                yield return ChatEventDto.Delta(delta.Text);
            }
        }
        finally
        {
            _streamCts = null;
            _generateGate.Release();
        }

        if (error is not null)
        {
            yield return ChatEventDto.Error(error);
            yield break;
        }

        if (cancelled)
        {
            yield return ChatEventDto.Cancelled();
            yield break;
        }

        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        var seconds = (elapsedMs - Math.Max(0, firstTokenMs)) / 1000.0;
        var completionTokens = usage?.CompletionTokens ?? 0;

        yield return ChatEventDto.Done(
            usage is null ? null : new UsageDto(usage.PromptTokens, usage.CompletionTokens),
            seconds > 0.05 && completionTokens > 0 ? completionTokens / seconds : null,
            firstTokenMs < 0 ? null : firstTokenMs);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();

        // 后台的预热/按需加载任务可能还在 Release 这两个 gate，Dispose 掉只会换来
        // ObjectDisposedException；SemaphoreSlim 不持有非托管资源，留给 GC 就行。
        await _engine.DisposeAsync();
    }

    private async Task<string?> EnsureLoadedAsync(ModelEntry entry, CancellationToken cancellationToken)
    {
        if (!File.Exists(entry.Descriptor.Path))
        {
            return $"模型文件不存在：{entry.Descriptor.Path}";
        }

        var loaded = _engine.Loaded;
        if (loaded is not null && PathsEqual(loaded.Path, entry.Descriptor.Path))
        {
            return null;
        }

        await _loadGate.WaitAsync(cancellationToken);

        try
        {
            loaded = _engine.Loaded;
            if (loaded is not null && PathsEqual(loaded.Path, entry.Descriptor.Path))
            {
                return null;
            }

            var ok = await LoadCoreAsync(entry, cancellationToken);
            return ok ? null : _message;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<bool> LoadCoreAsync(ModelEntry entry, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _loading = true;

        lock (_sync)
        {
            _loadProgress = 0;
            _message = $"正在加载 {entry.Name}…";
        }

        try
        {
            var progress = new Progress<float>(value =>
            {
                lock (_sync)
                {
                    _loadProgress = value;
                }
            });

            await _engine.LoadAsync(entry.Descriptor, BuildLoadOptions(entry), progress, cancellationToken);

            lock (_sync)
            {
                _loadProgress = 1;
                _message = $"{entry.Name} 已加载（{stopwatch.Elapsed.TotalSeconds:0.0} s）";
            }

            _logger.LogInformation("模型加载完成：{Model}，用时 {Seconds:0.0} s", entry.Name, stopwatch.Elapsed.TotalSeconds);
            return true;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _loadProgress = 0;
                _message = $"加载失败：{ex.Message}";
            }

            _logger.LogError(ex, "加载模型失败：{Model}", entry.Name);
            return false;
        }
        finally
        {
            _loading = false;
        }
    }

    private (List<ChatMessage> Messages, byte[]? Image, string? Error) ParseMessages(
        OpenAiChatRequest request,
        ModelEntry entry)
    {
        try
        {
            var (messages, image) = ParseMessagesCore(request, entry);
            return (messages, image, null);
        }
        catch (Exception ex)
        {
            return ([], null, ex.Message);
        }
    }

    private (List<ChatMessage> Messages, byte[]? Image) ParseMessagesCore(
        OpenAiChatRequest request,
        ModelEntry entry)
    {
        var messages = new List<ChatMessage>();
        byte[]? image = null;
        var hasSystem = false;

        foreach (var message in request.Messages ?? [])
        {
            if (message is null)
            {
                continue;
            }

            var role = (message.Role ?? "user").Trim().ToLowerInvariant();
            var (text, imageUrl) = ReadContent(message.Content);

            if (role == "assistant")
            {
                var content = (text ?? string.Empty).Trim();
                if (content.Length > 0)
                {
                    messages.Add(new ChatMessage(ChatRole.Assistant, Clamp(content)));
                }

                continue;
            }

            if (role is "system" or "developer")
            {
                var content = (text ?? string.Empty).Trim();
                if (content.Length > 0)
                {
                    messages.Add(new ChatMessage(ChatRole.System, Clamp(content)));
                    hasSystem = true;
                }

                continue;
            }

            if (imageUrl is not null)
            {
                if (!entry.Descriptor.HasVision)
                {
                    throw new InvalidOperationException($"模型 {entry.Name} 没有视觉投影（mmproj），不能接收图片");
                }

                image = DecodeImage(imageUrl);
            }

            var userText = (text ?? string.Empty).Trim();
            if (userText.Length == 0)
            {
                if (image is null)
                {
                    continue;
                }

                userText = "描述这张图片。";
            }

            messages.Add(new ChatMessage(ChatRole.User, Clamp(userText)));
        }

        if (!hasSystem)
        {
            messages.Insert(0, new ChatMessage(ChatRole.System, SystemPromptFor(entry)));
        }

        if (messages.Count <= 1)
        {
            throw new InvalidOperationException("messages 里没有可用的对话内容");
        }

        return (Trim(messages, BuildChatOptions(entry, request, null)), image);
    }

    /// <summary>按上下文窗口从尾部反推保留多少轮，system 永远保留。</summary>
    private List<ChatMessage> Trim(List<ChatMessage> messages, ChatOptions options)
    {
        var window = _engine.ContextSize;
        if (window <= 0 || messages.Count <= 2)
        {
            return messages;
        }

        var reserve = Math.Max(256, options.MaxTokens > 0 ? options.MaxTokens : 512);
        var budget = Math.Max(512, window - reserve - 64);

        var kept = new List<ChatMessage> { messages[0] };
        var total = _engine.CountTokens(messages[0].Text) + 8;

        for (var i = messages.Count - 1; i >= 1; i--)
        {
            var cost = _engine.CountTokens(messages[i].Text) + 8;
            if (total + cost > budget)
            {
                break;
            }

            kept.Insert(1, messages[i]);
            total += cost;
        }

        // 最新一句怎么都塞不进窗口时，宁可截断上下文也不返回空对话
        if (kept.Count == 1)
        {
            kept.Add(messages[^1]);
        }

        return kept;
    }

    private ModelLoadOptions BuildLoadOptions(ModelEntry entry)
    {
        var defaults = _options.Defaults;
        var config = entry.Config;

        return new ModelLoadOptions
        {
            Backend = _options.ParseBackend(),
            GpuLayerCount = config.GpuLayers ?? defaults.GpuLayers,
            ContextSize = config.ContextSize ?? defaults.ContextSize,
            BatchSize = config.BatchSize ?? defaults.BatchSize,
            Threads = config.Threads ?? defaults.Threads,
            KvCacheType = config.KvCacheType ?? defaults.KvCacheType,
            LoadVisionProjector = entry.Descriptor.HasVision,
        };
    }

    private ChatOptions BuildChatOptions(ModelEntry entry, OpenAiChatRequest request, byte[]? imageBytes)
    {
        var defaults = _options.Defaults;
        var config = entry.Config;

        return new ChatOptions
        {
            Temperature = request.Temperature ?? config.Temperature ?? defaults.Temperature,
            TopP = request.TopP ?? config.TopP ?? defaults.TopP,
            TopK = config.TopK ?? defaults.TopK,
            RepeatPenalty = config.RepeatPenalty ?? defaults.RepeatPenalty,
            MaxTokens = request.MaxCompletionTokens ?? request.MaxTokens ?? config.MaxTokens ?? defaults.MaxTokens,
            ImageBytes = imageBytes,
        };
    }

    private string SystemPromptFor(ModelEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Config.SystemPrompt)
            ? _options.DefaultSystemPrompt
            : entry.Config.SystemPrompt!;

    /// <summary>只做精确匹配（name / 全路径 / 文件名 / 去扩展名的文件名），不做兜底。</summary>
    private ModelEntry? MatchEntry(string key) =>
        key.Length == 0
            ? null
            : _entries.FirstOrDefault(item =>
                string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase)
                || PathsEqual(item.Descriptor.Path, key)
                || string.Equals(item.Descriptor.FileName, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFileNameWithoutExtension(item.Descriptor.FileName),
                    key,
                    StringComparison.OrdinalIgnoreCase));

    private ModelEntry? ResolveEntry(string? model)
    {
        var key = (model ?? string.Empty).Trim();

        if (key.Length > 0)
        {
            var matched = MatchEntry(key);

            if (matched is not null)
            {
                return matched;
            }

            _logger.LogWarning("请求的模型不在配置里：{Model}，改用默认模型", key);
        }

        var loaded = _engine.Loaded;
        if (loaded is not null)
        {
            var current = FindEntry(loaded.Path);
            if (current is not null)
            {
                return current;
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.DefaultModel))
        {
            var configured = _entries.FirstOrDefault(item =>
                string.Equals(item.Name, _options.DefaultModel, StringComparison.OrdinalIgnoreCase));

            if (configured is not null)
            {
                return configured;
            }
        }

        return _entries.FirstOrDefault();
    }

    private ModelEntry? FindEntry(string path) =>
        _entries.FirstOrDefault(item => PathsEqual(item.Descriptor.Path, path));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Clamp(string text) =>
        text.Length <= MaxMessageChars ? text : text[..MaxMessageChars];

    private static string DisplayName(ModelDescriptor descriptor) =>
        string.IsNullOrWhiteSpace(descriptor.DisplayName) ? descriptor.FileName : descriptor.DisplayName;

    private static (string? Text, string? ImageUrl) ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return (content.GetString(), null);
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        var builder = new StringBuilder();
        string? imageUrl = null;

        foreach (var part in content.EnumerateArray())
        {
            var type = part.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

            switch (type)
            {
                case "text":
                    if (part.TryGetProperty("text", out var textElement))
                    {
                        builder.AppendLine(textElement.GetString());
                    }

                    break;

                case "image_url":
                    if (!part.TryGetProperty("image_url", out var imageElement))
                    {
                        break;
                    }

                    imageUrl = imageElement.ValueKind switch
                    {
                        JsonValueKind.String => imageElement.GetString(),
                        JsonValueKind.Object when imageElement.TryGetProperty("url", out var urlElement)
                            => urlElement.GetString(),
                        _ => imageUrl,
                    };

                    break;
            }
        }

        return (builder.ToString().Trim(), imageUrl);
    }

    /// <summary>只接受 data URL；http 链接不代下载，避免 Server 变成代理。</summary>
    private static byte[] DecodeImage(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0)
        {
            throw new InvalidOperationException("image_url 只支持 data:image/...;base64, 形式");
        }

        var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        if (bytes.Length > MaxImageBytes)
        {
            throw new InvalidOperationException($"图片超过 {MaxImageBytes / 1024 / 1024} MB");
        }

        return bytes;
    }

    /// <summary>配置项 → 运行时条目；头部元数据读不出来也照样列出，只是少了量化信息。</summary>
    private ModelEntry BuildEntry(ModelConfig config)
    {
        var path = (config.Path ?? string.Empty).Trim();
        var exists = path.Length > 0 && File.Exists(path);

        var descriptor = exists
            ? ModelCatalog.ReadDescriptor(path)
            : new ModelDescriptor
            {
                Path = path,
                FileName = Path.GetFileName(path),
                DisplayName = config.Name,
                MetadataAvailable = false,
            };

        var (visionPath, visionSize) = ResolveVision(config.VisionProjector, path);
        var name = string.IsNullOrWhiteSpace(config.Name) ? descriptor.DisplayName : config.Name.Trim();

        return new ModelEntry(
            name,
            config,
            descriptor with
            {
                DisplayName = name,
                VisionProjectorPath = visionPath,
                VisionProjectorSizeBytes = visionSize,
            });
    }

    private (string? Path, long Size) ResolveVision(string? configured, string modelPath)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return (null, 0);
        }

        var path = configured.Trim();

        if (!Path.IsPathRooted(path) && modelPath.Length > 0)
        {
            var directory = Path.GetDirectoryName(modelPath);
            if (!string.IsNullOrEmpty(directory))
            {
                path = Path.Combine(directory, path);
            }
        }

        if (!File.Exists(path))
        {
            _logger.LogWarning("视觉投影不存在：{Path}", path);
            return (null, 0);
        }

        return (path, new FileInfo(path).Length);
    }

    private sealed record ModelEntry(string Name, ModelConfig Config, ModelDescriptor Descriptor);
}
