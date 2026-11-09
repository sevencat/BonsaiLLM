using Avalonia.Controls;
using Avalonia.Interactivity;
using BonsaiLLM.App.ViewModels;

namespace BonsaiLLM.App.Views;

public partial class SettingsPanel : UserControl
{
    public SettingsPanel()
    {
        InitializeComponent();
    }

    private void OnRemoveDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && DataContext is MainWindowViewModel vm)
        {
            vm.RemoveDirectoryCommand.Execute(path);
        }
    }
}
