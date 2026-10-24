using GitGui.Core;
using GitGui.Infrastructure;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class UiPreferencesTests
{
    [Fact]
    public void Preferences_roundtrip_locale_and_explicit_unbinding_and_recover_invalid_input()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitgui-ui-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "ui.json"); var store = new UiPreferencesStore(path);
            Assert.Equal("zh-TW", store.Load().Culture);
            store.Save(new("en", new Dictionary<string, string?> { ["Main.Refresh"] = "Ctrl+R", ["Main.Commit"] = null }));
            Assert.Equal("{\"Culture\":\"en\",\"Shortcuts\":{\"Main.Refresh\":\"Ctrl\\u002BR\",\"Main.Commit\":null}}", File.ReadAllText(path));
            var restored = new UiPreferencesStore(path).Load(); Assert.Equal("en", restored.Culture);
            Assert.Equal("Ctrl+R", restored.Shortcuts!["Main.Refresh"]); Assert.Null(restored.Shortcuts["Main.Commit"]);
            Assert.Single(Directory.EnumerateFiles(directory));
            File.WriteAllText(path, "{invalid"); Assert.Equal(new UiPreferences(), store.Load());
            File.WriteAllText(path, new string('x', 65_537)); Assert.Equal(new UiPreferences(), store.Load());
        }
        finally { Directory.Delete(directory, true); }
    }
}
