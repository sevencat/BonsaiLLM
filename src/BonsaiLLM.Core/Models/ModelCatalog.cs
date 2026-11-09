using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using BonsaiLLM.Core.Diagnostics;
using BonsaiLLM.Core.Gguf;

namespace BonsaiLLM.Core.Models;

public interface IModelCatalog
{
    Task<ModelScanResult> ScanAsync(
        IReadOnlyList<string> directories,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>递归扫描 .gguf，读头部元数据，并把 mmproj 视觉投影配给同名前缀的主模型。</summary>
public sealed partial class ModelCatalog : IModelCatalog
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public async Task<ModelScanResult> ScanAsync(
        IReadOnlyList<string> directories,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = new List<string>();
        var missing = 0;
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                // 目录被删/挪盘很常见，用户需要知道扫描为什么少了模型
                missing++;
                AppLog.Warn($"目录不存在，已跳过：{directory}");
                continue;
            }

            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*.gguf", EnumerationOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个目录不可读不影响其它目录
                AppLog.Warn($"目录不可读，已跳过：{directory}（{ex.Message}）");
            }
        }

        AppLog.Info(
            $"开始扫描 {directories.Count} 个目录（{missing} 个不存在），找到 {files.Count} 个 .gguf");

        var visionFiles = files.Where(IsVisionFile).ToList();
        var mainFiles = files.Where(f => !IsVisionFile(f)).ToList();

        var descriptors = new ConcurrentBag<ModelDescriptor>();
        var errors = new ConcurrentBag<ScanError>();
        var visionSizes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var total = mainFiles.Count + visionFiles.Count;
        var scanned = 0;

        await Parallel.ForEachAsync(
            visionFiles,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            (path, _) =>
            {
                visionSizes[path] = SafeFileSize(path);
                Report(progress, Interlocked.Increment(ref scanned), total, path);
                return ValueTask.CompletedTask;
            });

        await Parallel.ForEachAsync(
            mainFiles,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            (path, _) =>
            {
                try
                {
                    descriptors.Add(ReadDescriptor(path));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(new ScanError(path, ex.Message));
                }

                Report(progress, Interlocked.Increment(ref scanned), total, path);
                return ValueTask.CompletedTask;
            });

        var models = descriptors.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var paired = new HashSet<string>(
            models.Where(m => m.VisionProjectorPath is not null).Select(m => m.VisionProjectorPath!),
            StringComparer.OrdinalIgnoreCase);

        // 同一前缀 + 同目录，才算配对成功
        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            if (model.VisionProjectorPath is not null)
            {
                continue;
            }

            var modelDir = Path.GetDirectoryName(model.Path);
            var prefix = StripVariants(Path.GetFileNameWithoutExtension(model.FileName));

            // 同目录下所有同前缀的主模型（不同量化）共用同一个投影文件
            var match = visionSizes.Keys.FirstOrDefault(path =>
                string.Equals(Path.GetDirectoryName(path), modelDir, StringComparison.OrdinalIgnoreCase)
                && StripVariants(Path.GetFileNameWithoutExtension(path)) == prefix);

            if (match is null)
            {
                continue;
            }

            paired.Add(match);
            models[i] = model with
            {
                VisionProjectorPath = match,
                VisionProjectorSizeBytes = visionSizes[match],
            };
        }

        var unpaired = visionSizes
            .Where(pair => !paired.Contains(pair.Key))
            .Select(pair => new VisionFileInfo(pair.Key, pair.Value))
            .OrderBy(v => v.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var message = $"扫描完成：{models.Count} 个模型、{unpaired.Count} 个未配对 mmproj、{errors.Count} 个文件读取失败，"
            + $"用时 {stopwatch.ElapsedMilliseconds} ms";

        if (errors.Count > 0)
        {
            AppLog.Warn(message);
        }
        else
        {
            AppLog.Info(message);
        }

        foreach (var error in errors.Take(5))
        {
            AppLog.Warn($"读取失败（{Path.GetFileName(error.Path)}）：{error.Message}");
        }

        return new ModelScanResult(models, unpaired, errors.ToList());
    }

    /// <summary>读单个文件的描述，用于自动加载上次模型时不必全盘扫描。</summary>
    public static ModelDescriptor ReadDescriptor(string path)
    {
        var fileName = Path.GetFileName(path);
        var size = SafeFileSize(path);

        try
        {
            var metadata = GgufReader.Read(path);
            var architecture = metadata.GetString("general.architecture") ?? "";

            if (IsVisionFile(path) || architecture.Equals("clip", StringComparison.OrdinalIgnoreCase))
            {
                throw new GgufFormatException("这是视觉投影文件，不是主模型");
            }

            return new ModelDescriptor
            {
                Path = path,
                FileName = fileName,
                DisplayName = metadata.GetString("general.name") ?? Path.GetFileNameWithoutExtension(fileName),
                Architecture = architecture,
                Quantization = DetectQuantization(fileName),
                FileSizeBytes = size,
                NativeContextLength = metadata.GetInt32First($"{architecture}.context_length") ?? 0,
                LayerCount = metadata.GetInt32First($"{architecture}.block_count") ?? 0,
                HeadCount = metadata.GetInt32First($"{architecture}.attention.head_count") ?? 0,
                HeadCountKv = metadata.GetInt32First($"{architecture}.attention.head_count_kv") ?? 0,
                EmbeddingLength = metadata.GetInt32First($"{architecture}.embedding_length") ?? 0,
                ChatTemplate = metadata.GetString("tokenizer.chat_template"),
                MetadataAvailable = true,
            };
        }
        catch (Exception ex) when (ex is IOException or GgufFormatException or UnauthorizedAccessException)
        {
            return new ModelDescriptor
            {
                Path = path,
                FileName = fileName,
                DisplayName = Path.GetFileNameWithoutExtension(fileName),
                Quantization = DetectQuantization(fileName),
                FileSizeBytes = size,
                MetadataAvailable = false,
            };
        }
    }

    private static void Report(IProgress<ScanProgress>? progress, int scanned, int total, string path) =>
        progress?.Report(new ScanProgress(scanned, total, path));

    private static long SafeFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static bool IsVisionFile(string path) =>
        Path.GetFileName(path).Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    public static string DetectQuantization(string fileName)
    {
        var match = QuantizationPattern().Match(fileName);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
    }

    /// <summary>去掉量化后缀与 mmproj 标记，得到用于配对的基名。</summary>
    private static string StripVariants(string name)
    {
        var result = QuantizationPattern().Replace(name, "");
        result = MmprojPattern().Replace(result, "");
        return result.Trim(' ', '.', '_', '-').ToLowerInvariant();
    }

    [GeneratedRegex(@"(?i)(?:^|[._-])((?:IQ|Q)\d+(?:_[A-Z0-9]+)*|BF16|F16|F32)")]
    private static partial Regex QuantizationPattern();

    [GeneratedRegex(@"(?i)[._-]?mmproj(?:[._-](?:f16|f32|bf16))?")]
    private static partial Regex MmprojPattern();
}
