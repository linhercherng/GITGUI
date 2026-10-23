namespace GitGui.Core;

/// <summary>Small local application preference; contains no repository or credential data.</summary>
public interface IAppearancePreferences
{
    string? LoadTheme();
    void SaveTheme(string id);
}
