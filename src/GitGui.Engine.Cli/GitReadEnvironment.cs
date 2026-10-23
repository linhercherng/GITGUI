using System.Diagnostics;

namespace GitGui.Engine.Cli;

/// <summary>Read-side path resolution matching the selected Git child's environment.</summary>
public static class GitReadEnvironment
{
    private static readonly string[] NativeViewOverrides =
    [
        "GIT_NAMESPACE", "GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_REPLACE_REF_BASE",
        "GIT_SHALLOW_FILE", "GIT_GRAFT_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_NO_REPLACE_OBJECTS", "GIT_REFERENCE_BACKEND", "GIT_INDEX_FILE", "GIT_ATTR_SOURCE", "GIT_DIFF_OPTS"
    ];

    // Explicit libgit2 repository opens do not reproduce these selected-child overrides.
    // Falling back also retains Git's own diagnostics for conflicting pathspec settings.
    public static bool SupportsNativeRepositoryView(ProcessStartInfo start)
        => !NativeViewOverrides.Any(key => start.Environment.TryGetValue(key, out var value) && value is not null) &&
            !start.Environment.Any(pair => pair.Key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) &&
                pair.Key.EndsWith("_PATHSPECS", StringComparison.OrdinalIgnoreCase) && pair.Value is not null);

    public static string? EffectiveHome(ProcessStartInfo start)
    {
        string? Value(string key) => start.Environment.TryGetValue(key, out var value) ? value : null;
        if (Value("HOME") is { } explicitHome) return explicitHome;
        if (!OperatingSystem.IsWindows()) return null;
        // Git for Windows compat/mingw.c: setup_windows_environment rejects a
        // disconnected home share and the system32 directory before USERPROFILE.
        if (Value("HOMEDRIVE") is { } drive && Value("HOMEPATH") is { } path)
        {
            var candidate = drive + path;
            if (Directory.Exists(candidate) && !GitAccess.SamePath(candidate, Environment.GetFolderPath(Environment.SpecialFolder.System)))
                return candidate;
        }
        return Value("USERPROFILE");
    }
}
