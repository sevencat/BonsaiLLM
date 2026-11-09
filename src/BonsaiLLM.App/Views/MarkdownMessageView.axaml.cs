using Avalonia.Controls;
using BonsaiLLM.App.ViewModels;
using MarkView.Avalonia;
using MarkView.Avalonia.Math;
using MarkView.Avalonia.Mermaid;

namespace BonsaiLLM.App.Views;

/// <summary>
/// 把 ViewModel 的流式事件接到 MarkdownViewer.BeginStreaming()，
/// 避免每来一个 token 就整篇重渲染。
/// 注意：容器生成可能晚于首个 token，所以 delta 到达时若还没有会话，
/// 就用当前全文补一次，避免开头几个字丢失。
/// </summary>
public partial class MarkdownMessageView : UserControl
{
    private readonly MarkdownViewer? _viewer;
    private ChatMessageViewModel? _vm;
    private MarkdownStreamingSession? _session;

    public MarkdownMessageView()
    {
        InitializeComponent();
        _viewer = this.FindControl<MarkdownViewer>("PART_Viewer");

        // 必须用 Use…()：AddMath() 只注册渲染器，不加语法
        _viewer?.UseMath();
        _viewer?.UseMermaid();

        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Attach();
    }

    private void Attach()
    {
        if (DataContext is not ChatMessageViewModel vm || ReferenceEquals(vm, _vm))
        {
            return;
        }

        Detach();

        _vm = vm;

        if (_viewer is null)
        {
            return;
        }

        _viewer.Markdown = _vm.Text;

        _vm.StreamStarted += OnStreamStarted;
        _vm.StreamDelta += OnStreamDelta;
        _vm.StreamCompleted += OnStreamCompleted;
    }

    private void Detach()
    {
        _session?.Dispose();
        _session = null;

        if (_vm is null)
        {
            return;
        }

        _vm.StreamStarted -= OnStreamStarted;
        _vm.StreamDelta -= OnStreamDelta;
        _vm.StreamCompleted -= OnStreamCompleted;
        _vm = null;
    }

    private void OnStreamStarted()
    {
        if (_viewer is null)
        {
            return;
        }

        _viewer.Markdown = string.Empty;
        _session = _viewer.BeginStreaming();
    }

    private void OnStreamDelta(string delta)
    {
        if (_viewer is null || _vm is null)
        {
            return;
        }

        if (_session is null)
        {
            // 订阅晚了：用已累计的全文一次性补齐
            _viewer.Markdown = string.Empty;
            _session = _viewer.BeginStreaming();
            _session.Append(_vm.Text);
            return;
        }

        _session.Append(delta);
    }

    private void OnStreamCompleted()
    {
        _session?.Dispose();
        _session = null;
    }
}
