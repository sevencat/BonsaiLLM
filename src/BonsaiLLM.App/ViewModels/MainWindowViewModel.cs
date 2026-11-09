using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using BonsaiLLM.Core.Chat;
using BonsaiLLM.Core.Hardware;
using BonsaiLLM.Core.Models;
using BonsaiLLM.Core.Sessions;
using BonsaiLLM.Core.Settings;
using BonsaiLLM.Llama;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonsaiLLM.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const string DefaultSystemPrompt = "你是一个乐于助人的中文助手，回答尽量简洁准确。";
    private const int TemplateOverheadTokens = 128;
    private const int DefaultReserveTokens = 1024;

    private readonly IChatEngine _engine;
    private readonly DemoChatEngine _demoEngine = new();
    private readonly IModelCatalog _catalog;
    private readonly SettingsStore _settingsStore;
    private readonly SessionStore _sessionStore;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _scanCts;
    private bool _useDemo;
    private bool _initialized;
    private bool _suppressModelSelection;
    private bool _suppressSessionSwitch;
    private ChatSession? _session;

    public MainWindowViewModel(
        IChatEngine engine,
        IModelCatalog catalog,
        SettingsStore settingsStore,
        SessionStore sessionStore)
    {
        _engine = engine;
        _catalog = catalog;
        _settingsStore = settingsStore;
        _sessionStore = sessionStore;
        _settings = settingsStore.Load();

        BackendOptions =
        [
            new BackendOption(BackendKind.Cuda12, "CUDA 12", "NVIDIA 显卡，速度最快"),
            new BackendOption(BackendKind.Vulkan, "Vulkan", "AMD / Intel / 较新的 NVIDIA"),
            new BackendOption(BackendKind.Cpu, "CPU", "兼容性最好，速度最慢"),
        ];

        ContextModeOptions =
        [
            new ContextModeOption(ContextMode.Carry, "携带历史", "把之前的问答一起发给模型"),
            new ContextModeOption(ContextMode.Fresh, "每次全新", "只发当前问题，界面仍保留历史"),
        ];

        SelectedBackend = BackendOptions.FirstOrDefault(option => option.Kind == _settings.Backend);
        SelectedContextMode = ContextModeOptions.First(option => option.Mode == _settings.DefaultContextMode);
        _fontSize = _settings.FontSize;

        foreach (var directory in _settings.ModelDirectories)
        {
            ModelDirectories.Add(directory);
        }

        if (_settingsStore.LastError is { } error)
        {
            StatusText = error;
        }

        SettingsNotice = _settings.Backend is null ? "请先选择推理后端" : string.Empty;

        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowReadyHint));
            UpdateEmptyState();
        };

        UpdateEmptyState();
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public ObservableCollection<ModelItemViewModel> Models { get; } = [];

    public ObservableCollection<string> ModelDirectories { get; } = [];

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    public IReadOnlyList<BackendOption> BackendOptions { get; }

    public IReadOnlyList<ContextModeOption> ContextModeOptions { get; }

    /// <summary>由窗口注入的目录选择器，避免 ViewModel 直接依赖 StorageProvider。</summary>
    public Func<Task<string?>>? DirectoryPicker { get; set; }

    /// <summary>流式内容增长时触发，供窗口把滚动条跟到底部。</summary>
    public event EventHandler? StreamingContentUpdated;

    /// <summary>请求进入重命名编辑态时触发，供窗口把焦点移到输入框。</summary>
    public event EventHandler? RenameRequested;

    public string Header => $"BonsaiLLM · {ActiveEngine.DisplayName}";

    public string SettingsFilePath => $"设置文件：{_settingsStore.FilePath}";

    public string SessionsDirectory => _sessionStore.Directory;

    public bool CanChat => !IsBusy && (IsDemoMode || _engine.IsLoaded);

    /// <summary>模型就绪但还没开始对话时，中间给一句提示。</summary>
    public bool ShowReadyHint => !IsEmptyVisible && Messages.Count == 0 && CanChat;

    public bool IsDemoMode => _useDemo;

    public bool IsSessionEmpty => Messages.Count == 0;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _statsText = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _loadProgress;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private string _settingsNotice = string.Empty;

    [ObservableProperty]
    private string _gpuSummary = "正在读取显卡信息…";

    [ObservableProperty]
    private string _emptyTitle = "还没有模型目录";

    [ObservableProperty]
    private string _emptyHint = "添加一个存放 .gguf 文件的文件夹，BonsaiLLM 会递归扫描。";

    [ObservableProperty]
    private bool _isEmptyVisible = true;

    [ObservableProperty]
    private BackendOption? _selectedBackend;

    [ObservableProperty]
    private ModelItemViewModel? _selectedModel;

    [ObservableProperty]
    private ContextModeOption? _selectedContextMode;

    [ObservableProperty]
    private SessionItemViewModel? _currentSession;

    [ObservableProperty]
    private string _systemPrompt = DefaultSystemPrompt;

    [ObservableProperty]
    private string _sessionTitleDraft = string.Empty;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _profileGpuLayersText = "99";

    [ObservableProperty]
    private string _profileContextSizeText = "8192";

    [ObservableProperty]
    private string _profileTemperatureText = "0.7";

    [ObservableProperty]
    private string _profileMaxTokensText = "-1";

    [ObservableProperty]
    private string _profileNotice = string.Empty;

    [ObservableProperty]
    private double _fontSize = 14;

    partial void OnFontSizeChanged(double value)
    {
        var size = (int)Math.Round(Math.Clamp(value, 12, 20));
        if (_settings.FontSize == size)
        {
            return;
        }

        _settings.FontSize = size;
        SaveSettings();
    }

    private IChatEngine ActiveEngine => _useDemo ? _demoEngine : _engine;

    private ChatSession? CurrentChat => _session;

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanChat));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChat));
        OnPropertyChanged(nameof(ShowReadyHint));
    }

    public bool HasModel => SelectedModel is not null;

    partial void OnSelectedModelChanged(ModelItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasModel));

        if (_suppressModelSelection || value is null)
        {
            return;
        }

        _ = LoadModelAsync(value);
    }

    partial void OnSelectedBackendChanged(BackendOption? value)
    {
        if (value is null)
        {
            return;
        }

        _settings.Backend = value.Kind;
        SaveSettings();

        SettingsNotice = _engine.IsLoaded || LlamaBackendIsLocked()
            ? "后端已切换，重启应用后生效"
            : $"已选择 {value.Name}，加载模型时生效";
    }

    partial void OnSelectedContextModeChanged(ContextModeOption? value)
    {
        if (value is null || _suppressContextModeChanges)
        {
            return;
        }

        _settings.DefaultContextMode = value.Mode;

        if (_session is not null && _session.ContextMode != value.Mode)
        {
            _session.ContextMode = value.Mode;
            PersistSession();
        }

        SaveSettings();
        StatusText = value.Mode == ContextMode.Fresh
            ? "本会话只发当前问题，界面仍保留历史"
            : "本会话携带历史";
    }

    partial void OnCurrentSessionChanged(SessionItemViewModel? value)
    {
        if (_suppressSessionSwitch || value is null)
        {
            return;
        }

        SwitchSession(value.Session);
    }

    partial void OnSystemPromptChanged(string value)
    {
        if (SelectedModel is null)
        {
            return;
        }

        var profile = GetOrCreateProfile(SelectedModel.Descriptor);
        if (string.Equals(profile.SystemPrompt, value, StringComparison.Ordinal))
        {
            return;
        }

        profile.SystemPrompt = value;
        SaveSettings();
    }

    /// <summary>启动流程：读完设置 → 恢复会话 → 扫描目录 → 自动加载上次模型。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        _ = Task.Run(() =>
        {
            var gpu = GpuProbe.Detect();
            GpuSummary = $"{gpu.Name} · 总显存 {FormatGb(gpu.TotalMemoryBytes)} · 可用 {FormatGb(gpu.FreeMemoryBytes)}"
                + (gpu.IsEstimate ? "（估算）" : "");
        });

        if (_settings.Backend is null)
        {
            IsSettingsOpen = true;
        }

        RestoreSessions();

        if (ModelDirectories.Count > 0)
        {
            await RescanAsync();
        }
        else
        {
            UpdateEmptyState();
        }

        if (_settings.LastModelPath.Length == 0)
        {
            return;
        }

        var last = Models.FirstOrDefault(model => model.IsSamePath(_settings.LastModelPath));
        if (last is null)
        {
            // 文件被删或目录被移除：清掉记录，不阻塞启动
            _settings.LastModelPath = string.Empty;
            SaveSettings();
            StatusText = "上次使用的模型已不存在，已清除记录";
            return;
        }

        if (_settings.Backend is null)
        {
            return;
        }

        _suppressModelSelection = true;
        SelectedModel = last;
        _suppressModelSelection = false;
        await LoadModelAsync(last);
    }

    private void RestoreSessions()
    {
        var stored = _sessionStore.LoadAll();
        if (_sessionStore.LastError is { } error)
        {
            StatusText = error;
        }

        Sessions.Clear();
        foreach (var session in stored)
        {
            Sessions.Add(new SessionItemViewModel(session));
        }

        if (Sessions.Count > 0)
        {
            _suppressSessionSwitch = true;
            CurrentSession = Sessions[0];
            _suppressSessionSwitch = false;
            SwitchSession(Sessions[0].Session);
        }
        else
        {
            CreateSession();
        }
    }

    private void SwitchSession(ChatSession session)
    {
        if (IsStreaming)
        {
            _cts?.Cancel();
        }

        _session = session;
        ReloadMessages();

        var option = ContextModeOptions.First(item => item.Mode == session.ContextMode);
        if (!ReferenceEquals(SelectedContextMode, option))
        {
            _suppressContextModeChanges = true;
            SelectedContextMode = option;
            _suppressContextModeChanges = false;
        }

        OnPropertyChanged(nameof(Header));
        SessionTitleDraft = CurrentSession?.Title ?? string.Empty;
        UpdateStats(null, null, null);
    }

    private bool _suppressContextModeChanges;

    private void ReloadMessages()
    {
        Messages.Clear();

        if (_session is null)
        {
            UpdateEmptyState();
            return;
        }

        foreach (var stored in _session.Messages)
        {
            Messages.Add(new ChatMessageViewModel(stored.Role, stored.Text));
        }

        UpdateEmptyState();
    }

    private SessionItemViewModel CreateSession()
    {
        var session = new ChatSession
        {
            ModelPath = SelectedModel?.Descriptor.Path ?? string.Empty,
            ModelName = SelectedModel?.Descriptor.DisplayName ?? string.Empty,
            ContextMode = _settings.DefaultContextMode,
        };

        _session = session;
        var item = new SessionItemViewModel(session);
        Sessions.Insert(0, item);

        _suppressSessionSwitch = true;
        CurrentSession = item;
        _suppressSessionSwitch = false;

        ReloadMessages();
        SessionTitleDraft = session.Title;
        UpdateStats(null, null, null);
        PersistSession();
        return item;
    }

    private bool CanSend() => CanChat && !IsStreaming && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!CanChat)
        {
            StatusText = "请先加载一个模型";
            return;
        }

        if (_session is null)
        {
            CreateSession();
        }

        InputText = string.Empty;
        _session!.Messages.Add(new StoredMessage { Role = ChatRole.User, Text = text });
        Messages.Add(new ChatMessageViewModel(ChatRole.User, text));

        if (_session.Messages.Count == 1)
        {
            _session.Title = MakeTitle(text);
        }

        await (_generation = StreamAssistantAsync());
    }

    /// <summary>当前正在跑的生成任务，供清空/删除会话时等它收尾。</summary>
    private Task? _generation;

    /// <summary>取消并等待生成任务收尾，避免它把回复写回已经清空/删除的会话。</summary>
    private async Task StopGenerationAsync()
    {
        if (!IsStreaming)
        {
            return;
        }

        _cts?.Cancel();
        if (_generation is { } task)
        {
            await task;
        }
    }

    /// <summary>在历史末尾生成一条助手回复（发送与重新生成共用）。</summary>
    private async Task StreamAssistantAsync()
    {
        // 生成过程中可能切换会话：固定住发起时的会话，回复只写回它，
        // 否则取消后收尾会把旧回复追加到新会话里。
        var session = _session;
        if (session is null)
        {
            return;
        }

        var assistant = new ChatMessageViewModel(ChatRole.Assistant);
        Messages.Add(assistant);

        _cts = new CancellationTokenSource();
        IsStreaming = true;
        var answer = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        double firstTokenMs = -1;
        UsageInfo? usage = null;
        var trimmed = 0;

        assistant.BeginStream();
        try
        {
            var history = BuildRequestHistory(session, out trimmed);
            await foreach (var delta in ActiveEngine.StreamAsync(history, BuildChatOptions(), _cts.Token))
            {
                if (delta.IsDone)
                {
                    usage = delta.Usage;
                    break;
                }

                if (delta.Text.Length == 0)
                {
                    continue;
                }

                if (firstTokenMs < 0)
                {
                    firstTokenMs = stopwatch.Elapsed.TotalMilliseconds;
                }

                answer.Append(delta.Text);
                assistant.Append(delta.Text);

                if (answer.Length % 32 == 0)
                {
                    StreamingContentUpdated?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
            assistant.Append("\n\n_（已停止）_");
        }
        catch (Exception ex)
        {
            assistant.Append($"\n\n> {DescribeError(ex)}");
        }
        finally
        {
            stopwatch.Stop();
            assistant.Complete();

            if (answer.Length > 0)
            {
                session.Messages.Add(new StoredMessage
                {
                    Role = ChatRole.Assistant,
                    Text = answer.ToString(),
                });
            }

            PersistSession(session);

            // 会话已被切走：只落盘，不覆盖当前会话的统计与列表项
            if (ReferenceEquals(session, _session))
            {
                RefreshSessionItem();
                UpdateStats(usage, stopwatch.Elapsed.TotalSeconds, firstTokenMs, trimmed);
                InputText = string.Empty;
            }

            IsStreaming = false;
            _cts.Dispose();
            _cts = null;
            StreamingContentUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool CanEditMessage => !IsStreaming;

    /// <summary>丢弃这条助手回复并重新生成（历史里最后一条必须是 user）。</summary>
    [RelayCommand]
    private async Task RegenerateAsync(ChatMessageViewModel? message)
    {
        if (IsStreaming || message is null || _session is null || message.Role != ChatRole.Assistant)
        {
            return;
        }

        var index = Messages.IndexOf(message);
        if (index <= 0 || index >= _session.Messages.Count)
        {
            return;
        }

        // 只允许重新生成最后一轮：删中间的消息再追加到末尾会把历史顺序打乱
        if (index != Messages.Count - 1 || index != _session.Messages.Count - 1)
        {
            StatusText = "只能重新生成最后一条回复";
            return;
        }

        Messages.RemoveAt(index);
        _session.Messages.RemoveAt(index);
        PersistSession();
        RefreshSessionItem();

        await (_generation = StreamAssistantAsync());
    }

    [RelayCommand]
    private void DeleteMessage(ChatMessageViewModel? message)
    {
        if (IsStreaming || message is null || _session is null)
        {
            return;
        }

        var index = Messages.IndexOf(message);
        if (index < 0 || index >= _session.Messages.Count)
        {
            return;
        }

        Messages.RemoveAt(index);
        _session.Messages.RemoveAt(index);
        PersistSession();
        RefreshSessionItem();
        UpdateStats(null, null, null);
        StatusText = "消息已删除";
    }

    private bool CanStop() => IsStreaming;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void NewSession()
    {
        if (IsStreaming)
        {
            _cts?.Cancel();
        }

        CreateSession();
        StatusText = "已新建会话";
    }

    [RelayCommand]
    private void BeginRename()
    {
        if (_session is null)
        {
            return;
        }

        SessionTitleDraft = _session.Title;
        IsRenaming = true;
        RenameRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CancelRename()
    {
        SessionTitleDraft = _session?.Title ?? string.Empty;
        IsRenaming = false;
    }

    private bool CanRenameSession() => _session is not null && !string.IsNullOrWhiteSpace(SessionTitleDraft);

    [RelayCommand(CanExecute = nameof(CanRenameSession))]
    private void RenameSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.Title = SessionTitleDraft.Trim();
        IsRenaming = false;
        PersistSession();
        RefreshSessionItem();
        StatusText = "会话已重命名";
    }

    [RelayCommand]
    private async Task ClearSessionAsync()
    {
        if (_session is null)
        {
            return;
        }

        // 生成中先取消并等它收尾，否则收尾逻辑会把回复写回刚清空的会话
        await StopGenerationAsync();

        if (_session is null || Messages.Count == 0 && _session.Messages.Count == 0)
        {
            StatusText = "当前会话没有消息";
            return;
        }

        Messages.Clear();
        _session.Messages.Clear();
        PersistSession();
        RefreshSessionItem();
        UpdateStats(null, null, null);
        StatusText = "已清空当前会话的消息";
    }

    partial void OnSessionTitleDraftChanged(string value) => RenameSessionCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task DeleteCurrentSessionAsync()
    {
        if (CurrentSession is not { } item)
        {
            return;
        }

        // 先等生成收尾，否则收尾时的 PersistSession 会把刚删掉的文件又写回去
        await StopGenerationAsync();

        _sessionStore.TryDelete(item.Id);
        Sessions.Remove(item);

        if (Sessions.Count == 0)
        {
            CreateSession();
        }
        else
        {
            _suppressSessionSwitch = true;
            CurrentSession = Sessions[0];
            _suppressSessionSwitch = false;
            SwitchSession(Sessions[0].Session);
        }

        StatusText = "会话已删除";
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private async Task AddDirectoryAsync()
    {
        if (DirectoryPicker is null)
        {
            return;
        }

        var path = await DirectoryPicker();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (ModelDirectories.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ModelDirectories.Add(path);
        _settings.ModelDirectories.Add(path);
        SaveSettings();
        UpdateEmptyState();

        await RescanAsync();
    }

    [RelayCommand]
    private async Task RemoveDirectoryAsync(string? path)
    {
        if (path is null)
        {
            return;
        }

        ModelDirectories.Remove(path);
        _settings.ModelDirectories.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        UpdateEmptyState();

        if (ModelDirectories.Count > 0)
        {
            await RescanAsync();
        }
        else
        {
            Models.Clear();
            UpdateEmptyState();
        }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (ModelDirectories.Count == 0)
        {
            UpdateEmptyState();
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsBusy = true;
        StatusText = "正在扫描模型…";

        try
        {
            var directories = ModelDirectories.ToList();
            var progress = new Progress<ScanProgress>(value =>
                StatusText = $"正在扫描 {value.ScannedFiles}/{value.TotalFiles}");

            var result = await Task.Run(() => _catalog.ScanAsync(directories, progress, token), token);

            Models.Clear();
            foreach (var descriptor in result.Models)
            {
                Models.Add(new ModelItemViewModel(descriptor));
            }

            var errors = result.Errors.Count == 0 ? "" : $"，{result.Errors.Count} 个文件读取失败";
            StatusText = $"扫描完成：{Models.Count} 个模型{errors}";

            if (SelectedModel is not null && Models.All(model => !model.IsSamePath(SelectedModel.Descriptor.Path)))
            {
                _suppressModelSelection = true;
                SelectedModel = null;
                _suppressModelSelection = false;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            UpdateEmptyState();
        }
    }

    private async Task LoadModelAsync(ModelItemViewModel item)
    {
        if (_settings.Backend is null)
        {
            SettingsNotice = "请先选择推理后端，再加载模型";
            IsSettingsOpen = true;
            return;
        }

        var model = item.Descriptor;

        if (IsStreaming)
        {
            _cts?.Cancel();
        }

        IsBusy = true;
        LoadProgress = 0;
        StatusText = $"正在加载 {model.DisplayName}（{model.Quantization}）…";

        try
        {
            var options = BuildLoadOptions(model);
            var progress = new Progress<float>(value => LoadProgress = value);

            await _engine.LoadAsync(model, options, progress, CancellationToken.None);

            _settings.LastModelPath = model.Path;
            SaveSettings();

            var profile = GetOrCreateProfile(model);
            SystemPrompt = profile.SystemPrompt ?? DefaultSystemPrompt;
            SyncProfileFields(profile);

            StatusText = $"{model.DisplayName} · {model.Quantization} · {model.SizeText}";
            OnPropertyChanged(nameof(Header));
            OnPropertyChanged(nameof(CanChat));
            UpdateStats(null, null, null);
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
            SettingsNotice = ex.Message;
            IsSettingsOpen = true;
            OnPropertyChanged(nameof(Header));
            OnPropertyChanged(nameof(CanChat));
        }
        finally
        {
            IsBusy = false;
            LoadProgress = 1;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private ModelLoadOptions BuildLoadOptions(ModelDescriptor model)
    {
        var defaults = _settings.Defaults;
        var profile = GetOrCreateProfile(model);

        return new ModelLoadOptions
        {
            Backend = _settings.Backend ?? BackendKind.Cuda12,
            GpuLayerCount = profile.GpuLayers ?? defaults.GpuLayers,
            ContextSize = profile.ContextSize ?? defaults.ContextSize,
            BatchSize = profile.BatchSize ?? defaults.BatchSize,
            Threads = profile.Threads ?? defaults.Threads,
            KvCacheType = profile.KvCacheType ?? defaults.KvCacheType,
        };
    }

    /// <summary>第一次加载某个模型时按显存算一份推荐值并存进 profile，用户后续可覆盖。</summary>
    private ModelProfile GetOrCreateProfile(ModelDescriptor model)
    {
        if (_settings.ModelProfiles.TryGetValue(model.Path, out var existing))
        {
            return existing;
        }

        var recommendation = LaunchAdvisor.Recommend(model, GpuProbe.Detect(), _settings.Defaults);
        var profile = new ModelProfile
        {
            GpuLayers = recommendation.GpuLayers,
            ContextSize = recommendation.ContextSize,
        };

        _settings.ModelProfiles[model.Path] = profile;
        SaveSettings();
        return profile;
    }

    private void SyncProfileFields(ModelProfile profile)
    {
        var defaults = _settings.Defaults;
        ProfileGpuLayersText = (profile.GpuLayers ?? defaults.GpuLayers).ToString();
        ProfileContextSizeText = (profile.ContextSize ?? defaults.ContextSize).ToString();
        ProfileTemperatureText = (profile.Temperature ?? defaults.Temperature).ToString("0.##");
        ProfileMaxTokensText = (profile.MaxTokens ?? defaults.MaxTokens).ToString();
        ProfileNotice = string.Empty;
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedModel is null)
        {
            ProfileNotice = "请先选择一个模型";
            return;
        }

        var profile = GetOrCreateProfile(SelectedModel.Descriptor);
        var problems = new List<string>();

        if (int.TryParse(ProfileGpuLayersText.Trim(), out var layers))
        {
            profile.GpuLayers = Math.Max(0, layers);
        }
        else
        {
            problems.Add("GPU 层数");
        }

        if (int.TryParse(ProfileContextSizeText.Trim(), out var context))
        {
            profile.ContextSize = Math.Clamp(context, 512, 262144);
        }
        else
        {
            problems.Add("上下文长度");
        }

        if (double.TryParse(
                ProfileTemperatureText.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var temperature))
        {
            profile.Temperature = Math.Clamp(temperature, 0, 2);
        }
        else
        {
            problems.Add("温度");
        }

        if (int.TryParse(ProfileMaxTokensText.Trim(), out var maxTokens))
        {
            profile.MaxTokens = maxTokens < 0 ? -1 : Math.Min(maxTokens, 65536);
        }
        else
        {
            problems.Add("最大 token");
        }

        if (problems.Count > 0)
        {
            ProfileNotice = $"{string.Join("、", problems)} 需要填写数字";
            return;
        }

        SaveSettings();
        SyncProfileFields(profile);
        ProfileNotice = "已保存；GPU 层数 / 上下文长度的改动需要「重新加载模型」后生效";
    }

    [RelayCommand]
    private async Task ReloadModelAsync()
    {
        if (SelectedModel is { } model)
        {
            await LoadModelAsync(model);
        }
    }

    private ChatOptions BuildChatOptions()
    {
        var defaults = _settings.Defaults;
        ModelProfile? profile = null;

        if (SelectedModel is not null)
        {
            _settings.ModelProfiles.TryGetValue(SelectedModel.Descriptor.Path, out profile);
        }

        return new ChatOptions
        {
            Temperature = profile?.Temperature ?? defaults.Temperature,
            TopP = profile?.TopP ?? defaults.TopP,
            TopK = profile?.TopK ?? defaults.TopK,
            RepeatPenalty = profile?.RepeatPenalty ?? defaults.RepeatPenalty,
            MaxTokens = profile?.MaxTokens ?? defaults.MaxTokens,
        };
    }

    /// <summary>
    /// 组装请求：首条固定 system（引擎的 KV cache 增量比对靠它对齐），
    /// 超出预算时从最旧的问答对开始丢，保留最后一轮。
    /// </summary>
    private List<ChatMessage> BuildRequestHistory(ChatSession session, out int trimmed)
    {
        trimmed = 0;
        var request = new List<ChatMessage> { new(ChatRole.System, GetSystemPrompt()) };

        if (session.Messages.Count == 0)
        {
            return request;
        }

        if (session.ContextMode == ContextMode.Fresh)
        {
            var lastUser = session.Messages.LastOrDefault(message => message.Role == ChatRole.User);
            if (lastUser is not null)
            {
                request.Add(new ChatMessage(ChatRole.User, lastUser.Text));
            }

            return request;
        }

        var engine = ActiveEngine;
        var window = engine.ContextSize;
        if (window <= 0)
        {
            request.AddRange(session.Messages.Select(message => new ChatMessage(message.Role, message.Text)));
            return request;
        }

        var reserve = BuildChatOptions().MaxTokens;
        var budget = Math.Max(
            512,
            window - (reserve > 0 ? reserve : DefaultReserveTokens) - TemplateOverheadTokens);

        var used = engine.CountTokens(request[0].Text) + 8;
        var kept = new List<ChatMessage>();

        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            var message = new ChatMessage(session.Messages[i].Role, session.Messages[i].Text);
            var cost = engine.CountTokens(message.Text) + 8;

            if (used + cost > budget && kept.Count >= 2)
            {
                trimmed = i + 1;
                break;
            }

            used += cost;
            kept.Insert(0, message);
        }

        // 裁剪后必须以 user 开头，避免把半轮补全当成历史
        while (kept.Count > 0 && kept[0].Role != ChatRole.User)
        {
            kept.RemoveAt(0);
            trimmed++;
        }

        request.AddRange(kept);
        return request;
    }

    private string GetSystemPrompt() =>
        string.IsNullOrWhiteSpace(SystemPrompt) ? DefaultSystemPrompt : SystemPrompt;

    private void UpdateStats(UsageInfo? usage, double? seconds, double? firstTokenMs, int trimmed = 0)
    {
        var parts = new List<string>(4);
        var engine = ActiveEngine;

        var promptTokens = usage?.PromptTokens ?? 0;
        var completionTokens = usage?.CompletionTokens ?? 0;

        if (firstTokenMs is { } latency && latency >= 0)
        {
            parts.Add($"首 token {latency:0} ms");
        }

        if (seconds is { } elapsed && elapsed > 0 && completionTokens > 0)
        {
            parts.Add($"{completionTokens / elapsed:0.0} tok/s");
        }

        // 刚生成完用引擎累计的真实占用；切换会话/删消息后按当前会话重新估算
        var used = usage is not null
            ? (engine.ContextTokensUsed > 0 ? engine.ContextTokensUsed : promptTokens + completionTokens)
            : EstimateContextTokens(engine);

        var window = engine.ContextSize;
        if (window > 0)
        {
            parts.Add($"上下文 {used}/{window}");
        }
        else if (used > 0)
        {
            parts.Add($"上下文 {used}");
        }

        if (trimmed > 0)
        {
            parts.Add($"已裁剪 {trimmed} 条");
        }

        StatsText = string.Join(" · ", parts);
    }

    private int EstimateContextTokens(IChatEngine engine)
    {
        if (_session is null || _session.Messages.Count == 0)
        {
            return 0;
        }

        var total = engine.CountTokens(GetSystemPrompt()) + 8;
        foreach (var message in _session.Messages)
        {
            total += engine.CountTokens(message.Text) + 8;
        }

        return total;
    }

    private static string MakeTitle(string text)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 18 ? oneLine : oneLine[..18] + "…";
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        BackendUnavailableException => ex.Message,
        ModelLoadException => ex.Message,
        OutOfMemoryException => "显存或内存不足，可以降低上下文长度或减少 GPU 层数后重试。",
        OperationCanceledException => "已取消",
        _ => $"出错了：{ex.Message}",
    };

    private void PersistSession(ChatSession? session = null)
    {
        session ??= _session;
        if (session is null || IsDemoMode)
        {
            return;
        }

        session.UpdatedAt = DateTimeOffset.Now;

        if (!_sessionStore.TrySave(session) && _sessionStore.LastError is { } error)
        {
            StatusText = error;
        }
    }

    private void RefreshSessionItem()
    {
        if (CurrentSession is null || _session is null)
        {
            return;
        }

        CurrentSession.Refresh();
        SessionTitleDraft = CurrentSession.Title;
    }

    private void SaveSettings()
    {
        if (!_settingsStore.TrySave(_settings) && _settingsStore.LastError is { } error)
        {
            StatusText = error;
        }
    }

    private void UpdateEmptyState()
    {
        // 没有消息且没有模型时才展示引导，否则会盖住对话内容（自检/演示模式）
        IsEmptyVisible = Models.Count == 0 && Messages.Count == 0;
        OnPropertyChanged(nameof(ShowReadyHint));
        OnPropertyChanged(nameof(IsSessionEmpty));

        if (ModelDirectories.Count == 0)
        {
            EmptyTitle = "还没有模型目录";
            EmptyHint = "添加一个存放 .gguf 文件的文件夹，BonsaiLLM 会递归扫描。";
        }
        else
        {
            EmptyTitle = "没有找到模型";
            EmptyHint = "当前目录里没有可用的 .gguf 文件，换个目录或点「重新扫描」。";
        }
    }

    private static bool LlamaBackendIsLocked() => LlamaBackend.Configured is not null;

    private static string FormatGb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";

    /// <summary>自检入口：用假数据引擎跑一遍渲染链路。</summary>
    internal void StartSelfCheck()
    {
        _useDemo = true;
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(CanChat));

        InputText = "自检：渲染一段 Markdown";

        if (SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
    }
}
