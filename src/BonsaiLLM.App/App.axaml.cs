using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BonsaiLLM.App.Views;

namespace BonsaiLLM.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Initialize();

            var window = new MainWindow { DataContext = services.MainViewModel };
            if (window.Screens.Primary is { } screen)
            {
                // WorkingArea 是物理像素，Window.Width/Height 是 DIP，要除以缩放
                var area = screen.WorkingArea;
                var scaling = screen.Scaling;
                window.Width = Math.Min(1040, area.Width / scaling * 0.82);
                window.Height = Math.Min(730, area.Height / scaling * 0.82);
            }

            desktop.MainWindow = window;

            if (Environment.GetCommandLineArgs().Contains("--selfcheck"))
            {
                window.Topmost = true;

                Dispatcher.UIThread.Post(
                    services.MainViewModel.StartSelfCheck,
                    DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
