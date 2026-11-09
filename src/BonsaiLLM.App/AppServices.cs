using BonsaiLLM.App.ViewModels;
using BonsaiLLM.Core;
using BonsaiLLM.Core.Chat;
using BonsaiLLM.Core.Models;
using BonsaiLLM.Core.Sessions;
using BonsaiLLM.Core.Settings;
using BonsaiLLM.Llama;

namespace BonsaiLLM.App;

/// <summary>手写组合根，不引 DI 容器。</summary>
public sealed class AppServices
{
    public static AppServices Current { get; private set; } = null!;

    private AppServices()
    {
        // 便携模式：数据放在 exe 同目录；旧版 %AppData% 的数据搬一次
        AppPaths.MigrateFromLegacyIfNeeded();

        SettingsStore = new SettingsStore();
        SessionStore = new SessionStore();
        Catalog = new ModelCatalog();
        ChatEngine = new LlamaChatEngine();
        MainViewModel = new MainWindowViewModel(ChatEngine, Catalog, SettingsStore, SessionStore);
    }

    public SettingsStore SettingsStore { get; }

    public SessionStore SessionStore { get; }

    public IModelCatalog Catalog { get; }

    public IChatEngine ChatEngine { get; }

    public MainWindowViewModel MainViewModel { get; }

    public static AppServices Initialize() => Current = new AppServices();
}
