using System.Collections.ObjectModel;
using System.Text;
using BonsaiLLM.Core.Chat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonsaiLLM.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IChatEngine _engine;
    private readonly List<ChatMessage> _history = [];
    private CancellationTokenSource? _cts;

    public MainWindowViewModel(IChatEngine engine)
    {
        _engine = engine;
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public string Header => $"BonsaiLLM · {_engine.DisplayName}";

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanSend() => !IsStreaming && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        InputText = string.Empty;
        _history.Add(new ChatMessage(ChatRole.User, text));
        Messages.Add(new ChatMessageViewModel(ChatRole.User, text));

        var assistant = new ChatMessageViewModel(ChatRole.Assistant);
        Messages.Add(assistant);

        _cts = new CancellationTokenSource();
        IsStreaming = true;
        var answer = new StringBuilder();

        assistant.BeginStream();
        try
        {
            await foreach (var delta in _engine.StreamAsync(_history, new ChatOptions(), _cts.Token))
            {
                if (delta.IsDone)
                {
                    break;
                }

                if (delta.Text.Length == 0)
                {
                    continue;
                }

                answer.Append(delta.Text);
                assistant.Append(delta.Text);
            }
        }
        catch (OperationCanceledException)
        {
            answer.Append("\n\n_（已停止）_");
        }
        catch (Exception ex)
        {
            answer.Append($"\n\n> 出错了：{ex.Message}");
        }
        finally
        {
            assistant.Complete();

            if (answer.Length > 0)
            {
                _history.Add(new ChatMessage(ChatRole.Assistant, answer.ToString()));
            }

            IsStreaming = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanStop() => IsStreaming;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    /// <summary>自检入口：启动后自动发一条，验证流式渲染链路。</summary>
    internal void StartSelfCheck()
    {
        InputText = "自检：渲染一段 Markdown";

        if (SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
    }
}
