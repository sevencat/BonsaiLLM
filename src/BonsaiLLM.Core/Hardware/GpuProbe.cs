using System.Diagnostics;
using System.Globalization;

namespace BonsaiLLM.Core.Hardware;

public sealed record GpuInfo(
    string Name,
    long TotalMemoryBytes,
    long UsedMemoryBytes,
    string Source,
    bool IsEstimate)
{
    public long FreeMemoryBytes => Math.Max(0, TotalMemoryBytes - UsedMemoryBytes);
}

/// <summary>
/// 显卡/显存探测：优先 nvidia-smi，失败按 8 GB 保守假设。结果进程内缓存。
/// 说明：plan 里写的 Vulkan 查询没有实现，非 NVIDIA 机器走保守估算，见 misc.md。
/// </summary>
public static class GpuProbe
{
    private const long FallbackTotalBytes = 8L * 1024 * 1024 * 1024;

    private static readonly object Gate = new();
    private static GpuInfo? _cached;

    public static GpuInfo Detect()
    {
        lock (Gate)
        {
            return _cached ??= DetectCore();
        }
    }

    private static GpuInfo DetectCore()
    {
        var fromSmi = TryDetectWithNvidiaSmi();
        return fromSmi ?? new GpuInfo("未知显卡（未检测到 NVIDIA 驱动）", FallbackTotalBytes, 0, "保守估算", true);
    }

    private static GpuInfo? TryDetectWithNvidiaSmi()
    {
        try
        {
            var startInfo = new ProcessStartInfo("nvidia-smi")
            {
                Arguments = "--query-gpu=name,memory.total,memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000) || process.ExitCode != 0)
            {
                return null;
            }

            var firstLine = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (firstLine is null)
            {
                return null;
            }

            var parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMiB)
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedMiB))
            {
                return null;
            }

            return new GpuInfo(parts[0], totalMiB * 1024 * 1024, usedMiB * 1024 * 1024, "nvidia-smi", false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
