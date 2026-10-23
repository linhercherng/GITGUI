using System.Security.Cryptography;
using System.Text;

namespace GitGui.Core;

/// <summary>Local files which can change object lookup or ancestry without moving a visible tip.</summary>
public sealed record RepositoryObjectView(string Fingerprint, bool RequiresCli, bool HasCommitGraph)
{
    public static RepositoryObjectView Read(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        var directory = Directory.Exists(dotGit) ? dotGit : File.Exists(dotGit)
            ? Path.GetFullPath(File.ReadAllText(dotGit).Trim()[8..], root) : root;
        var commonFile = Path.Combine(directory, "commondir");
        var common = File.Exists(commonFile) ? Path.GetFullPath(File.ReadAllText(commonFile).Trim(), directory) : directory;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string name, byte[] data)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name)); hash.AppendData([0]);
            hash.AppendData(data); hash.AppendData([0]);
        }
        bool ReadFile(string name, string path)
        {
            if (!File.Exists(path)) { Add(name, []); return false; }
            var bytes = File.ReadAllBytes(path); Add(name, bytes); return bytes.Length > 0;
        }
        ReadFile("config", Path.Combine(common, "config"));
        ReadFile("config.worktree", Path.Combine(directory, "config.worktree"));
        ReadFile("reftable", Path.Combine(common, "reftable", "tables.list"));
        var shallow = ReadFile("shallow", Path.Combine(common, "shallow"));
        var grafts = ReadFile("grafts", Path.Combine(common, "info", "grafts"));
        var replacements = false;
        var replaceDirectory = Path.Combine(common, "refs", "replace");
        if (Directory.Exists(replaceDirectory))
            foreach (var path in Directory.EnumerateFiles(replaceDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            { replacements = true; ReadFile(Path.GetRelativePath(replaceDirectory, path), path); }
        var packed = Path.Combine(common, "packed-refs");
        if (File.Exists(packed))
            foreach (var line in File.ReadLines(packed).Where(line => line.Contains(" refs/replace/", StringComparison.Ordinal)))
            { replacements = true; Add("packed-replace", Encoding.UTF8.GetBytes(line)); }
        var objects = Path.Combine(common, "objects", "info");
        var commitGraph = File.Exists(Path.Combine(objects, "commit-graph")) ||
            File.Exists(Path.Combine(objects, "commit-graphs", "commit-graph-chain"));
        Add("commit-graph-present", [commitGraph ? (byte)1 : (byte)0]);
        return new(Convert.ToHexString(hash.GetHashAndReset()), shallow || grafts || replacements, commitGraph);
    }
}
