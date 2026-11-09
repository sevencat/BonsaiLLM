using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Messages.CollectionChanged += OnMessagesChanged;
            vm.StreamingContentUpdated += OnStreamingContentUpdated;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add
            || Environment.GetCommandLineArgs().Contains("--selfcheck"))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => this.FindControl<ScrollViewer>("ChatScroll")?.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    private void OnStreamingContentUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () => this.FindControl<ScrollViewer>("ChatScroll")?.ScrollToEnd(),
            DispatcherPriority.Background);
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
        if (e.Key == Key.Enter
            && DataContext is MainWindowViewModel vm
            && vm.RenameSessionCommand.CanExecute(null))
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
