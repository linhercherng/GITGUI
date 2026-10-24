using System.Text.Json;
using GitGui.Core;

namespace GitGui.Infrastructure;

public sealed class AppearancePreferences(string path) : IAppearancePreferences
{
    public string? LoadTheme()
    {
        try
        {
            if (!File.Exists(path)) return null;
            // Preferences are a few bytes. Do not load an accidentally replaced large file.
            if (new FileInfo(path).Length > 4096) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.Preference)?.Theme;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void SaveTheme(string id)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preference(id), SettingsJsonContext.Default.Preference));
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal sealed record Preference(string Theme);
}
