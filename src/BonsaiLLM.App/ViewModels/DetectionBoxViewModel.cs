using BonsaiLLM.Core.Detection;

namespace BonsaiLLM.App.ViewModels;

/// <summary>一个检测框的展示数据，坐标保持 0~1，由视图乘上图片尺寸摆放。</summary>
public sealed class DetectionBoxViewModel
{
    public DetectionBoxViewModel(DetectionBox box, string? label, int index)
    {
        Box = box;
        Label = string.IsNullOrWhiteSpace(label) ? $"#{index}" : label;
    }

    public DetectionBox Box { get; }

    public string Label { get; }

    public double X1 => Box.X1;

    public double Y1 => Box.Y1;

    public double X2 => Box.X2;

    public double Y2 => Box.Y2;
}
