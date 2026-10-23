using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Data;

namespace GitGui.UI;

public sealed class Localization : INotifyPropertyChanged
{
    private IReadOnlyDictionary<string, string> values = Load("zh-TW");
    public string Culture { get; private set; } = "zh-TW";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => values.TryGetValue(key, out var value) ? value : key;
    public void SetCulture(string culture)
    {
        culture = culture == "en" ? "en" : "zh-TW";
        values = Load(culture);
        Culture = culture;
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new("Item"));
        PropertyChanged?.Invoke(this, new(nameof(Culture)));
    }
    public string Format(string key, params object?[] args) => string.Format(System.Globalization.CultureInfo.GetCultureInfo(Culture), this[key], args);
    public void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target.Bind(property, new Binding("[" + key + "]") { Source = this });
    private static Dictionary<string, string> Load(string culture)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"GitGui.UI.Resources.{culture}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
