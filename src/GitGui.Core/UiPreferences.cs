namespace GitGui.Core;

public sealed record UiPreferences(string Culture = "zh-TW", IReadOnlyDictionary<string, string?>? Shortcuts = null);

/// <summary>User interface preferences contain no repository paths, commands, or credentials.</summary>
public interface IUiPreferences
{
    UiPreferences Load();
    void Save(UiPreferences preferences);
}
