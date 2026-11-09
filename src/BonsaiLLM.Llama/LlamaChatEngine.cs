using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using BonsaiLLM.Core.Chat;
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
        try
        {
            UnloadCore();

            if (!File.Exists(model.Path))
            {
                throw new ModelLoadException($"模型文件不存在：{model.Path}");
            }

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
        }
        catch
        {
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
        }

        var newMessages = history.Skip(_fed.Count).ToList();
        if (newMessages.Count == 0)
        {
            yield return ChatDelta.Done(new UsageInfo(0, 0));
            yield break;
        }

        var prompt = ChatPromptBuilder.BuildPrompt(newMessages);
        var promptTokens = SafeCountTokens(_context, prompt);

        var inference = new InferenceParams
        {
            MaxTokens = options.MaxTokens > 0 ? options.MaxTokens : -1,
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
                try
                {
                    await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken).ConfigureAwait(false))
                    {
                        await channel.Writer.WriteAsync(piece, cancellationToken).ConfigureAwait(false);
                    }

                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
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
            _usedTokens += promptTokens + SafeCountTokens(_context, answer.ToString());
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
