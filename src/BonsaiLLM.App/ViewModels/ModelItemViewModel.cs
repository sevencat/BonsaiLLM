using BonsaiLLM.Core.Models;
using BonsaiLLM.Core.Settings;

namespace BonsaiLLM.App.ViewModels;

/// <summary>模型下拉里的一项。</summary>
public sealed class ModelItemViewModel(ModelDescriptor descriptor)
{
    public ModelDescriptor Descriptor { get; } = descriptor;

    public string Title => Descriptor.DisplayName;

    /// <summary>UIA / 读屏软件会直接读这个值，别让它显示类型名。</summary>
    public override string ToString() => Title;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(Descriptor.Quantization))
            {
                parts.Add(Descriptor.Quantization);
            }

            parts.Add(Descriptor.SizeText);

            if (Descriptor.HasVision)
            {
                parts.Add("视觉");
            }

            if (!Descriptor.MetadataAvailable)
            {
                parts.Add("元数据缺失");
            }

            return string.Join(" · ", parts);
        }
    }

    public bool IsSamePath(string path) =>
        string.Equals(Descriptor.Path, path, StringComparison.OrdinalIgnoreCase);
}

public sealed record BackendOption(BackendKind Kind, string Name, string Note);
