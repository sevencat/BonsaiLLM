using BonsaiLLM.App.ViewModels;
using BonsaiLLM.Core.Chat;

namespace BonsaiLLM.App;

/// <summary>手写组合根，不引 DI 容器。</summary>
public sealed class AppServices
{
    public static AppServices Current { get; private set; } = null!;

    private AppServices()
    {
        ChatEngine = new DemoChatEngine();
        MainViewModel = new MainWindowViewModel(ChatEngine);
    }

    public IChatEngine ChatEngine { get; }

    public MainWindowViewModel MainViewModel { get; }

    public static AppServices Initialize() => Current = new AppServices();
}
