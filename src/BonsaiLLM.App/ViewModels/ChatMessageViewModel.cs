using BonsaiLLM.Core.Chat;
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

    /// <summary>MarkdownViewer 的流式会话由 View 订阅这些事件驱动，避免整篇重渲染。</summary>
    public event Action? StreamStarted;

    public event Action<string>? StreamDelta;

    public event Action? StreamCompleted;

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
