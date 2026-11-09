namespace BonsaiLLM.Core.Detection;

/// <summary>一次特化检测的解析结果。</summary>
public sealed record DetectionResult(
    IReadOnlyList<DetectionBox> Boxes,
    string? Label,
    string CleanedText,
    bool IsNone)
{
    public bool HasBoxes => Boxes.Count > 0;
}
