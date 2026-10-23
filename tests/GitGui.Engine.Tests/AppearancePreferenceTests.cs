using GitGui.Infrastructure;
using Xunit;

namespace GitGui.Engine.Tests;

public class AppearancePreferenceTests
{
    [Fact]
    public void Palette_roundtrips_and_corrupt_preferences_do_not_prevent_startup()
    {
        using var temporary = new TemporaryRepository();
        var file = Path.Combine(temporary.Root, "settings", "appearance.json");
        var preferences = new AppearancePreferences(file);
        Assert.Null(preferences.LoadTheme());
        preferences.SaveTheme("forest");
        Assert.Equal("forest", new AppearancePreferences(file).LoadTheme());
        File.WriteAllText(file, "{broken");
        Assert.Null(new AppearancePreferences(file).LoadTheme());
        preferences.SaveTheme("porcelain");
        Assert.Equal("porcelain", preferences.LoadTheme());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!));
    }

    [Fact]
    public void Failed_atomic_save_leaves_no_temporary_file_and_keeps_existing_directory()
    {
        using var temporary = new TemporaryRepository();
        var target = Path.Combine(temporary.Root, "blocked"); Directory.CreateDirectory(target);
        var error = Record.Exception(() => new AppearancePreferences(target).SaveTheme("forest"));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(temporary.Root));
    }
}
