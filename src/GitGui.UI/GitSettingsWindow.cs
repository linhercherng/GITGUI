using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GitGui.Core;

namespace GitGui.UI;

public sealed class GitSettingsWindow : Window
{
    public GitSettingsWindow(IGitRuntimeSettings settings, Localization l)
    {
        var vm = new GitSettingsViewModel(settings);
        DataContext = vm; Title = l["GitSettings"]; Width = 700; CanResize = false;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#191F29"); Foreground = Brush.Parse("#E4EAF3");
        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = l["GitSettings"], FontSize = 20, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = l["GitPathHint"], TextWrapping = TextWrapping.Wrap });
        var path = new TextBox { Name = "GitPathInput", Watermark = l["GitAutomatic"] };
        path.Bind(TextBox.TextProperty, new Binding(nameof(vm.PathText)) { Mode = BindingMode.TwoWay });
        path.Bind(IsEnabledProperty, new Binding(nameof(vm.CanEdit))); stack.Children.Add(path);
        var picks = new WrapPanel();
        Button Pick(string caption)
        {
            var button = new Button { Content = caption, Margin = new Thickness(0, 0, 8, 0) };
            button.Bind(IsEnabledProperty, new Binding(nameof(vm.CanEdit))); picks.Children.Add(button); return button;
        }
        var file = Pick(l["GitChooseFile"]);
        file.Click += async (_, _) =>
        {
            var selected = await StorageProvider.OpenFilePickerAsync(new() { Title = l["GitChooseFile"], AllowMultiple = false,
                FileTypeFilter = [new("Git") { Patterns = OperatingSystem.IsWindows() ? ["git.exe"] : ["git"] }] });
            if (selected.Count > 0 && selected[0].TryGetLocalPath() is { } local) vm.PathText = local;
        };
        var folder = Pick(l["GitChooseFolder"]);
        folder.Click += async (_, _) =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new() { Title = l["GitChooseFolder"], AllowMultiple = false });
            if (selected.Count > 0 && selected[0].TryGetLocalPath() is { } local) vm.PathText = local;
        };
        Pick(l["GitAutomatic"]).Click += (_, _) => vm.PathText = "";
        stack.Children.Add(picks);
        var status = new TextBox { Name = "GitRuntimeStatus", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
        status.Bind(TextBox.TextProperty, new Binding(nameof(vm.Status)) { Mode = BindingMode.OneWay }); stack.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = l["Cancel"] }; cancel.Click += (_, _) => Close(null); buttons.Children.Add(cancel);
        var check = new Button { Content = l["GitCheck"], Command = vm.CheckCommand };
        check.Bind(IsEnabledProperty, new Binding(nameof(vm.CanEdit))); buttons.Children.Add(check);
        var save = new Button { Name = "SaveGitSettingsButton", Content = l["GitSave"], Command = vm.SaveCommand, Background = Brush.Parse("#2463B6"), Foreground = Brushes.White };
        save.Bind(IsEnabledProperty, new Binding(nameof(vm.CanEdit))); buttons.Children.Add(save); stack.Children.Add(buttons);
        Content = stack; vm.Saved += (_, info) => Close(info);
        Opened += async (_, _) => await vm.InitializeAsync(); Closed += (_, _) => vm.Dispose();
    }
}
