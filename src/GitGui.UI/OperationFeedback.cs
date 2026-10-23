using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.UI;

public sealed class OperationFeedback : ObservableObject, IDisposable
{
    private readonly Localization localization;
    private readonly Func<bool> canExecute;
    private OperationOutcome? outcome;
    private Func<Task>? retry;
    private Func<Task>? explicitRemedy;
    private string? explicitLabelKey;
    public Func<GitSuggestedAction, Task>? Navigate { get; set; }
    public AsyncRelayCommand RemedyCommand { get; }
    public bool HasAction => outcome?.SuggestedAction is not null and not GitSuggestedAction.None;
    public bool CanRemedy => HasAction && canExecute();
    public string Summary => outcome is null ? "" : localization["Error" + outcome.Error];
    public string ActionLabel => localization[explicitLabelKey ?? "Remedy" + (outcome?.SuggestedAction ?? GitSuggestedAction.None)];
    public OperationFeedback(Localization localization, Func<bool> canExecute)
    {
        this.localization = localization; this.canExecute = canExecute; localization.PropertyChanged += LocaleChanged;
        RemedyCommand = new(async () =>
        {
            if (!CanRemedy || outcome is null) return;
            if (explicitRemedy is { } remedy) await remedy();
            else if (outcome.SuggestedAction == GitSuggestedAction.Retry && retry is not null) await retry();
            else if (Navigate is { } navigate) await navigate(outcome.SuggestedAction);
        });
    }
    public void Record(OperationOutcome result, Func<Task>? retryOperation = null, string? explicitActionLabel = null, Func<Task>? explicitAction = null)
    { outcome = result; retry = retryOperation; explicitLabelKey = explicitActionLabel; explicitRemedy = explicitAction; Notify(); }
    public void Notify() { OnPropertyChanged(nameof(HasAction)); OnPropertyChanged(nameof(CanRemedy)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(ActionLabel)); }
    private void LocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Notify();
    public void Dispose() => localization.PropertyChanged -= LocaleChanged;
    public static async Task ShowHelpAsync(Window owner, Localization l, GitSuggestedAction action)
    {
        var dialog = new Window { Width = 620, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = l["Remedy" + action] };
        AppearanceManager.StyleWindow(dialog);
        var panel = new StackPanel { Spacing = 14, Margin = new(22) }; panel.Children.Add(new TextBlock { Text = l["Help" + action], TextWrapping = TextWrapping.Wrap });
        var close = new ActionButton { Content = l["Close"], Icon = "Close", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => dialog.Close(); panel.Children.Add(close); dialog.Content = panel;
        dialog.KeyBindings.Add(new() { Gesture = new(Avalonia.Input.Key.Escape), Command = new RelayCommand(() => dialog.Close()) }); dialog.Opened += (_, _) => close.Focus();
        var focus = owner.FocusManager?.GetFocusedElement(); await dialog.ShowDialog(owner); focus?.Focus();
    }
}

public sealed class OperationFeedbackBar : StackPanel
{
    public static readonly StyledProperty<OperationFeedback?> FeedbackProperty = AvaloniaProperty.Register<OperationFeedbackBar, OperationFeedback?>(nameof(Feedback));
    public OperationFeedback? Feedback { get => GetValue(FeedbackProperty); set => SetValue(FeedbackProperty, value); }
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ActionButton remedy = new() { Icon = "Shield", Name = "SuggestedActionButton" };
    public OperationFeedbackBar() { Orientation = Orientation.Horizontal; Spacing = 8; Children.Add(summary); Children.Add(remedy); IsVisible = false; }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change); if (change.Property != FeedbackProperty || Feedback is not { } feedback) return;
        Bind(IsVisibleProperty, new Binding(nameof(feedback.HasAction)) { Source = feedback }); summary.Bind(TextBlock.TextProperty, new Binding(nameof(feedback.Summary)) { Source = feedback });
        remedy.Bind(ContentControl.ContentProperty, new Binding(nameof(feedback.ActionLabel)) { Source = feedback }); remedy.Bind(IsEnabledProperty, new Binding(nameof(feedback.CanRemedy)) { Source = feedback }); remedy.Command = feedback.RemedyCommand;
    }
}
