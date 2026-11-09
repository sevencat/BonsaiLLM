using Avalonia.Media.Imaging;
using BonsaiLLM.Core.Chat;
using BonsaiLLM.Core.Detection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BonsaiLLM.App.ViewModels;

public partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatRole role, string text = "")
    {
        Role = role;
        _text = text;
    }

    public ChatRole Role { get; }

    public bool IsUser => Role == ChatRole.User;

    public bool IsAssistant => Role == ChatRole.Assistant;

    [ObservableProperty]
    private string _text;

    /// <summary>随消息附带的图片（用户上传的图或检测的源图）。</summary>
    public Bitmap? Image { get; private set; }

    /// <summary>原始 JPEG 字节，导出标注图时直接复用，避免再从 Bitmap 编码一次。</summary>
    public byte[]? ImageBytes { get; private set; }

    public bool HasImage => Image is not null;

    /// <summary>用户消息才显示缩略图；检测消息的图交给叠加控件画。</summary>
    public bool ShowUserImage => IsUser && Image is not null;

    /// <summary>这条助手回复是特化检测结果，改用坐标叠加渲染。</summary>
    public bool IsDetection { get; private set; }

    /// <summary>检测结果的框，坐标 0~1。</summary>
    public IReadOnlyList<DetectionBoxViewModel> Boxes { get; private set; } = [];

    public bool HasBoxes => Boxes.Count > 0;

    public string DetectionLabel { get; private set; } = string.Empty;

    /// <summary>纯文本摘要，给没有图形的场合兜底（也给复制按钮用）。</summary>
    public string DetectionSummary { get; private set; } = string.Empty;

    public bool ShowDetection => IsDetection;

    public bool ShowMarkdown => !IsUser && !IsDetection;

    /// <summary>MarkdownViewer 的流式会话由 View 订阅这些事件驱动，避免整篇重渲染。</summary>
    public event Action? StreamStarted;

    public event Action<string>? StreamDelta;

    public event Action? StreamCompleted;

    /// <summary>异步补上图片（会话恢复时延迟解码，避免一次性占用太多内存）。</summary>
    public void AttachImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            Image = new Bitmap(stream);
            ImageBytes = bytes;
            OnPropertyChanged(nameof(Image));
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(ShowUserImage));
        }
        catch (Exception)
        {
            // 图片损坏就当纯文本，别让整个会话打不开
        }
    }

    public void ApplyDetection(DetectionResult result)
    {
        IsDetection = true;
        Boxes = result.Boxes
            .Select((box, index) => new DetectionBoxViewModel(box, result.Label, index + 1))
            .ToList();
        DetectionLabel = result.Label ?? string.Empty;
        DetectionSummary = result.HasBoxes
            ? $"检测到 {Boxes.Count} 个目标" + (DetectionLabel.Length > 0 ? $"：{DetectionLabel}" : "")
            : "未检测到目标";

        OnPropertyChanged(nameof(IsDetection));
        OnPropertyChanged(nameof(Boxes));
        OnPropertyChanged(nameof(HasBoxes));
        OnPropertyChanged(nameof(DetectionLabel));
        OnPropertyChanged(nameof(DetectionSummary));
        OnPropertyChanged(nameof(ShowDetection));
        OnPropertyChanged(nameof(ShowMarkdown));
    }

    internal void BeginStream()
    {
        Text = string.Empty;
        StreamStarted?.Invoke();
    }

    internal void Append(string delta)
    {
        Text += delta;
        StreamDelta?.Invoke(delta);
    }

    internal void Complete() => StreamCompleted?.Invoke();
}
