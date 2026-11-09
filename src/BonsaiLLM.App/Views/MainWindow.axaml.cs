using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BonsaiLLM.App.ViewModels;

namespace BonsaiLLM.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.DirectoryPicker = PickDirectoryAsync;
        vm.ImagePicker = PickImageAsync;
        vm.ImageSaver = SaveImageAsync;

        // 启动后直接可以打字，避免焦点停在按钮上
        this.FindControl<TextBox>("Composer")?.Focus();

        // 自检模式只用假数据引擎，不读设置、不扫描
        if (!Environment.GetCommandLineArgs().Contains("--selfcheck"))
        {
            _ = RunInitializeAsync(vm);
        }
    }

    private static async Task RunInitializeAsync(MainWindowViewModel vm)
    {
        try
        {
            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            vm.StatusText = $"初始化失败：{ex.Message}";
        }
    }

    private async Task<string?> PickDirectoryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择模型目录",
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"] }],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<bool> SaveImageAsync(byte[] bytes, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存标注图",
            SuggestedFileName = suggestedName,
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }],
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        await File.WriteAllBytesAsync(path, bytes);
        return true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Messages.CollectionChanged += OnMessagesChanged;
            vm.Logs.CollectionChanged += OnLogsChanged;
            vm.StreamingContentUpdated += OnStreamingContentUpdated;
            vm.RenameRequested += OnRenameRequested;
            vm.ClipboardWriter = CopyTextAsync;

            // ScrollViewer 自己会处理滚轮并标记 Handled，只有隧道阶段 + handledEventsToo 才收得到
            if (this.FindControl<ScrollViewer>("ChatScroll") is { } chat)
            {
                chat.AddHandler(
                    PointerWheelChangedEvent, OnChatWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
                chat.AddHandler(
                    PointerPressedEvent, OnChatPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            }
        }
    }

    private void OnRenameRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                var box = this.FindControl<TextBox>("RenameBox");
                box?.Focus();
                box?.SelectAll();
            },
            DispatcherPriority.Background);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // 切会话/清空：重新回到跟随状态
            _followTail = true;
            Dispatcher.UIThread.Post(() => ScrollToTail(true), DispatcherPriority.Background);
            return;
        }

        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        // 自己发的新消息一定跟到底部，助手回复则尊重用户当前位置
        var force = e.NewItems?.Count > 0
            && e.NewItems[0] is ChatMessageViewModel { IsAssistant: false };
        Dispatcher.UIThread.Post(() => ScrollToTail(force), DispatcherPriority.Background);
    }

    private async Task CopyTextAsync(string text)
    {
        if (Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占用时静默失败，状态栏已经由调用方更新
        }
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        // 抽屉没打开时控件不存在/不可见，跟着滚一下也不贵
        Dispatcher.UIThread.Post(
            () => this.FindControl<ScrollViewer>("LogScroll")?.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    private void OnStreamingContentUpdated(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => ScrollToTail(false), DispatcherPriority.Background);

    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 只认 offset 变化：内容变长带来的 Extent 增长不算用户滚动，否则流式输出时会误判成「已上滚」
        if (e.OffsetDelta.Y == 0 || sender is not ScrollViewer scroll)
        {
            return;
        }

        if (!IsAtBottom(scroll))
        {
            _followPausedAt = Environment.TickCount64;
            SetFollowTail(false);
            return;
        }

        // 回到（或本来就在）底部。虚拟化面板的 Extent 是估算值，刚上滚那几帧可能被误判成
        // 「已经到底」，所以离开底部后的短时间内不自动恢复跟随——用户滚轮向下到底也会立即恢复
        if (!_followTail && Environment.TickCount64 - _followPausedAt > FollowResumeDelayMs)
        {
            SetFollowTail(true);
        }
    }

    private void OnChatWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0)
        {
            _followPausedAt = Environment.TickCount64;
            SetFollowTail(false);
        }
        else if (sender is ScrollViewer scroll && !_followTail && IsAtBottom(scroll))
        {
            SetFollowTail(true);
        }
    }

    private void OnChatPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 拖滚动条同样算用户接管；按钮/文本选择不受影响
        if (e.Source is Visual source
            && source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
        {
            SetFollowTail(false);
        }
    }

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e) => ScrollToTail(true);

    private static bool IsAtBottom(ScrollViewer scroll)
        => scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y <= ScrollTailThreshold;

    private const double ScrollTailThreshold = 32;

    /// <summary>跟随状态：用户上滚后置 false，滚回底部或点「回到底部」后恢复。</summary>
    private bool _followTail = true;

    /// <summary>最近一次用户上滚的时间戳，用来抑制随后的误判。</summary>
    private long _followPausedAt;

    private const int FollowResumeDelayMs = 600;

    private void SetFollowTail(bool follow)
    {
        _followTail = follow;
        UpdateScrollToBottomButton(!follow);
    }

    private void ScrollToTail(bool force)
    {
        if (force)
        {
            SetFollowTail(true);
        }

        // 强制回底要补推几轮，普通跟随由流式更新自己持续驱动
        PushToTail(force ? 3 : 0);
    }

    private void PushToTail(int retries)
    {
        if (!_followTail || this.FindControl<ScrollViewer>("ChatScroll") is not { } scroll)
        {
            return;
        }

        scroll.ScrollToEnd();

        // 虚拟化面板的 Extent 是估算值，一次 ScrollToEnd 可能只推到「已实现部分」的末端，
        // 等布局再跑一两轮补推，否则长会话会停在中间
        if (retries > 0)
        {
            Dispatcher.UIThread.Post(() => PushToTail(retries - 1), DispatcherPriority.Background);
        }
    }

    private void UpdateScrollToBottomButton(bool visible)
    {
        if (this.FindControl<Button>("ScrollToBottomButton") is { } button)
        {
            button.IsVisible = visible;
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (vm.StopCommand.CanExecute(null))
            {
                vm.StopCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.IsSettingsOpen)
            {
                vm.CloseSettingsCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.N:
                vm.NewSessionCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemComma:
                vm.ToggleSettingsCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.R:
                vm.RescanCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when vm.SendCommand.CanExecute(null):
                vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private async void OnCopyMessage(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessageViewModel message }
            || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (Clipboard is not { } clipboard)
        {
            vm.StatusText = "剪贴板不可用";
            return;
        }

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(message.Text));
            await clipboard.SetDataAsync(data);
            vm.StatusText = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            vm.StatusText = $"复制失败：{ex.Message}";
        }
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.CancelRenameCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && vm.RenameSessionCommand.CanExecute(null))
        {
            vm.RenameSessionCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRemoveDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && DataContext is MainWindowViewModel vm)
        {
            vm.RemoveDirectoryCommand.Execute(path);
        }
    }
}
