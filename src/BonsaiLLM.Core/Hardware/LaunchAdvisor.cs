using BonsaiLLM.Core.Models;
using BonsaiLLM.Core.Settings;

namespace BonsaiLLM.Core.Hardware;

public sealed record LaunchRecommendation(int GpuLayers, int ContextSize, string Reason);

/// <summary>按显存与模型规模给出 gpuLayers / contextSize 推荐值，用户可在设置里覆盖。</summary>
public static class LaunchAdvisor
{
    private const int MaxContextSize = 32768;
    private const int MinContextSize = 2048;
    private const double ComputeOverheadRatio = 0.08;

    public static LaunchRecommendation Recommend(ModelDescriptor model, GpuInfo gpu, ModelDefaults defaults)
    {
        var weights = model.TotalSizeBytes;
        var layers = model.LayerCount;
        var nativeContext = model.NativeContextLength > 0 ? model.NativeContextLength : defaults.ContextSize;

        if (weights <= 0 || layers <= 0)
        {
            return new LaunchRecommendation(
                defaults.GpuLayers,
                defaults.ContextSize,
                "模型元数据不完整，沿用全局默认值");
        }

        var free = gpu.FreeMemoryBytes > 0 ? gpu.FreeMemoryBytes : gpu.TotalMemoryBytes;
        if (gpu.IsEstimate || free <= 0)
        {
            return new LaunchRecommendation(
                defaults.GpuLayers,
                Math.Min(nativeContext, defaults.ContextSize),
                $"未能读到真实显存，按 {gpu.TotalMemoryBytes / (1024.0 * 1024 * 1024):0.#} GB 估算");
        }

        var perLayerWeights = weights / (double)layers;
        var kvBytesPerToken = KvBytesPerToken(model);

        // 先试「全部层 + 默认上下文」
        var preferredContext = Math.Min(nativeContext, Math.Min(defaults.ContextSize, MaxContextSize));
        var allLayersCost = weights * (1 + ComputeOverheadRatio) + kvBytesPerToken * preferredContext;
        if (allLayersCost <= free)
        {
            return new LaunchRecommendation(
                layers,
                Math.Max(preferredContext, MinContextSize),
                $"显存 {FormatGb(free)} 可用，可全部放入显存");
        }

        // 放不下：先压上下文，再压层数
        var weightsCost = weights * (1 + ComputeOverheadRatio);
        var availableForKv = free - weightsCost;

        if (availableForKv <= 0)
        {
            return new LaunchRecommendation(
                0,
                MinContextSize,
                $"显存 {FormatGb(free)} 小于权重 {FormatGb(weightsCost)}，建议改用 CPU 或更小的量化");
        }

        var context = (int)(availableForKv / kvBytesPerToken);
        context = Math.Clamp(context / 512 * 512, MinContextSize, Math.Min(nativeContext, MaxContextSize));

        // 用压缩后的上下文重新算层数
        var kvAtContext = kvBytesPerToken * context;
        var perLayerCost = perLayerWeights + kvAtContext / layers;
        var fittingLayers = (int)((free - kvAtContext) / perLayerCost);
        fittingLayers = Math.Clamp(fittingLayers, 0, layers);

        var layersText = fittingLayers >= layers ? "全部层" : $"{fittingLayers}/{layers} 层";
        return new LaunchRecommendation(
            fittingLayers,
            context,
            $"显存 {FormatGb(free)} 可用，建议 {layersText} + {context} 上下文");
    }

    private static double KvBytesPerToken(ModelDescriptor model)
    {
        var heads = Math.Max(1, model.HeadCount);
        var kvHeads = model.HeadCountKv > 0 ? model.HeadCountKv : heads;
        var headDim = model.EmbeddingLength > 0 ? model.EmbeddingLength / (double)heads : 128;
        return 2.0 * Math.Max(1, model.LayerCount) * kvHeads * headDim * 2; // f16
    }

    private static string FormatGb(double bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
}
