using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using BonsaiLLM.Core.Chat;
using BonsaiLLM.Core.Diagnostics;
using BonsaiLLM.Core.Models;
using BonsaiLLM.Core.Settings;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging.Abstractions;

namespace BonsaiLLM.Llama;

/// <summary>
/// LLamaSharp 实现。一个实例同时只驻留一个模型；
/// 推理在后台任务里跑，UI 侧通过 Channel 拿增量，避免预填充卡界面。
/// </summary>
public sealed class LlamaChatEngine : IChatEngine
{
    private static readonly string[] AntiPrompts = ["<|im_end|>", "<|endoftext|>", "<|eot_id|>"];

    /// <summary>图片占用的 token 数只能估：不同分辨率差异很大，按 1024 保守预留。</summary>
    private const int ImageTokenEstimate = 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>已经进入 KV cache 的消息，用于下一轮只喂增量。</summary>
    private readonly List<ChatMessage> _fed = [];

    /// <summary>KV cache 里累计占用的 token 数（预填充 + 已生成）。</summary>
    private int _usedTokens;

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private MtmdWeights? _vision;
    private InteractiveExecutor? _executor;
    private ModelParams? _modelParams;
    private ModelDescriptor? _loaded;

    public string DisplayName => _loaded is null
        ? "未加载模型"
        : $"{_loaded.DisplayName} · {_loaded.Quantization}";

    public ModelDescriptor? Loaded => _loaded;

    public bool IsLoaded => _executor is not null;

    public int ContextSize => (int)(_modelParams?.ContextSize ?? 0);

    public int ContextTokensUsed => _usedTokens;

    public int CountTokens(string text) =>
        _context is null ? TokenEstimator.Estimate(text) : SafeCountTokens(_context, text);

    public async Task LoadAsync(
        ModelDescriptor model,
        ModelLoadOptions options,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            UnloadCore();

            if (!File.Exists(model.Path))
            {
                throw new ModelLoadException($"模型文件不存在：{model.Path}");
            }

            AppLog.Info(
                $"开始加载 {model.DisplayName}（{model.Quantization}）· 后端 {options.Backend} · "
                + $"{options.GpuLayerCount} 层上 GPU · 上下文 {options.ContextSize}");

            LlamaBackend.Configure(options.Backend);

            var modelParams = new ModelParams(model.Path)
            {
                ContextSize = (uint)Math.Max(256, options.ContextSize),
                GpuLayerCount = options.GpuLayerCount,
                BatchSize = (uint)Math.Max(32, options.BatchSize),
                UBatchSize = (uint)Math.Max(32, options.BatchSize / 2),
            };

            if (options.Threads > 0)
            {
                modelParams.Threads = options.Threads;
            }

            if (MapKvType(options.KvCacheType) is { } kvType)
            {
                modelParams.TypeK = kvType;
                modelParams.TypeV = kvType;
            }

            LLamaWeights weights;
            try
            {
                weights = await Task
                    .Run(() => LLamaWeights.LoadFromFileAsync(modelParams, cancellationToken, progress), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw TranslateLoadError(ex, options.Backend, model);
            }

            _weights = weights;
            _modelParams = modelParams;
            _context = weights.CreateContext(modelParams, NullLogger.Instance);

            if (options.LoadVisionProjector && !string.IsNullOrEmpty(model.VisionProjectorPath))
            {
                if (!File.Exists(model.VisionProjectorPath))
                {
                    throw new ModelLoadException($"视觉投影文件不存在：{model.VisionProjectorPath}");
                }

                try
                {
                    _vision = MtmdWeights.LoadFromFile(
                        model.VisionProjectorPath,
                        weights,
                        MtmdContextParams.Default());

                    if (!_vision.SupportsVision)
                    {
                        throw new ModelLoadException(
                            $"该 mmproj 不是视觉投影：{Path.GetFileName(model.VisionProjectorPath)}");
                    }
                }
                catch (Exception ex) when (ex is not ModelLoadException)
                {
                    throw new ModelLoadException($"加载视觉投影失败：{ex.Message}", ex);
                }
            }

            _executor = CreateExecutor(_context);
            _fed.Clear();
            _loaded = model;
            progress?.Report(1f);

            AppLog.Info(
                $"{model.DisplayName} 加载完成，用时 {stopwatch.Elapsed.TotalSeconds:0.0}s，"
                + $"上下文 {ContextSize}，视觉投影 {(_vision is null ? "未启用" : "已启用")}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"加载 {model.DisplayName} 失败", ex);
            UnloadCore();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            UnloadCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_executor is null || _context is null)
        {
            throw new InvalidOperationException("模型尚未加载。");
        }

        // 历史被改写（比如切到「每次全新」）就重建上下文，保证 KV cache 与历史一致
        if (!IsPrefixOfFed(history))
        {
            ResetExecutorCore();
            AppLog.Info("历史与 KV cache 不一致，已重建上下文");
        }

        var reusedMessages = _fed.Count;
        var newMessages = history.Skip(_fed.Count).ToList();
        if (newMessages.Count == 0)
        {
            yield return ChatDelta.Done(new UsageInfo(0, 0));
            yield break;
        }

        var imageBytes = options.ImageBytes is { Length: > 0 } ? options.ImageBytes : null;
        if (imageBytes is not null && _vision is null)
        {
            throw new InvalidOperationException("当前模型没有启用视觉投影，无法发送图片。");
        }

        var prompt = ChatPromptBuilder.BuildPrompt(newMessages, withMedia: imageBytes is not null);
        var promptTokens = SafeCountTokens(_context, prompt);
        if (imageBytes is not null)
        {
            promptTokens += ImageTokenEstimate;
        }

        AppLog.Info(
            $"发送请求：新增 {newMessages.Count} 条消息，预填充 {promptTokens} token（KV 复用 {reusedMessages} 条）"
            + (imageBytes is not null ? $"，附带图片 {imageBytes.Length / 1024} KB" : ""));

        var inferStopwatch = Stopwatch.StartNew();

        // 预填充完剩下的空间才决定能生成多少：照搬 MaxTokens 会在生成中途撞上下文上限硬报错
        var room = Math.Max(0, ContextSize - _usedTokens - promptTokens);
        var maxTokens = options.MaxTokens > 0 ? options.MaxTokens : -1;
        if (room <= 64)
        {
            AppLog.Warn($"上下文只剩 {room} token，本次最多生成 8 token（可清空会话或调大上下文）");
            maxTokens = 8;
        }
        else if (maxTokens < 0 || maxTokens > room - 32)
        {
            maxTokens = room - 32;
            AppLog.Info($"按剩余上下文把生成长度压到 {maxTokens} token");
        }

        var inference = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = AntiPrompts,
            DecodeSpecialTokens = true,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = (float)options.Temperature,
                TopP = (float)options.TopP,
                TopK = options.TopK,
                RepeatPenalty = (float)options.RepeatPenalty,
            },
        };

        var executor = _executor;
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // 原生推理放到后台：预填充阶段在 UI 线程同步执行会直接卡住界面
        _ = Task.Run(
            async () =>
            {
                // 图片必须在 tokenize（InferAsync 内部）之前挂起，生成结束后再清理
                SafeMtmdEmbed? media = null;
                try
                {
                    if (imageBytes is not null)
                    {
                        media = _vision!.LoadMedia(imageBytes);
                    }

                    await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken).ConfigureAwait(false))
                    {
                        await channel.Writer.WriteAsync(piece, cancellationToken).ConfigureAwait(false);
                    }

                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    AppLog.Error("推理过程中出错", ex);
                    channel.Writer.TryComplete(ex);
                }
                finally
                {
                    if (media is not null)
                    {
                        _vision?.ClearMedia();
                        media.Dispose();
                    }
                }
            },
            CancellationToken.None);

        var answer = new StringBuilder();
        try
        {
            await foreach (var piece in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (piece.Length == 0)
                {
                    continue;
                }

                answer.Append(piece);
                yield return new ChatDelta(piece, false);
            }
        }
        finally
        {
            // 停到一半也要记账，否则下一轮 diff 会从头重建
            _fed.AddRange(newMessages);
            _fed.Add(new ChatMessage(ChatRole.Assistant, answer.ToString()));
            var generated = SafeCountTokens(_context, answer.ToString());
            _usedTokens += promptTokens + generated;

            var seconds = inferStopwatch.Elapsed.TotalSeconds;
            AppLog.Info(
                $"本轮结束：生成 {generated} token，用时 {seconds:0.0}s"
                + (seconds > 0 && generated > 0 ? $"，{generated / seconds:0.0} tok/s" : ""));
        }

        yield return ChatDelta.Done(
            new UsageInfo(promptTokens, SafeCountTokens(_context, answer.ToString())));
    }

    public async ValueTask DisposeAsync()
    {
        await UnloadAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private InteractiveExecutor CreateExecutor(LLamaContext context) => _vision is null
        ? new InteractiveExecutor(context, NullLogger.Instance)
        : new InteractiveExecutor(context, _vision, NullLogger.Instance);

    private void ResetExecutorCore()
    {
        if (_weights is null || _modelParams is null)
        {
            return;
        }

        var context = _weights.CreateContext(_modelParams, NullLogger.Instance);
        _context?.Dispose();
        _context = context;
        _executor = CreateExecutor(context);
        _fed.Clear();
        _usedTokens = 0;
    }

    private bool IsPrefixOfFed(IReadOnlyList<ChatMessage> history)
    {
        if (history.Count < _fed.Count)
        {
            return false;
        }

        for (var i = 0; i < _fed.Count; i++)
        {
            if (history[i] != _fed[i])
            {
                return false;
            }
        }

        return true;
    }

    private void UnloadCore()
    {
        _fed.Clear();
        _usedTokens = 0;
        _executor = null;
        _vision?.Dispose();
        _vision = null;
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
        _modelParams = null;
        _loaded = null;
    }

    private static int SafeCountTokens(LLamaContext context, string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        try
        {
            return context.Tokenize(text, addBos: false, special: true).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static GGMLType? MapKvType(string kvCacheType) => kvCacheType.ToLowerInvariant() switch
    {
        "f16" or "fp16" => GGMLType.GGML_TYPE_F16,
        "q8_0" => GGMLType.GGML_TYPE_Q8_0,
        "q4_0" => GGMLType.GGML_TYPE_Q4_0,
        _ => null,
    };

    private static Exception TranslateLoadError(Exception ex, BackendKind backend, ModelDescriptor model)
    {
        var backendName = backend switch
        {
            BackendKind.Cuda12 => "CUDA12",
            BackendKind.Vulkan => "Vulkan",
            _ => "CPU",
        };

        if (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            return new BackendUnavailableException(
                $"{backendName} 后端初始化失败：{LlamaBackend.LastError ?? ex.Message}。请换一个后端后重试（切换需要重启应用）。",
                ex);
        }

        if (ex is OutOfMemoryException || ex.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelLoadException(
                $"显存或内存不足，加载 {model.DisplayName} 失败。可以减少 GPU 层数、降低上下文长度，或换更小的量化。",
                ex);
        }

        return new ModelLoadException($"加载模型失败：{ex.Message}", ex);
    }
}
