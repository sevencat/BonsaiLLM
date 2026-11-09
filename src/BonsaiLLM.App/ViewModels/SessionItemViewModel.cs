using BonsaiLLM.Core.Sessions;
using BonsaiLLM.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BonsaiLLM.App.ViewModels;

/// <summary>左侧会话列表的一项。</summary>
public partial class SessionItemViewModel : ObservableObject
{
    public SessionItemViewModel(ChatSession session) => Session = session;

    public ChatSession Session { get; }

    public string Id => Session.Id;

    public string Title => string.IsNullOrWhiteSpace(Session.Title) ? "新会话" : Session.Title;

    public string Subtitle
    {
        get
        {
            var count = Session.Messages.Count;
            var time = Session.UpdatedAt.LocalDateTime;
            var stamp = time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("MM-dd HH:mm");
            return count == 0 ? stamp : $"{stamp} · {count} 条";
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }
}

public sealed record ContextModeOption(ContextMode Mode, string Name, string Note);
