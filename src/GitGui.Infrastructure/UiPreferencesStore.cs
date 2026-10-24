using System.Text.Json;
using GitGui.Core;

namespace GitGui.Infrastructure;

public sealed class UiPreferencesStore(string path) : IUiPreferences
{
    public UiPreferences Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return new();
            var saved = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.UiPreferences);
            return saved is null ? new() : saved with { Culture = saved.Culture == "en" ? "en" : "zh-TW" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(UiPreferences preferences)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, SettingsJsonContext.Default.UiPreferences)); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
