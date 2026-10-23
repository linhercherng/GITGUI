using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace GitGui.UI;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.InitializeCommand.ExecuteAsync(null);
        Closed += (_, _) => viewModel.Dispose();
        Activated += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(viewModel.RepositoryPath) && !viewModel.IsBusy)
                await viewModel.RefreshCommand.ExecuteAsync(null);
        };
    }

    private async void BrowseClick(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "選取 Git 倉庫根目錄", AllowMultiple = false });
        if (folders.Count == 0 || DataContext is not MainWindowViewModel vm) return;
        var path = folders[0].TryGetLocalPath();
        if (path is null) return;
        vm.RepositoryPath = path;
        await vm.RefreshCommand.ExecuteAsync(null);
    }

    private void RecentSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (DataContext is MainWindowViewModel vm && args.AddedItems.Count > 0 && args.AddedItems[0] is string path)
            vm.RepositoryPath = path;
    }
}
