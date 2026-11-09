using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BonsaiLLM.App.ViewModels;

namespace BonsaiLLM.App.Views;

/// <summary>运行日志弹窗。非模态，关掉不影响主窗口继续跑。</summary>
public partial class LogWindow : Window
{
    private MainWindowViewModel? _vm;

    public LogWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _vm = DataContext as MainWindowViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.Logs.CollectionChanged += OnLogsChanged;
        ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (_vm is not null)
        {
            _vm.Logs.CollectionChanged -= OnLogsChanged;
            _vm.CloseLogCommand.Execute(null);
            _vm = null;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
        => Dispatcher.UIThread.Post(
            () => this.FindControl<ScrollViewer>("LogScroll")?.ScrollToEnd(),
            DispatcherPriority.Background);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
