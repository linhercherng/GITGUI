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
    public Task InitializationTask { get; private set; } = Task.CompletedTask;
    public GitSettingsWindow(IGitRuntimeSettings settings, Localization l)
    {
        var vm = new GitSettingsViewModel(settings);
        DataContext = vm; Title = l["GitSettings"]; Width = 700; CanResize = false;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppearanceManager.StyleWindow(this);
        l.Bind(this, TitleProperty, "GitSettings");
        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        var title = new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold }; l.Bind(title, TextBlock.TextProperty, "GitSettings"); stack.Children.Add(title);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap }; l.Bind(hint, TextBlock.TextProperty, "GitPathHint"); stack.Children.Add(hint);
        var path = new TextBox { Name = "GitPathInput", Watermark = l["GitAutomatic"] };
        l.Bind(path, TextBox.WatermarkProperty, "GitAutomatic"); l.Bind(path, Avalonia.Automation.AutomationProperties.NameProperty, "GitSettings");
        path.Bind(TextBox.TextProperty, new Binding(nameof(vm.PathText)) { Mode = BindingMode.TwoWay });
        path.Bind(IsEnabledProperty, new Binding(nameof(vm.CanEdit))); stack.Children.Add(path);
        var picks = new WrapPanel();
        Button Pick(string key, string icon)
        {
            var button = new ActionButton { Icon = icon, Margin = new Thickness(0, 0, 8, 0) }; l.Bind(button, ContentControl.ContentProperty, key);
            button.Bind(IsEnabledProperty, new Binding(nameof(vm.CanEdit))); picks.Children.Add(button); return button;
        }
        var file = Pick("GitChooseFile", "Repository");
        file.Click += async (_, _) =>
        {
            var selected = await StorageProvider.OpenFilePickerAsync(new() { Title = l["GitChooseFile"], AllowMultiple = false,
                FileTypeFilter = [new("Git") { Patterns = OperatingSystem.IsWindows() ? ["git.exe"] : ["git"] }] });
            if (selected.Count > 0 && selected[0].TryGetLocalPath() is { } local) vm.PathText = local;
        };
        var folder = Pick("GitChooseFolder", "Folder");
        folder.Click += async (_, _) =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new() { Title = l["GitChooseFolder"], AllowMultiple = false });
            if (selected.Count > 0 && selected[0].TryGetLocalPath() is { } local) vm.PathText = local;
        };
        Pick("GitAutomatic", "Refresh").Click += (_, _) => vm.PathText = "";
        stack.Children.Add(picks);
        var status = new TextBox { Name = "GitRuntimeStatus", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
        status.Bind(TextBox.TextProperty, new Binding(nameof(vm.Status)) { Mode = BindingMode.OneWay }); stack.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new ActionButton { Icon = "Close", Content = l["Cancel"] }; cancel.Click += (_, _) => Close(null); buttons.Children.Add(cancel);
        l.Bind(cancel, ContentControl.ContentProperty, "Cancel");
        var check = new ActionButton { Icon = "Shield", Content = l["GitCheck"], Command = vm.CheckCommand };
        l.Bind(check, ContentControl.ContentProperty, "GitCheck");
        check.Bind(IsEnabledProperty, new Binding(nameof(vm.CanEdit))); buttons.Children.Add(check);
        var save = new ActionButton { Name = "SaveGitSettingsButton", Icon = "Save", Content = l["GitSave"], Command = vm.SaveCommand };
        l.Bind(save, ContentControl.ContentProperty, "GitSave");
        save.Classes.Add("primary");
        save.Bind(IsEnabledProperty, new Binding(nameof(vm.CanEdit))); buttons.Children.Add(save); stack.Children.Add(buttons);
        Content = stack; vm.Saved += (_, info) => Close(info);
        Opened += (_, _) => InitializationTask = vm.InitializeAsync(); Closed += (_, _) => vm.Dispose();
    }
}
