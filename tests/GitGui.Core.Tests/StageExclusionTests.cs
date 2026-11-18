using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public sealed class StageExclusionTests
{
    [Fact]
    public void Files_are_literal_and_folders_include_their_gitlink_and_descendants_only()
    {
        var rules = new StageExclusionRules([new("資料/ file[*].txt", false), new("build", true)]);
        Assert.True(rules.IsExcluded("資料/ file[*].txt"));
        Assert.False(rules.IsExcluded("資料/ file1.txt"));
        Assert.True(rules.IsExcluded("build")); Assert.True(rules.IsExcluded("build/a/b.txt"));
        Assert.False(rules.IsExcluded("build.txt")); Assert.False(rules.IsExcluded("build-tools/a.txt"));
        Assert.False(rules.IsExcluded("src/build/a.txt"));
    }

    [Fact]
    public void Nested_and_lexically_adjacent_folders_do_not_mask_an_ancestor()
    {
        var rules = new StageExclusionRules([new("foo", true), new("foo/bar", true), new("foo-/x", true)]);
        Assert.True(rules.IsExcluded("foo/a.txt")); Assert.True(rules.IsExcluded("foo/bar/a.txt"));
        Assert.True(rules.IsExcluded("foo-/x/a.txt")); Assert.False(rules.IsExcluded("foo-/a.txt"));
    }

    [Fact]
    public void Rename_is_excluded_if_either_old_or_new_path_matches()
    {
        var rules = new StageExclusionRules([new("private", true)]);
        Assert.True(rules.IsExcluded(new FileChange("public/a.txt", "R", "M", [], "private/a.txt")));
        Assert.True(rules.IsExcluded(new FileChange("private/a.txt", "R", "M", [], "public/a.txt")));
        Assert.False(rules.IsExcluded(new FileChange("public/a.txt", "R", "M", [], "old/a.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../a")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("/outside")]
    [InlineData("a\0b")]
    public void Invalid_or_unrepresentable_paths_are_rejected(string path)
        => Assert.Throws<ArgumentException>(() => new StageExclusionRules([new(path, false)]));

    [Fact]
    public void Normalization_deduplicates_but_preserves_legal_spaces_and_folder_kind()
    {
        var rules = new StageExclusionRules([new(" space.txt", false), new(" space.txt", false), new("folder/", true)]);
        Assert.Equal(2, rules.Rules.Count); Assert.Equal(" space.txt", rules.Rules[0].Path);
        Assert.Equal(new StageExclusion("folder", true), rules.Rules[1]);
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), rules.IsExcluded("FOLDER/a"));
        Assert.Throws<ArgumentException>(() => new StageExclusionRules([new("folder/", false)]));
        Assert.Throws<ArgumentException>(() => new StageExclusionRules([new(new string((char)0xd800, 1), false)]));
    }
}
