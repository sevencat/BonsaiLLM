namespace BonsaiLLM.Core.Models;

public sealed record ModelScanResult(
    IReadOnlyList<ModelDescriptor> Models,
    IReadOnlyList<VisionFileInfo> UnpairedVisionFiles,
    IReadOnlyList<ScanError> Errors);

public sealed record VisionFileInfo(string Path, long FileSizeBytes);

public sealed record ScanError(string Path, string Message);

public sealed record ScanProgress(int ScannedFiles, int TotalFiles, string CurrentPath);
