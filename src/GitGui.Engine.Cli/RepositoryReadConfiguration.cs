using System.Diagnostics;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Engine.Cli;

/// <summary>A fresh Git-resolved read view, never a mutation authorization.</summary>
internal sealed class RepositoryReadConfiguration
{
    private static readonly Regex RiskKey = new("^(core\\.(fsmonitor|sshcommand|hookspath)|filter\\..*|diff\\.external|diff\\..*\\.command|credential\\..*|protocol\\..*\\.allow|remote\\..*\\.url)$", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> ConfigurationEnvironmentKeys = new(StringComparer.OrdinalIgnoreCase)
    { "HOME", "HOMEDRIVE", "HOMEPATH", "USERPROFILE", "XDG_CONFIG_HOME", "PROGRAMDATA", "GIT_EXEC_PATH", "GIT_DIR", "GIT_COMMON_DIR" };
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dependencies = new(GitAccess.CollisionComparer);
    private readonly string root;
    private readonly string executable;
    private readonly string home;
    private readonly string environmentStamp;
    public IReadOnlyList<KeyValuePair<string, string>> Entries { get; }
    public bool Cacheable { get; private set; } = true;
    public string Stamp { get; }

    public RepositoryReadConfiguration(string root, ProcessStartInfo start, byte[] output)
    {
        this.root = root; executable = start.FileName;
        environmentStamp = ConfigurationEnvironmentStamp(start);
        string? EnvironmentValue(string key) => start.Environment.TryGetValue(key, out var value) ? value : null;
        home = GitReadEnvironment.EffectiveHome(start) ?? "";
        string[] fields;
        try { fields = GitAccess.NullFields(output).ToArray(); }
        catch (System.Text.DecoderFallbackException)
        {
            // Git accepts opaque bytes in unrelated configuration values. Keep
            // the UI usable, but never feed a lossy snapshot to native reads.
            Cacheable = false;
            fields = System.Text.Encoding.UTF8.GetString(output).Split('\0');
            if (fields.Length > 0 && fields[^1].Length == 0) fields = fields[..^1];
        }
        if (fields.Length % 2 != 0) throw new FormatException("Truncated Git configuration origins.");
        var entries = new List<KeyValuePair<string, string>>();
        for (var index = 0; index < fields.Length; index += 2)
        {
            var origin = fields[index]; var field = fields[index + 1]; var split = field.IndexOf('\n');
            var key = split < 0 ? field : field[..split]; var value = split < 0 ? "" : field[(split + 1)..];
            entries.Add(new(key, value)); values[key] = value;
            string? source = null;
            if (origin.StartsWith("file:", StringComparison.Ordinal)) { source = Expand(origin[5..], root); if (source is not null) dependencies.Add(source); }
            else if (origin != "command line:") Cacheable = false;
            if (key.Equals("include.path", StringComparison.OrdinalIgnoreCase) || key.StartsWith("includeif.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".path", StringComparison.OrdinalIgnoreCase))
            {
                var included = Expand(value, source is null ? root : Path.GetDirectoryName(source)!);
                if (included is not null) dependencies.Add(included); else Cacheable = false;
            }
        }
        Entries = entries;
        // Include empty/missing conventional files as well as origins containing values.
        Add(".gitconfig", home);
        Add("git/config", EnvironmentValue("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"));
        if (OperatingSystem.IsWindows() && EnvironmentValue("PROGRAMDATA") is { Length: > 0 } programData) Add("Git/config", programData);
        if (start.Environment.TryGetValue("GIT_CONFIG_GLOBAL", out var global) && !string.IsNullOrEmpty(global)) Add(global, root);
        if (start.Environment.TryGetValue("GIT_CONFIG_SYSTEM", out var system) && !string.IsNullOrEmpty(system)) Add(system, root);
        var folder = new DirectoryInfo(Path.GetDirectoryName(executable)!);
        for (var level = 0; level < 4 && folder is not null; level++, folder = folder.Parent)
        { Add("etc/gitconfig", folder.FullName); Add("ucrt64/etc/gitconfig", folder.FullName); Add("mingw64/etc/gitconfig", folder.FullName); }
        Add(".git", root); Add(".git/HEAD", root); Add(".git/config", root); Add(".git/config.worktree", root);
        if (File.Exists(Path.Combine(root, ".git")))
        {
            var marker = File.ReadAllText(Path.Combine(root, ".git")).Trim();
            if (marker.StartsWith("gitdir: ", StringComparison.Ordinal))
            {
                var admin = Expand(marker[8..], root);
                if (admin is not null)
                {
                    Add("HEAD", admin); Add("config", admin); Add("config.worktree", admin); Add("commondir", admin);
                    var commonMarker = Path.Combine(admin, "commondir");
                    if (File.Exists(commonMarker) && Expand(File.ReadAllText(commonMarker).Trim(), admin) is { } common) Add("config", common);
                }
            }
            else Cacheable = false;
        }
        Stamp = Fingerprint();
    }

    public string? Get(string key) => values.GetValueOrDefault(key);
    public RepoFormat Format => new(Get("extensions.objectformat") ?? "sha1", Get("extensions.refstorage") ?? "files");
    public RepositoryTextSettings Text => new(Get("core.autocrlf") ?? "unset", Get("core.safecrlf") ?? "unset", Get("core.eol") ?? "unset", Get("i18n.commitencoding") ?? "UTF-8");
    public IReadOnlyList<string> Risky => Entries.Where(pair => RiskKey.IsMatch(pair.Key))
        .Select(pair => GitProcessRunner.Redact((pair.Key + "=" + pair.Value).Replace('\n', '='))).ToArray();
    public IReadOnlyList<RemoteInfo> Remotes => Entries.Where(pair => pair.Key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) && pair.Key.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        .Select(pair => new RemoteInfo(pair.Key[7..^4], GitProcessRunner.Redact(pair.Value))).DistinctBy(remote => remote.Name).ToArray();
    public bool IsCurrent(ProcessStartInfo start) => Cacheable && GitAccess.SamePath(executable, start.FileName) &&
        string.Equals(home, GitReadEnvironment.EffectiveHome(start) ?? "", StringComparison.Ordinal) &&
        environmentStamp == ConfigurationEnvironmentStamp(start) && Stamp == Fingerprint();
    private static string ConfigurationEnvironmentStamp(ProcessStartInfo start)
    {
        // Retain a digest, never configuration environment values (which may contain credentials).
        var fields = start.Environment.Where(pair => pair.Key.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase) ||
                ConfigurationEnvironmentKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => new[] { pair.Key, pair.Value ?? "" });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\0', fields))));
    }
    private void Add(string path, string relativeTo) { var value = Expand(path, relativeTo); if (value is not null) dependencies.Add(value); else Cacheable = false; }
    private string? Expand(string path, string relativeTo)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) path = Path.Combine(home, path[2..]);
        else if (path.StartsWith('~') || path.StartsWith("%(prefix)", StringComparison.Ordinal)) { Cacheable = false; return null; }
        try { return Path.GetFullPath(path, relativeTo); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { Cacheable = false; return null; }
    }
    private string Fingerprint() => string.Join(';', dependencies.Append(executable).Order(StringComparer.Ordinal).Select(path =>
    {
        var file = new FileInfo(path);
        return path + "=" + (file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing");
    }));
}
