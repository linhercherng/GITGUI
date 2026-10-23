using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace GitGui.UI;

public sealed class Localization : INotifyPropertyChanged
{
    private IReadOnlyDictionary<string, string> values = Load("zh-TW");
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => values.TryGetValue(key, out var value) ? value : key;
    public void SetCulture(string culture)
    {
        values = Load(culture);
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new("Item"));
    }
    private static Dictionary<string, string> Load(string culture)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"GitGui.UI.Resources.{culture}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
