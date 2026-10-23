using Avalonia.Automation;
using Avalonia.Controls;
using GitGui.Core;

namespace GitGui.UI;

internal static class UiAccessibility
{
    /// <summary>Keep the standard ListBox selection peers; supply untruncated repository names.</summary>
    public static void NameFileItems(ListBox list, Localization l)
    {
        void Name(Control container)
        {
            if (container.DataContext is not FileChange file) return;
            AutomationProperties.SetName(container, file.DisplayPath);
            AutomationProperties.SetHelpText(container, l.Format("FileStatusDescription", file.IndexStatus, file.WorktreeStatus));
            AutomationProperties.SetAutomationId(container, "file-" + file.DisplayPath);
        }
        void Update(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { foreach (var container in list.GetRealizedContainers()) Name(container); }
        list.ContainerPrepared += (_, args) => Name(args.Container);
        list.AttachedToVisualTree += (_, _) => l.PropertyChanged += Update;
        list.DetachedFromVisualTree += (_, _) => l.PropertyChanged -= Update;
    }
}
