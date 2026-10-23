using System.Diagnostics;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class GitHomeEnvironmentTests
{
    [Theory]
    [InlineData("explicit")]
    [InlineData("roaming")]
    [InlineData("disconnected")]
    [InlineData("system32")]
    public async Task Effective_home_agrees_with_real_selected_Windows_Git(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var repo = new TemporaryRepository();
        var explicitHome = Path.Combine(repo.Root, "explicit-home");
        var roaming = Path.Combine(repo.Root, "roaming-home");
        var profile = Path.Combine(repo.Root, "profile-home");
        foreach (var path in new[] { explicitHome, roaming, profile })
        {
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, ".gitconfig"), "[test]\n home = " + Path.GetFileName(path) + "\n");
        }
        var candidate = scenario switch
        {
            "disconnected" => Path.Combine(repo.Root, "missing-home"),
            "system32" => Environment.GetFolderPath(Environment.SpecialFolder.System),
            _ => roaming
        };
        var start = new ProcessStartInfo(new GitRuntime().ResolveExecutable(null))
        {
            WorkingDirectory = repo.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Remove("HOME"); start.Environment.Remove("GIT_CONFIG_GLOBAL");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(repo.Root, "missing-xdg");
        var drive = Path.GetPathRoot(candidate)!;
        start.Environment["HOMEDRIVE"] = drive.TrimEnd('\\');
        start.Environment["HOMEPATH"] = candidate[(drive.Length - 1)..];
        start.Environment["USERPROFILE"] = profile;
        if (scenario == "explicit") start.Environment["HOME"] = explicitHome;
        var expected = scenario is "explicit" ? explicitHome : scenario is "roaming" ? roaming : profile;
        Assert.Equal(expected, GitReadEnvironment.EffectiveHome(start));
        foreach (var argument in new[] { "--no-optional-locks", "config", "--global", "--get", "test.home" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        Assert.Equal(Path.GetFileName(expected), (await output).Trim());
    }

    [Fact]
    public void Configuration_cache_tracks_roaming_Git_home_when_HOME_is_unset()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var repo = new TemporaryRepository();
        var roaming = Path.Combine(repo.Root, "roaming-home"); Directory.CreateDirectory(roaming);
        var profile = Path.Combine(repo.Root, "local-profile"); Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(new GitRuntime().ResolveExecutable(null));
        start.Environment.Clear();
        var drive = Path.GetPathRoot(roaming)!;
        start.Environment["HOMEDRIVE"] = drive.TrimEnd('\\');
        start.Environment["HOMEPATH"] = roaming[(drive.Length - 1)..];
        start.Environment["USERPROFILE"] = profile;
        var type = typeof(GitProcessRunner).Assembly.GetType("GitGui.Engine.Cli.RepositoryReadConfiguration", throwOnError: true)!;
        var snapshot = Activator.CreateInstance(type, [repo.Root, start, Array.Empty<byte>()])!;
        var isCurrent = type.GetMethod("IsCurrent")!;
        Assert.True((bool)isCurrent.Invoke(snapshot, [start])!);
        File.WriteAllText(Path.Combine(roaming, ".gitconfig"), "[core]\n attributesFile = ~/.config/git/attributes\n");
        Assert.False((bool)isCurrent.Invoke(snapshot, [start])!);
    }
}
